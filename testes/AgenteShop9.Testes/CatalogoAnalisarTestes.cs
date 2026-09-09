using System.Text;
using AgenteShop9.Catalogos;

namespace AgenteShop9.Testes;

/// <summary>Validacoes estruturais de <see cref="Catalogo.Analisar"/> (Tarefa C2).</summary>
public class CatalogoAnalisarTestes
{
    private static byte[] Json(string periodoMaxDias) => Encoding.UTF8.GetBytes($$"""
        {
          "versao_catalogo": 99,
          "operacoes": {
            "x": {
              "sql": "SELECT 1",
              "parametros": [],
              "periodo_max_dias": {{periodoMaxDias}}
            }
          }
        }
        """);

    [Fact]
    public void Periodo_max_dias_zero_e_catalogo_invalido()
    {
        var ex = Assert.Throws<CatalogoInvalidoException>(() => Catalogo.Analisar(Json("0")));
        Assert.Contains("periodo_max_dias", ex.Message);
    }

    [Fact]
    public void Periodo_max_dias_negativo_e_catalogo_invalido()
    {
        Assert.Throws<CatalogoInvalidoException>(() => Catalogo.Analisar(Json("-5")));
    }

    [Fact]
    public void Periodo_max_dias_valido_parseia()
    {
        var cat = Catalogo.Analisar(Json("92"));
        Assert.Equal(92, cat.Operacoes!["x"].PeriodoMaxDias);
    }

    [Fact]
    public void Sem_periodo_max_dias_e_valido()
    {
        var json = Encoding.UTF8.GetBytes("""
            { "versao_catalogo": 99, "operacoes": { "x": { "sql": "SELECT 1", "parametros": [] } } }
            """);
        var cat = Catalogo.Analisar(json);
        Assert.Null(cat.Operacoes!["x"].PeriodoMaxDias);
    }
}
