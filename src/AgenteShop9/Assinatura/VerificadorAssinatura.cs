using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace AgenteShop9.Assinatura;

public readonly record struct ResultadoVerificacao(bool Valida, string? ComentarioConfiavel, string? Motivo)
{
    public static ResultadoVerificacao Ok(string comentario) => new(true, comentario, null);
    public static ResultadoVerificacao Falha(string motivo) => new(false, null, motivo);
}

/// <summary>
/// Verifica um artefato (catalogo.json ou binario do agente) contra sua
/// assinatura minisign, usando as chaves publicas embutidas. Porte de
/// ferramentas/assinatura/docs/ASSINATURA.md secao 5 (raiz Plurivo).
///
/// ISSUE #5 / spec 3.3.1 e 3.7: assinatura invalida = artefato DESCARTADO; o
/// agente segue com o catalogo/versao atual. Proibido pular esta verificacao ou
/// desligar qualquer uma das checagens.
/// </summary>
public static class VerificadorAssinatura
{
    /// <param name="conteudoArquivo">bytes crus do artefato assinado.</param>
    /// <param name="conteudoMinisig">texto completo do arquivo .minisig (4 linhas).</param>
    /// <param name="chavesPublicas">2a linha de cada .pub aceito; default: as embutidas.</param>
    public static ResultadoVerificacao Verificar(
        byte[] conteudoArquivo, string conteudoMinisig, IEnumerable<string>? chavesPublicas = null)
    {
        var chaves = (chavesPublicas ?? ChavesPublicas.Aceitas).ToArray();

        var linhas = conteudoMinisig.Replace("\r\n", "\n").Split('\n');
        if (linhas.Length < 4)
            return ResultadoVerificacao.Falha("arquivo .minisig com menos de 4 linhas.");

        byte[] sig;
        try { sig = Convert.FromBase64String(linhas[1].Trim()); }
        catch (FormatException) { return ResultadoVerificacao.Falha("linha 2 do .minisig nao e base64."); }
        if (sig.Length != 74) return ResultadoVerificacao.Falha("assinatura com tamanho invalido.");

        var algo = Encoding.ASCII.GetString(sig, 0, 2);
        var sigKeyId = sig.AsSpan(2, 8).ToArray();
        var assinatura = sig.AsSpan(10, 64).ToArray();

        const string prefixo = "trusted comment: ";
        if (!linhas[2].StartsWith(prefixo, StringComparison.Ordinal))
            return ResultadoVerificacao.Falha("linha 3 do .minisig sem 'trusted comment: '.");
        var comentario = linhas[2][prefixo.Length..];
        var comentarioBytes = Encoding.UTF8.GetBytes(comentario);

        byte[] assinaturaGlobal;
        try { assinaturaGlobal = Convert.FromBase64String(linhas[3].Trim()); }
        catch (FormatException) { return ResultadoVerificacao.Falha("linha 4 do .minisig nao e base64."); }
        if (assinaturaGlobal.Length != 64)
            return ResultadoVerificacao.Falha("assinatura global com tamanho invalido.");

        byte[] mensagem;
        if (algo == "ED") // prehashed (padrao do minisign)
        {
            var h = new byte[64];
            var blake = new Blake2bDigest(512);
            blake.BlockUpdate(conteudoArquivo, 0, conteudoArquivo.Length);
            blake.DoFinal(h, 0);
            mensagem = h;
        }
        else if (algo == "Ed") // bytes crus (formato legado, minisign -l)
        {
            mensagem = conteudoArquivo;
        }
        else
        {
            return ResultadoVerificacao.Falha($"algoritmo de assinatura desconhecido: '{algo}'.");
        }

        foreach (var chaveBase64 in chaves)
        {
            byte[] pub;
            try { pub = Convert.FromBase64String(chaveBase64.Trim()); }
            catch (FormatException) { continue; }
            if (pub.Length != 42) continue;

            var keyId = pub.AsSpan(2, 8).ToArray();
            if (!keyId.SequenceEqual(sigKeyId)) continue;

            // key id bateu: esta e a chave. O resultado dela e final.
            var chavePub = new Ed25519PublicKeyParameters(pub, 10);

            var v1 = new Ed25519Signer();
            v1.Init(false, chavePub);
            v1.BlockUpdate(mensagem, 0, mensagem.Length);
            if (!v1.VerifySignature(assinatura))
                return ResultadoVerificacao.Falha("assinatura do conteudo nao confere.");

            var global = new byte[assinatura.Length + comentarioBytes.Length];
            Buffer.BlockCopy(assinatura, 0, global, 0, assinatura.Length);
            Buffer.BlockCopy(comentarioBytes, 0, global, assinatura.Length, comentarioBytes.Length);

            var v2 = new Ed25519Signer();
            v2.Init(false, chavePub);
            v2.BlockUpdate(global, 0, global.Length);
            if (!v2.VerifySignature(assinaturaGlobal))
                return ResultadoVerificacao.Falha("assinatura do trusted comment nao confere.");

            return ResultadoVerificacao.Ok(comentario);
        }

        return ResultadoVerificacao.Falha(
            "nenhuma chave publica embutida corresponde ao key id da assinatura.");
    }
}
