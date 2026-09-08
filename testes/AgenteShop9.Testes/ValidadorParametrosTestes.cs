using System.Text.Json;
using AgenteShop9.Catalogos;
using AgenteShop9.Operacoes;

namespace AgenteShop9.Testes;

public class ValidadorParametrosTestes
{
    private static readonly Operacao Op = new()
    {
        Sql = "SELECT 1",
        Parametros =
        [
            new ParametroDeclarado { Nome = "CodigoFilial", Tipo = "inteiro", Obrigatorio = true },
            new ParametroDeclarado { Nome = "Nome", Tipo = "texto", Obrigatorio = false },
            new ParametroDeclarado { Nome = "EstoqueMinimo", Tipo = "decimal", Obrigatorio = false },
        ],
    };

    private static Dictionary<string, JsonElement> Params(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Ok_quando_obrigatorio_presente_e_tipos_batem()
    {
        var erro = ValidadorParametros.Validar(Op, Params(
            """{ "CodigoFilial": 1, "Nome": "parafuso", "EstoqueMinimo": 0.5 }"""));

        Assert.Null(erro);
    }

    [Fact]
    public void Ok_so_com_o_obrigatorio()
    {
        Assert.Null(ValidadorParametros.Validar(Op, Params("""{ "CodigoFilial": 7 }""")));
    }

    [Fact]
    public void Obrigatorio_ausente_e_parametro_invalido()
    {
        var erro = ValidadorParametros.Validar(Op, Params("""{ "Nome": "x" }"""));

        Assert.Equal("parametro_invalido", erro?.Codigo);
        Assert.Contains("CodigoFilial", erro?.Mensagem);
    }

    [Fact]
    public void Obrigatorio_nulo_conta_como_ausente()
    {
        var erro = ValidadorParametros.Validar(Op, Params("""{ "CodigoFilial": null }"""));

        Assert.Equal("parametro_invalido", erro?.Codigo);
    }

    [Fact]
    public void Tipo_errado_e_parametro_invalido()
    {
        var erro = ValidadorParametros.Validar(Op, Params(
            """{ "CodigoFilial": "um", "Nome": "x" }"""));

        Assert.Equal("parametro_invalido", erro?.Codigo);
        Assert.Contains("CodigoFilial", erro?.Mensagem);
    }

    [Fact]
    public void Parametro_nao_declarado_e_recusado()
    {
        var erro = ValidadorParametros.Validar(Op, Params(
            """{ "CodigoFilial": 1, "OrderBy": "prod.Nome DESC" }"""));

        Assert.Equal("parametro_invalido", erro?.Codigo);
        Assert.Contains("OrderBy", erro?.Mensagem);
    }
}
