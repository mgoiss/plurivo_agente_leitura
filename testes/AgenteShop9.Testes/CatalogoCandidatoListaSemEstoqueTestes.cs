using AgenteShop9.Catalogos;
using AgenteShop9.Operacoes;
using System.Text.Json.Nodes;

namespace AgenteShop9.Testes;

/// <summary>Issue #203. Somente o formato do candidato e o texto do SQL: não valida SQL Server nem assinatura.</summary>
public class CatalogoCandidatoListaSemEstoqueTestes
{
    private static Catalogo Candidato() =>
        Catalogo.Analisar(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "catalogo.candidato-lista-sem-estoque-v47.json")));

    [Theory]
    [InlineData("emissao_produtos_sem_estoque")]
    [InlineData("emissao_produtos_sem_estoque_v2")]
    public void Entrada_recente_conta_compra_e_transferencia_de_entrada_mas_nao_a_de_saida(string nome)
    {
        var operacao = Candidato().Operacoes![nome];
        foreach (var sql in new[] { operacao.Sql, operacao.SqlContagem })
        {
            Assert.Contains("m.Tipo_Operacao IN ('COM', 'TRE')", sql);
            Assert.DoesNotContain("'TRS'", sql);
            Assert.DoesNotContain("m.Tipo_Operacao = 'COM'", sql);
        }
    }

    [Fact]
    public void Vendas_da_origem_nao_exige_compra_transferencia_nem_estoque()
    {
        var operacao = Candidato().Operacoes!["emissao_produtos_vendas_origem_v2"];
        foreach (var sql in new[] { operacao.Sql, operacao.SqlContagem })
        {
            Assert.Contains("m.Tipo_Operacao = 'VND'", sql);
            Assert.Contains("m.Ordem_Filial = @OrdemFilialOrigem", sql);
            Assert.Contains("@DiasVendas", sql);
            Assert.DoesNotContain("'COM'", sql);
            Assert.DoesNotContain("'TRE'", sql);
            Assert.DoesNotContain("Qtde_Estoque_Atual >", sql);
            Assert.Contains("LEFT JOIN Estoque_Atual", sql);
        }
        var nomes = operacao.Parametros!.Select(p => p.Nome).ToList();
        Assert.Contains("OrdemFilialOrigem", nomes);
        Assert.Contains("OrdemFilialDestino", nomes);
        Assert.Contains("DiasVendas", nomes);
        Assert.DoesNotContain("DiasRecentes", nomes);
    }

    [Fact]
    public void Operacao_nova_monta_paginacao_sem_colunas_repetidas()
    {
        var operacao = Candidato().Operacoes!["emissao_produtos_vendas_origem_v2"];
        foreach (var chave in operacao.Ordenacao!.Colunas.Keys)
        {
            var comando = MontadorComando.Montar(operacao, new Dictionary<string, System.Text.Json.JsonElement>(), new PaginacaoResolvida { Pagina = 1, TamanhoPagina = 10, OrdenarPor = chave, Direcao = "asc", IncluirTotal = false });
            var colunas = comando.CommandText[comando.CommandText.IndexOf("ORDER BY")..].Replace("ORDER BY ", "").Split("OFFSET")[0].Split(',').Select(c => c.Trim().Replace(" ASC", "").Replace(" DESC", "")).ToList();
            Assert.Equal(colunas.Count, colunas.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void Candidato_so_acrescenta_a_operacao_nova_sobre_o_catalogo_em_uso()
    {
        // Base: v44, o catálogo em uso no campo (inclui as operações de fotos). Não o catalogo.json do repo.
        var baseV44 = Lido("catalogo.candidato-cache-validacao-fotos-v44.json");
        var candidato = Lido("catalogo.candidato-lista-sem-estoque-v47.json");
        Assert.True((int)candidato["versao_catalogo"]! > (int)baseV44["versao_catalogo"]!);

        var opsBase = baseV44["operacoes"]!.AsObject();
        var opsCandidato = candidato["operacoes"]!.AsObject();
        Assert.Equal(new[] { "emissao_produtos_vendas_origem_v2" },
            opsCandidato.Select(o => o.Key).Except(opsBase.Select(o => o.Key)).ToArray());
        Assert.Contains("catalogo_indice_fotos_metadados", opsCandidato.Select(o => o.Key));
        Assert.Contains("catalogo_foto_base64", opsCandidato.Select(o => o.Key));

        // Só as duas operações de TRE (entrada recente) podem diferir das do v44; as demais, inclusive as de fotos, idênticas.
        var alteradas = opsBase.Where(o => !JsonNode.DeepEquals(o.Value, opsCandidato[o.Key])).Select(o => o.Key).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "emissao_produtos_sem_estoque", "emissao_produtos_sem_estoque_v2" }, alteradas);
    }

    private static JsonNode Lido(string nome) =>
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", nome)))!;
}
