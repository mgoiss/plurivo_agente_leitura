using AgenteShop9.Catalogos;
using AgenteShop9.Operacoes;

namespace AgenteShop9.Testes;

/// <summary>Somente formato do candidato: não valida SQL Server nem assinatura.</summary>
public class CatalogoCandidatoConfiguracaoTestes
{
    [Fact]
    public void Candidato_parseia_e_monta_paginacao_sem_colunas_repetidas()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "catalogo.candidato-configuracao.json"));
        var catalogo = Catalogo.Analisar(bytes);
        Assert.Equal(27, catalogo.VersaoCatalogo);
        foreach (var nome in new[] { "emissao_cadastros_produtos", "emissao_produtos_disponiveis_v2", "emissao_produtos_sem_estoque_v2", "emissao_notas_itens_mes" })
        {
            var operacao = catalogo.Operacoes![nome];
            Assert.Contains("COUNT(*)", operacao.SqlContagem);
            foreach (var chave in operacao.Ordenacao!.Colunas.Keys)
            {
                var comando = MontadorComando.Montar(operacao, new Dictionary<string, System.Text.Json.JsonElement>(), new PaginacaoResolvida { Pagina = 1, TamanhoPagina = 10, OrdenarPor = chave, Direcao = "asc", IncluirTotal = false });
                var colunas = comando.CommandText[comando.CommandText.IndexOf("ORDER BY")..].Replace("ORDER BY ", "").Split("OFFSET")[0].Split(',').Select(c => c.Trim().Replace(" ASC", "").Replace(" DESC", "")).ToList();
                Assert.Equal(colunas.Count, colunas.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.Contains("OFFSET", comando.CommandText);
            }
        }
    }

    [Fact]
    public void Classes_de_imposto_do_cadastro_so_listam_saida_e_ativas()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "catalogo.candidato-configuracao.json"));
        var operacao = Catalogo.Analisar(bytes).Operacoes!["emissao_cadastros_produtos"];
        foreach (var sql in new[] { operacao.Sql, operacao.SqlContagem })
        {
            var bloco = sql!.Split('\n').Single(l => l.Contains("'classes_imposto'"));
            Assert.Contains("c.Tipo_Operacao = 'S'", bloco);
            Assert.Contains("c.Inativo = 0", bloco);
        }
    }

    [Fact]
    public void Candidato_tem_versao_maior_que_a_do_catalogo_em_uso()
    {
        var candidato = Catalogo.Analisar(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "catalogo.candidato-configuracao.json")));
        var emUso = Catalogo.Analisar(Fixtures.CatalogoRealBytes);
        Assert.True(candidato.VersaoCatalogo >= 27);
        Assert.True(candidato.VersaoCatalogo > emUso.VersaoCatalogo);
    }

    [Fact]
    public void Max_e_min_nao_se_aplicam_direto_a_documento_cancelado_sem_cast()
    {
        // SQL Server: MAX/MIN rejeitam bit (erro 8117). Exige CAST(... AS int) dentro do agregado.
        var candidato = Catalogo.Analisar(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "catalogo.candidato-configuracao.json")));
        var proibido = new System.Text.RegularExpressions.Regex(@"\b(MAX|MIN)\s*\(\s*(ISNULL\s*\(\s*)?(\w+\.)?Documento_Cancelado\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var (nome, operacao) in candidato.Operacoes!)
        {
            foreach (var sql in new[] { operacao.Sql, operacao.SqlContagem })
                Assert.False(sql is not null && proibido.IsMatch(sql), $"{nome}: MAX/MIN direto em Documento_Cancelado sem CAST");
        }
        var itens = candidato.Operacoes["emissao_notas_itens_mes"];
        Assert.Contains("MAX(CAST(ISNULL(Documento_Cancelado, 0) AS int)) = 0", itens.Sql);
        Assert.Contains("MAX(CAST(ISNULL(Documento_Cancelado, 0) AS int)) = 0", itens.SqlContagem);
    }

    [Fact]
    public void Vendas_por_dia_conta_vendas_sem_filtro_de_nfce_e_pagina_sem_coluna_repetida()
    {
        // Issue #83: histórico de vendas para distribuir a cota mensal. Não pode excluir venda já emitida.
        var operacao = Catalogo.Analisar(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "catalogo.candidato-configuracao.json"))).Operacoes!["emissao_vendas_por_dia"];
        Assert.Contains("COUNT(DISTINCT M.Sequencia) AS Qtd_Vendas", operacao.Sql);
        Assert.Contains("AS Data_Venda", operacao.Sql);
        Assert.DoesNotContain("Movimento_Documentos_Fiscais", operacao.Sql);
        Assert.DoesNotContain("ORDER BY", operacao.Sql);
        Assert.Contains("COUNT(*)", operacao.SqlContagem);
        Assert.DoesNotContain("ORDER BY", operacao.SqlContagem!);
        Assert.Equal(92, operacao.PeriodoMaxDias);
        foreach (var chave in operacao.Ordenacao!.Colunas.Keys)
        {
            var comando = MontadorComando.Montar(operacao, new Dictionary<string, System.Text.Json.JsonElement>(), new PaginacaoResolvida { Pagina = 0, TamanhoPagina = 10, OrdenarPor = chave, Direcao = "asc", IncluirTotal = false });
            var colunas = comando.CommandText[comando.CommandText.IndexOf("ORDER BY")..].Replace("ORDER BY ", "").Split("OFFSET")[0].Split(',').Select(c => c.Trim().Replace(" ASC", "").Replace(" DESC", "")).ToList();
            Assert.Equal(colunas.Count, colunas.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }
}
