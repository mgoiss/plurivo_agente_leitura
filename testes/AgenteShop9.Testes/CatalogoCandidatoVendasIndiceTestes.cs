using System.Text.Json;
using AgenteShop9.Catalogos;

namespace AgenteShop9.Testes;

/// <summary>
/// Candidato v37 da issue #125: o trecho de vendas le Movimento pelo indice de data
/// e entra a conferencia do carrinho. Nao assina nem publica nada.
/// </summary>
public class CatalogoCandidatoVendasIndiceTestes
{
    private const string Indice = "WITH (INDEX (CI_Filial_Estoque_CliFor))";
    private const string Conferencia = "catalogo_conferir_carrinho";
    private static readonly string[] ComIndice = ["catalogo_produtos_ids", "catalogo_inicio", "catalogo_relacionados"];

    private static Catalogo Ler(string arquivo) => Catalogo.Analisar(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "fixtures", arquivo)));

    [Fact]
    public void Versao_e_37()
    {
        Assert.Equal(37, Ler("catalogo.candidato-vendas-indice.json").VersaoCatalogo);
    }

    [Fact]
    public void So_o_trecho_de_vendas_ganha_a_dica_de_indice_e_o_resto_fica_identico_ao_v36()
    {
        var novas = Ler("catalogo.candidato-vendas-indice.json").Operacoes!;
        var anterior = Ler("catalogo.candidato-indice-fotos.json");
        Assert.Equal(36, anterior.VersaoCatalogo);
        foreach (var (nome, op) in anterior.Operacoes!)
        {
            Assert.Contains(nome, novas.Keys);
            if (!ComIndice.Contains(nome))
            {
                Assert.Equal(JsonSerializer.Serialize(op), JsonSerializer.Serialize(novas[nome]));
                continue;
            }
            Assert.Contains("FROM Movimento AS mov", op.Sql);
            Assert.Equal(op.Sql.Replace("FROM Movimento AS mov", "FROM Movimento AS mov " + Indice), novas[nome].Sql);
            Assert.Equal(JsonSerializer.Serialize(op with { Sql = "" }), JsonSerializer.Serialize(novas[nome] with { Sql = "" }));
        }
        Assert.Equal(anterior.Operacoes!.Count + 1, novas.Count);
    }

    [Fact]
    public void Conferencia_do_carrinho_e_select_puro_de_ate_24_itens()
    {
        var op = Ler("catalogo.candidato-vendas-indice.json").Operacoes![Conferencia];
        Assert.Equal(24, op.MaxLinhas);
        Assert.StartsWith("WITH Itens AS", op.Sql);
        foreach (var proibido in new[] { "INSERT ", "UPDATE ", "DELETE ", "EXEC", "INTO ", "DROP ", ";" })
            Assert.DoesNotContain(proibido, op.Sql, StringComparison.OrdinalIgnoreCase);
        var nomes = op.Parametros!.Select(p => p.Nome).ToHashSet();
        Assert.Contains("CodigoFilial", nomes);
        Assert.Contains("Tabela", nomes);
        for (var i = 1; i <= 24; i++)
        {
            Assert.Contains($"Produto_{i:00}", nomes);
            Assert.Contains($"Variacao_{i:00}", nomes);
        }
        for (var i = 1; i <= 8; i++)
            foreach (var tipo in new[] { "Classe", "Subclasse", "Familia" })
                Assert.Contains($"Ocultar{tipo}_{i:00}", nomes);
        Assert.Equal(2 + 48 + 24, nomes.Count);
        foreach (var obrigatorio in op.Parametros!.Where(p => p.Obrigatorio))
            Assert.Contains(obrigatorio.Nome, new[] { "CodigoFilial", "Tabela" });
    }
}
