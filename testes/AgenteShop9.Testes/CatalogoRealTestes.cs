using AgenteShop9.Catalogos;

namespace AgenteShop9.Testes;

/// <summary>O catálogo real da servidor piloto (catalogo/catalogo.json) parseia e valida.</summary>
public class CatalogoRealTestes
{
    [Fact]
    public void Catalogo_real_parseia_com_a_operacao_e_os_parametros()
    {
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);

        Assert.True(cat.VersaoCatalogo >= 28);
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

    // ---- paginacao: v16 padroniza toda operacao (issue #24) ----------------

    [Fact]
    public void Catalogo_real_e_toda_operacao_e_paginada()
    {
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);

        Assert.True(cat.VersaoCatalogo >= 28);

        // As 12 operacoes que a v16 padronizou continuam todas paginadas, com pagina maxima 200.
        // As operacoes do catalogo publico (catalogo_*) tem tamanhos de pagina proprios: ficam fora.
        string[] paginadas =
        [
            "listar_produtos_estoque", "saida_com_classificacao_pis_cofins_por_periodo",
            "financeiro_recebimentos_cartao_pix_em_aberto", "listar_filiais",
            "emissao_vendas_por_forma_pagamento", "emissao_vendas_por_dia",
            "emissao_produtos_disponiveis", "emissao_produtos_sem_estoque",
            "emissao_produtos_disponiveis_v2", "emissao_produtos_sem_estoque_v2",
            "emissao_cadastros_produtos", "emissao_notas_itens_mes",
        ];
        foreach (var op in paginadas.Select(nome => cat.Operacoes![nome]))
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

            Assert.InRange(op.TamanhoPaginaPadrao, 1, 200);
            Assert.Equal(200, op.TamanhoPaginaMax);
        }
    }

    [Fact]
    public void Nenhuma_operacao_gera_order_by_com_coluna_repetida()
    {
        // Reproduz o erro 169 do SQL Server pego na piloto: monta o ORDER BY de
        // cada operacao para cada coluna ordenavel e confere que nao repete.
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);

        foreach (var (_, op) in cat.Operacoes!.Where(o => o.Value.Ordenacao is not null))
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

    // ---- v17: Etapa 5, Fase 1 (issue #32) ----------------------------------

    [Fact]
    public void Catalogo_real_tem_financeiro_recebimentos_com_filial_e_periodo()
    {
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);

        Assert.True(cat.Operacoes!.TryGetValue(
            "financeiro_recebimentos_cartao_pix_em_aberto", out var op));

        Assert.Contains("Financeiro_Contas", op!.Sql);
        Assert.Contains("Pagar_Receber = 'R'", op.Sql);
        Assert.Contains("fin.Parcela_Descricao", op.Sql);   // ex.: "1/10"
        // Situacao e opcional e aceita varios codigos separados por virgula
        // (ex.: "A,Q") — sem STRING_SPLIT porque a piloto roda em compat 110.
        const string clausulaSituacao =
            "(@Situacao IS NULL OR ',' + @Situacao + ',' LIKE '%,' + fin.Situacao + ',%')";
        Assert.Contains(clausulaSituacao, op.Sql);
        Assert.DoesNotContain("COALESCE(@Situacao", op.Sql);
        Assert.DoesNotContain("STRING_SPLIT", op.Sql);
        Assert.Equal(31, op.PeriodoMaxDias);

        var obrigatorios = op.Parametros.Where(p => p.Obrigatorio).Select(p => p.Nome).ToHashSet();
        Assert.Equal(new HashSet<string> { "CodigoFilial", "inicio", "fim" }, obrigatorios);
        var situacao = op.Parametros.Single(p => p.Nome == "Situacao");
        Assert.Equal("texto", situacao.Tipo);
        Assert.False(situacao.Obrigatorio);

        Assert.Equal(["fin.Ordem"], op.Desempate);   // PK: desempate estavel
        Assert.Contains("Financeiro_Contas", op.SqlContagem);
        Assert.Contains(clausulaSituacao, op.SqlContagem!);
        Assert.DoesNotContain("ORDER BY", op.SqlContagem!);
    }

    [Fact]
    public void Catalogo_real_tem_listar_filiais_sem_parametro()
    {
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);

        Assert.True(cat.Operacoes!.TryGetValue("listar_filiais", out var op));

        Assert.Contains("FROM Filiais", op!.Sql);
        Assert.Empty(op.Parametros);
        Assert.Equal(
            new HashSet<string> { "codigo", "nome" },
            op.Ordenacao!.Colunas.Keys.ToHashSet());
        Assert.Equal(["Ordem"], op.Desempate);
    }

    [Fact]
    public void Catalogo_real_tem_vendas_emissao_com_filial_periodo_e_paginacao_estavel()
    {
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);
        var op = cat.Operacoes!["emissao_vendas_por_forma_pagamento"];

        Assert.Contains("Financeiro_Formas_Recebimento_Pagamento_V2", op.Sql);
        Assert.Contains("Data_Efetivado_Estoque", op.Sql);
        Assert.Contains("Documento_Cancelado", op.Sql);
        Assert.Contains("C.Agrupamento", op.Sql);
        Assert.DoesNotContain("ORDER BY", op.Sql);
        Assert.Contains("COUNT(*)", op.SqlContagem);
        Assert.Equal(92, op.PeriodoMaxDias);
        Assert.Equal(100, op.TamanhoPaginaPadrao);
        Assert.Equal(6, op.Desempate.Count);
        Assert.Equal(new HashSet<string> { "CodigoFilial", "inicio", "fim" },
            op.Parametros.Where(p => p.Obrigatorio).Select(p => p.Nome).ToHashSet());
    }

    [Fact]
    public void Catalogo_real_tem_as_operacoes_de_produtos_para_emissao()
    {
        var cat = Catalogo.Analisar(Fixtures.CatalogoRealBytes);

        Assert.True(cat.Operacoes!.TryGetValue("emissao_produtos_disponiveis", out var disponiveis));
        Assert.Contains("@ClasseImposto IS NULL OR cla.Nome LIKE @ClasseImposto", disponiveis!.Sql);
        Assert.Contains("SELECT COUNT(*) FROM (", disponiveis.SqlContagem);
        Assert.Equal("inteiro", disponiveis.Parametros.Single(p => p.Nome == "CodigoFilialEstoque").Tipo);
        Assert.Equal("texto", disponiveis.Parametros.Single(p => p.Nome == "ClasseImposto").Tipo);
        Assert.False(disponiveis.Parametros.Single(p => p.Nome == "ClasseImposto").Obrigatorio);

        Assert.True(cat.Operacoes.TryGetValue("emissao_produtos_sem_estoque", out var semEstoque));
        Assert.Contains("m.Tipo_Operacao = 'COM'", semEstoque!.Sql);
        Assert.Equal("inteiro", semEstoque.Parametros.Single(p => p.Nome == "DiasRecentes").Tipo);
    }
}
