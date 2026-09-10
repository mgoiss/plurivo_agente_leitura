using AgenteShop9.Catalogos;

namespace AgenteShop9.Testes;

/// <summary>O catálogo real da servidor piloto (catalogo/catalogo.json) parseia e valida.</summary>
public class CatalogoRealTestes
{
    [Fact]
    public void Catalogo_real_parseia_com_a_operacao_e_os_parametros()
    {
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);

        Assert.Equal(16, cat.VersaoCatalogo);
        Assert.NotNull(cat.Operacoes);
        Assert.True(cat.Operacoes!.TryGetValue("listar_produtos_estoque", out var op));

        Assert.Contains("Prod_Serv", op!.Sql);
        Assert.Equal(10_000, op.MaxLinhas);
        Assert.Equal(30_000, op.TimeoutMs);
        Assert.Null(op.PeriodoMaxDias);          // operacao sem faixa de datas

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

        // SQL parametrizado, com o agrupamento.
        Assert.Contains("Movimento", op!.Sql);
        Assert.Contains("GROUP BY", op.Sql);
        Assert.Equal(10_000, op.MaxLinhas);
        Assert.Equal(30_000, op.TimeoutMs);

        // v15: janela maxima do periodo + RTRIM no nome da classe de imposto
        // (o Shop9 guarda esse campo com espaco a direita).
        Assert.Equal(92, op.PeriodoMaxDias);
        Assert.Contains("RTRIM(clas.Nome) AS NomeClasse", op.Sql);
        Assert.Contains("cfop.Codigo, RTRIM(clas.Nome),", op.Sql);   // tambem no GROUP BY

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

    // ---- paginacao: v16 padroniza as duas operacoes (issue #24) -----------

    [Fact]
    public void Catalogo_real_v16_e_as_duas_operacoes_sao_paginadas()
    {
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);

        Assert.Equal(16, cat.VersaoCatalogo);
        Assert.Equal(2, cat.Operacoes!.Count);

        foreach (var (_, op) in cat.Operacoes)
        {
            // SQL escrito SEM TOP e SEM ORDER BY final — o agente cola os dois.
            Assert.DoesNotContain("TOP (@max_linhas", op.Sql);
            Assert.DoesNotContain("@max_linhas", op.Sql);
            Assert.DoesNotContain("ORDER BY", op.Sql);

            // O trio da paginacao, completo.
            Assert.NotNull(op.Ordenacao);
            Assert.NotEmpty(op.Ordenacao!.Colunas);
            Assert.Contains(op.Ordenacao.Padrao.Coluna, op.Ordenacao.Colunas.Keys);
            Assert.NotEmpty(op.Desempate);
            Assert.DoesNotContain(op.Desempate, string.IsNullOrWhiteSpace);
            Assert.Contains("COUNT(*)", op.SqlContagem);
            Assert.DoesNotContain("ORDER BY", op.SqlContagem!);

            Assert.Equal(50, op.TamanhoPaginaPadrao);
            Assert.Equal(200, op.TamanhoPaginaMax);
        }
    }

    [Fact]
    public void Nenhuma_operacao_gera_order_by_com_coluna_repetida()
    {
        // Reproduz o erro 169 do SQL Server pego na piloto: monta o ORDER BY de
        // cada operacao para cada coluna ordenavel e confere que nao repete.
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);

        foreach (var (_, op) in cat.Operacoes!)
        foreach (var chave in op.Ordenacao!.Colunas.Keys)
        {
            var cmd = Operacoes.MontadorComando.Montar(
                op,
                new Dictionary<string, System.Text.Json.JsonElement>(),
                new Operacoes.PaginacaoResolvida
                {
                    Pagina = 0, TamanhoPagina = 10, OrdenarPor = chave,
                    Direcao = "asc", IncluirTotal = false,
                });

            var clausula = cmd.CommandText[cmd.CommandText.IndexOf("ORDER BY")..];
            var colunas = clausula
                .Replace("ORDER BY ", "").Split("OFFSET")[0]
                .Split(',')
                .Select(t => t.Trim().Replace(" ASC", "").Replace(" DESC", "").Trim())
                .ToList();

            Assert.Equal(colunas.Count, colunas.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void Listar_produtos_ordena_por_codigo_nome_estoque_e_desempata_pela_pk()
    {
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);
        var op = cat.Operacoes!["listar_produtos_estoque"];

        Assert.Equal(
            new HashSet<string> { "codigo", "nome", "estoque" },
            op.Ordenacao!.Colunas.Keys.ToHashSet());
        Assert.Equal("prod.Codigo", op.Ordenacao.Colunas["codigo"]);
        Assert.Equal(["prod.Ordem"], op.Desempate);   // PK: desempate estavel
    }

    [Fact]
    public void Saidas_por_periodo_desempata_pela_tupla_inteira_do_group_by()
    {
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);
        var op = cat.Operacoes!["saida_com_classificacao_pis_cofins_por_periodo"];

        // Sem PK unica (tem GROUP BY): o desempate e a tupla inteira do agrupamento.
        Assert.Contains("clasOp.PIS_COFINS_CST", op.Desempate);
        foreach (var coluna in op.Ordenacao!.Colunas.Values)
            Assert.Contains(op.Desempate, d => d == coluna);   // ordenavel => coberto pelo desempate
    }
}
