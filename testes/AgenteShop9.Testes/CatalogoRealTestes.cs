using AgenteShop9.Catalogos;

namespace AgenteShop9.Testes;

/// <summary>O catálogo real da loja piloto (catalogo/catalogo.json) parseia e valida.</summary>
public class CatalogoRealTestes
{
    [Fact]
    public void Catalogo_real_parseia_com_a_operacao_e_os_parametros()
    {
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);

        Assert.Equal(13, cat.VersaoCatalogo);
        Assert.NotNull(cat.Operacoes);
        Assert.True(cat.Operacoes!.TryGetValue("listar_produtos_estoque", out var op));

        Assert.Contains("Prod_Serv", op!.Sql);
        Assert.Contains("@max_linhas", op.Sql);
        Assert.Equal(10_000, op.MaxLinhas);
        Assert.Equal(30_000, op.TimeoutMs);

        var filial = op.Parametros.Single(p => p.Nome == "CodigoFilial");
        Assert.Equal("inteiro", filial.Tipo);
        Assert.True(filial.Obrigatorio);

        Assert.All(op.Parametros.Where(p => p.Nome != "CodigoFilial"), p => Assert.False(p.Obrigatorio));
        Assert.Equal("texto", op.Parametros.Single(p => p.Nome == "Codigo").Tipo);
        Assert.Equal("decimal", op.Parametros.Single(p => p.Nome == "EstoqueMinimo").Tipo);
    }
}
