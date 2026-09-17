using System.Security.Cryptography;
using AgenteShop9.Assinatura;
using AgenteShop9.Configuracao;
using Microsoft.Extensions.Logging;

namespace AgenteShop9.Catalogos;

/// <summary>
/// Cuida do catalogo no agente: verifica a assinatura, aplica o anti-retrocesso
/// e guarda o catalogo aceito. Itens 1.3 e 1.4 do roteiro.
///
/// ISSUE #5 / spec 3.3.1: so aceita catalogo com assinatura minisign valida,
/// SHA-256 conferido, `versao=` do trusted comment igual ao `versao_catalogo`
/// do JSON, e `versao_catalogo` MAIOR que a do catalogo ja aceito.
/// </summary>
public sealed class GestorCatalogo
{
    /// <summary>Assinatura para o construtor: bytes do arquivo + texto do .minisig.</summary>
    public delegate ResultadoVerificacao Verificador(byte[] conteudo, string minisig);

    private readonly ILogger<GestorCatalogo> _log;
    private readonly Verificador _verificar;
    private readonly string _caminhoCandidato;
    private readonly string _caminhoAceito;

    public GestorCatalogo(ILogger<GestorCatalogo> log, ConfiguracaoAgente config, Verificador? verificar = null)
    {
        _log = log;
        _verificar = verificar ?? ((c, m) => VerificadorAssinatura.Verificar(c, m));
        _caminhoCandidato = config.CatalogoCaminho;
        _caminhoAceito = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(config.CatalogoCaminho)) ?? ".",
            "dados", "catalogo.json");
    }

    /// <summary>O catalogo aceito agora (operacoes disponiveis). null se nao ha.</summary>
    public Catalogo? CatalogoAceito { get; private set; }

    /// <summary>versao_catalogo aceita agora; 0 se nao ha catalogo valido.</summary>
    public int VersaoAceita => CatalogoAceito?.VersaoCatalogo ?? 0;

    /// <summary>
    /// Roda uma vez na subida: carrega o catalogo aceito guardado (re-verificando,
    /// para pegar adulteracao em disco) e, se houver um candidato local mais novo
    /// e valido, promove-o.
    /// </summary>
    public void Inicializar()
    {
        CatalogoAceito = CarregarAceitoGuardado();
        TentarPromoverCandidato();
        _log.LogInformation("Catalogo em uso: versao {Versao}.", VersaoAceita);
    }

    /// <summary>
    /// Reavalia o candidato em disco (item 4.1: chamado pelo <see cref="BaixadorCatalogo"/>
    /// depois de gravar um candidato novo baixado do backend). Mesma logica da
    /// subida — so promove se passar assinatura + SHA-256 + anti-retrocesso.
    /// </summary>
    public void ReavaliarCandidato() => TentarPromoverCandidato();

    private Catalogo? CarregarAceitoGuardado()
    {
        var r = Avaliar(_caminhoAceito, versaoMinimaExclusiva: 0);
        if (r is null) return null;
        if (!r.Value.aceito)
        {
            _log.LogWarning(
                "Catalogo aceito guardado esta invalido ({Motivo}); recomecando sem catalogo.",
                r.Value.motivo);
            return null;
        }
        return r.Value.catalogo;
    }

    private void TentarPromoverCandidato()
    {
        var r = Avaliar(_caminhoCandidato, versaoMinimaExclusiva: VersaoAceita);
        if (r is null) return; // nao ha candidato local — normal (o download e o item 4.1)
        if (!r.Value.aceito)
        {
            _log.LogWarning(
                "Catalogo candidato recusado: {Motivo}. Mantendo a versao {Atual}.",
                r.Value.motivo, VersaoAceita);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_caminhoAceito)!);
        File.Copy(_caminhoCandidato, _caminhoAceito, overwrite: true);
        File.Copy(_caminhoCandidato + ".minisig", _caminhoAceito + ".minisig", overwrite: true);
        CatalogoAceito = r.Value.catalogo;
        _log.LogInformation("Catalogo versao {Versao} aceito e guardado.", VersaoAceita);
    }

    /// <summary>
    /// null = o arquivo (ou o .minisig) nao existe. Senao, roda todas as
    /// checagens: assinatura, SHA-256, `versao=` == `versao_catalogo`, e
    /// `versao_catalogo` &gt; versaoMinimaExclusiva (anti-retrocesso).
    /// </summary>
    private (bool aceito, int versao, string motivo, Catalogo? catalogo)? Avaliar(
        string caminho, int versaoMinimaExclusiva)
    {
        var caminhoSig = caminho + ".minisig";
        if (!File.Exists(caminho) || !File.Exists(caminhoSig)) return null;

        var conteudo = File.ReadAllBytes(caminho);
        var minisig = File.ReadAllText(caminhoSig);

        // ISSUE #5 / spec 3.3.1 e 3.7: as tres checagens abaixo sao a barreira
        // contra um backend comprometido empurrar catalogo adulterado. Nenhuma
        // delas pode ser afrouxada ou removida.
        var assin = _verificar(conteudo, minisig);
        if (!assin.Valida)
            return (false, 0, $"assinatura invalida: {assin.Motivo}", null);

        var comentario = ComentarioConfiavel.Analisar(assin.ComentarioConfiavel!);

        var sha = Convert.ToHexStringLower(SHA256.HashData(conteudo));
        if (!string.Equals(sha, comentario.Sha256, StringComparison.OrdinalIgnoreCase))
            return (false, 0, "SHA-256 do arquivo nao bate com o sha256= do trusted comment", null);

        Catalogo cat;
        try { cat = Catalogo.Analisar(conteudo); }
        catch (CatalogoInvalidoException ex) { return (false, 0, ex.Message, null); }

        if (comentario.Versao is null || comentario.Versao != cat.VersaoCatalogo)
            return (false, 0,
                $"versao= do trusted comment ({comentario.Versao?.ToString() ?? "ausente"}) " +
                $"difere do versao_catalogo do JSON ({cat.VersaoCatalogo})", null);

        // ISSUE #5: anti-retrocesso. `<=` (nao `<`): reaceitar a MESMA versao ja
        // abriria a porta para reenviar uma versao antiga com defeito conhecido.
        if (cat.VersaoCatalogo <= versaoMinimaExclusiva)
            return (false, cat.VersaoCatalogo,
                $"anti-retrocesso: versao {cat.VersaoCatalogo} <= {versaoMinimaExclusiva} em uso", null);

        return (true, cat.VersaoCatalogo, "ok", cat);
    }
}
