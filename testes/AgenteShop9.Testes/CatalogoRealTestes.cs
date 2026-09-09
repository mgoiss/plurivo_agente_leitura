using AgenteShop9.Catalogos;

namespace AgenteShop9.Testes;

/// <summary>O catálogo real da loja piloto (catalogo/catalogo.json) parseia e valida.</summary>
public class CatalogoRealTestes
{
    [Fact]
    public void Catalogo_real_parseia_com_a_operacao_e_os_parametros()
    {
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);

        Assert.Equal(14, cat.VersaoCatalogo);
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

    [Fact]
    public void Catalogo_real_tem_a_operacao_de_saidas_por_periodo()
    {
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);

        Assert.True(cat.Operacoes!.TryGetValue(
            "saida_com_classificacao_pis_cofins_por_periodo", out var op));

        // SQL parametrizado, com o teto de linhas e o agrupamento.
        Assert.Contains("Movimento", op!.Sql);
        Assert.Contains("@max_linhas", op.Sql);
        Assert.Contains("GROUP BY", op.Sql);
        Assert.Equal(10_000, op.MaxLinhas);
        Assert.Equal(30_000, op.TimeoutMs);

        // Obrigatórios: filial + as duas datas do período.
        var obrigatorios = op.Parametros.Where(p => p.Obrigatorio).Select(p => p.Nome).ToHashSet();
        Assert.Equal(new HashSet<string> { "CodigoFilial", "inicio", "fim" }, obrigatorios);

        Assert.Equal("inteiro", op.Parametros.Single(p => p.Nome == "CodigoFilial").Tipo);
        Assert.Equal("data", op.Parametros.Single(p => p.Nome == "inicio").Tipo);
        Assert.Equal("data", op.Parametros.Single(p => p.Nome == "fim").Tipo);

        // Opcionais de texto.
        foreach (var nome in new[] { "Codigo", "TipoOperacao", "BaseData" })
        {
            var p = op.Parametros.Single(x => x.Nome == nome);
            Assert.Equal("texto", p.Tipo);
            Assert.False(p.Obrigatorio);
        }
    }
}
