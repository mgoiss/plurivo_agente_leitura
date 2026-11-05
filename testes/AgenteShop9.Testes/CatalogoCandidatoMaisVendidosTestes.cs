using System.Text.Json;
using AgenteShop9.Catalogos;

namespace AgenteShop9.Testes;

/// <summary>
/// Candidato v39 da issue #133: a CTE Vendas de catalogo_produtos_ids e unida por hash,
/// para o estimador antigo (compatibility_level 110) nao a recalcule por produto.
/// Nao assina nem publica nada.
/// </summary>
public class CatalogoCandidatoMaisVendidosTestes
{
    private const string Antes = "LEFT JOIN Vendas AS venda ON";
    private const string Depois = "LEFT HASH JOIN Vendas AS venda ON";

    private static Catalogo Ler(string arquivo) => Catalogo.Analisar(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "fixtures", arquivo)));

    [Fact]
    public void Versao_e_39()
    {
        Assert.Equal(39, Ler("catalogo.candidato-mais-vendidos.json").VersaoCatalogo);
    }

    [Fact]
    public void So_catalogo_produtos_ids_muda_e_so_o_join_das_vendas()
    {
        var novas = Ler("catalogo.candidato-mais-vendidos.json").Operacoes!;
        var anterior = Ler("catalogo-real.json");
        Assert.Equal(38, anterior.VersaoCatalogo);
        Assert.Equal(anterior.Operacoes!.Count, novas.Count);
        foreach (var (nome, op) in anterior.Operacoes!)
        {
            if (nome != "catalogo_produtos_ids")
            {
                Assert.Equal(JsonSerializer.Serialize(op), JsonSerializer.Serialize(novas[nome]));
                continue;
            }
            Assert.Contains(Antes, op.Sql);
            Assert.Equal(op.Sql.Replace(Antes, Depois), novas[nome].Sql);
            Assert.Equal(JsonSerializer.Serialize(op with { Sql = "" }), JsonSerializer.Serialize(novas[nome] with { Sql = "" }));
        }
    }
}
