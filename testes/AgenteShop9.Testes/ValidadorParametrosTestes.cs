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

    // Operacao com faixa de datas (periodo_max_dias) — espelha a fiscal do catalogo.
    private static readonly Operacao OpFaixa = new()
    {
        Sql = "SELECT 1",
        PeriodoMaxDias = 92,
        Parametros =
        [
            new ParametroDeclarado { Nome = "CodigoFilial", Tipo = "inteiro", Obrigatorio = true },
            new ParametroDeclarado { Nome = "inicio", Tipo = "data", Obrigatorio = true },
            new ParametroDeclarado { Nome = "fim", Tipo = "data", Obrigatorio = true },
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

    // ---- faixa de datas (periodo_max_dias, Tarefa C2) --------------------

    [Fact]
    public void Faixa_dentro_do_limite_passa()
    {
        // 2026-01-01 -> 2026-04-01 = 90 dias
        Assert.Null(ValidadorParametros.Validar(OpFaixa, Params(
            """{ "CodigoFilial": 1, "inicio": "2026-01-01", "fim": "2026-04-01" }""")));
    }

    [Fact]
    public void Faixa_de_exatamente_92_dias_passa()
    {
        // 2026-01-01 -> 2026-04-03 = 92 dias
        Assert.Null(ValidadorParametros.Validar(OpFaixa, Params(
            """{ "CodigoFilial": 1, "inicio": "2026-01-01", "fim": "2026-04-03" }""")));
    }

    [Fact]
    public void Faixa_de_93_dias_e_recusada()
    {
        // 2026-01-01 -> 2026-04-04 = 93 dias
        var erro = ValidadorParametros.Validar(OpFaixa, Params(
            """{ "CodigoFilial": 1, "inicio": "2026-01-01", "fim": "2026-04-04" }"""));

        Assert.Equal("parametro_invalido", erro?.Codigo);
        Assert.Contains("92", erro?.Mensagem);
    }

    [Fact]
    public void Faixa_de_200_dias_e_recusada()
    {
        var erro = ValidadorParametros.Validar(OpFaixa, Params(
            """{ "CodigoFilial": 1, "inicio": "2026-01-01", "fim": "2026-07-20" }"""));

        Assert.Equal("parametro_invalido", erro?.Codigo);
        Assert.Contains("periodo", erro?.Mensagem);
    }

    [Fact]
    public void Fim_antes_do_inicio_e_recusado()
    {
        var erro = ValidadorParametros.Validar(OpFaixa, Params(
            """{ "CodigoFilial": 1, "inicio": "2026-04-01", "fim": "2026-01-01" }"""));

        Assert.Equal("parametro_invalido", erro?.Codigo);
        Assert.Contains("fim", erro?.Mensagem);
        Assert.Contains("inicio", erro?.Mensagem);
    }

    [Fact]
    public void Hora_nas_pontas_nao_muda_a_contagem_de_dias()
    {
        // 92 dias de calendario, mas com hora no inicio e no fim: passa.
        Assert.Null(ValidadorParametros.Validar(OpFaixa, Params(
            """{ "CodigoFilial": 1, "inicio": "2026-01-01T00:00:00", "fim": "2026-04-03T23:59:59" }""")));

        // 93 dias de calendario, mesmo com fim de madrugada: recusa.
        var erro = ValidadorParametros.Validar(OpFaixa, Params(
            """{ "CodigoFilial": 1, "inicio": "2026-01-01T22:00:00", "fim": "2026-04-04T01:00:00" }"""));
        Assert.Equal("parametro_invalido", erro?.Codigo);
    }

    [Fact]
    public void Operacao_sem_periodo_max_dias_nao_checa_faixa()
    {
        // Op nao declara periodo_max_dias; 200 dias entre duas datas quaisquer passa.
        var op = new Operacao
        {
            Sql = "SELECT 1",
            Parametros =
            [
                new ParametroDeclarado { Nome = "inicio", Tipo = "data", Obrigatorio = false },
                new ParametroDeclarado { Nome = "fim", Tipo = "data", Obrigatorio = false },
            ],
        };

        Assert.Null(ValidadorParametros.Validar(op, Params(
            """{ "inicio": "2026-01-01", "fim": "2026-07-20" }""")));
    }
}
