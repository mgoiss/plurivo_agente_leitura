using AgenteShop9.Catalogos;

namespace AgenteShop9.Testes;

public class ComentarioConfiavelTestes
{
    [Fact]
    public void Analisa_o_comentario_do_exemplo()
    {
        var c = ComentarioConfiavel.Analisar(
            "arquivo=catalogo.json " +
            "sha256=7b390ced8651a8be4c1d9abff432f92dd5b1fe4ca1f98c1261d6d29c34129e32 " +
            "data=2026-09-07T15:22:52Z versao=12");

        Assert.Equal("catalogo.json", c.Arquivo);
        Assert.Equal("7b390ced8651a8be4c1d9abff432f92dd5b1fe4ca1f98c1261d6d29c34129e32", c.Sha256);
        Assert.Equal("2026-09-07T15:22:52Z", c.Data);
        Assert.Equal(12, c.Versao);
    }

    [Fact]
    public void Versao_ausente_fica_null()
    {
        var c = ComentarioConfiavel.Analisar("arquivo=x sha256=y data=z");

        Assert.Null(c.Versao);
        Assert.Equal("x", c.Arquivo);
    }
}
