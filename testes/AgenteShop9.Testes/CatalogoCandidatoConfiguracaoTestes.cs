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
        Assert.Equal(25, catalogo.VersaoCatalogo);
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
}
