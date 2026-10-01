using AgenteShop9.Assinatura;

namespace AgenteShop9.Testes;

/// <summary>
/// Verificação minisign contra o par arquivo/assinatura REAL de
/// testes/fixtures/ (mesmo que o `minisign -V` e o `verificar.sh` aprovam).
/// </summary>
public class VerificadorAssinaturaTestes
{
    [Fact]
    public void Exemplo_real_e_valido()
    {
        var r = VerificadorAssinatura.Verificar(Fixtures.CatalogoBytes, Fixtures.CatalogoMinisig);

        Assert.True(r.Valida, r.Motivo);
        Assert.Contains("versao=12", r.ComentarioConfiavel);
        Assert.Contains("sha256=", r.ComentarioConfiavel);
    }

    [Fact]
    public void Conteudo_adulterado_e_recusado()
    {
        var bytes = Fixtures.CatalogoBytes;
        bytes[0] ^= 0xFF;

        var r = VerificadorAssinatura.Verificar(bytes, Fixtures.CatalogoMinisig);

        Assert.False(r.Valida);
    }

    [Fact]
    public void Assinatura_adulterada_e_recusada()
    {
        var linhas = Fixtures.CatalogoMinisig.Replace("\r\n", "\n").Split('\n');
        var l2 = linhas[1].ToCharArray();
        l2[10] = l2[10] == 'A' ? 'B' : 'A';
        linhas[1] = new string(l2);

        var r = VerificadorAssinatura.Verificar(Fixtures.CatalogoBytes, string.Join('\n', linhas));

        Assert.False(r.Valida);
    }

    [Fact]
    public void Trusted_comment_adulterado_e_recusado()
    {
        var linhas = Fixtures.CatalogoMinisig.Replace("\r\n", "\n").Split('\n');
        linhas[2] = linhas[2].Replace("versao=12", "versao=99");

        var r = VerificadorAssinatura.Verificar(Fixtures.CatalogoBytes, string.Join('\n', linhas));

        Assert.False(r.Valida);
        Assert.Contains("trusted comment", r.Motivo);
    }

    [Fact]
    public void Chave_publica_com_outro_key_id_e_recusada()
    {
        var pub = Convert.FromBase64String(ChavesPublicas.Aceitas[0]);
        pub[3] ^= 0xFF; // muda o key id, mantendo o formato válido
        var pubFalsa = Convert.ToBase64String(pub);

        var r = VerificadorAssinatura.Verificar(
            Fixtures.CatalogoBytes, Fixtures.CatalogoMinisig, [pubFalsa]);

        Assert.False(r.Valida);
        Assert.Contains("nenhuma chave", r.Motivo);
    }

    [Fact]
    public void Minisig_com_menos_de_4_linhas_e_recusado()
    {
        var r = VerificadorAssinatura.Verificar(Fixtures.CatalogoBytes, "linha1\nlinha2");

        Assert.False(r.Valida);
    }
}
