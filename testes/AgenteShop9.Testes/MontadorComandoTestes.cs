using System.Data;
using System.Text.Json;
using AgenteShop9.Catalogos;
using AgenteShop9.Operacoes;

namespace AgenteShop9.Testes;

public class MontadorComandoTestes
{
    private static readonly Operacao Op = new()
    {
        Sql = "SELECT TOP (@max_linhas + 1) * FROM Prod_Serv WHERE (@Codigo IS NULL OR Codigo = @Codigo)",
        MaxLinhas = 5000,
        TimeoutMs = 20_000,
        Parametros =
        [
            new ParametroDeclarado { Nome = "CodigoFilial", Tipo = "inteiro", Obrigatorio = true },
            new ParametroDeclarado { Nome = "Codigo", Tipo = "texto", Obrigatorio = false },
            new ParametroDeclarado { Nome = "EstoqueMinimo", Tipo = "decimal", Obrigatorio = false },
        ],
    };

    private static Dictionary<string, JsonElement> Params(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Usa_o_sql_do_catalogo_e_o_timeout_da_operacao()
    {
        var cmd = MontadorComando.Montar(Op, Params("""{ "CodigoFilial": 1 }"""));

        Assert.Equal(Op.Sql, cmd.CommandText);
        Assert.Equal(20, cmd.CommandTimeout); // 20000 ms -> 20 s
    }

    [Fact]
    public void Injeta_max_linhas_do_catalogo()
    {
        var cmd = MontadorComando.Montar(Op, Params("""{ "CodigoFilial": 1 }"""));

        var p = cmd.Parameters["@max_linhas"];
        Assert.Equal(SqlDbType.Int, p.SqlDbType);
        Assert.Equal(5000, p.Value);
    }

    [Fact]
    public void Opcionais_ausentes_entram_como_DBNull()
    {
        var cmd = MontadorComando.Montar(Op, Params("""{ "CodigoFilial": 1 }"""));

        Assert.Equal(1L, cmd.Parameters["@CodigoFilial"].Value);
        Assert.Equal(DBNull.Value, cmd.Parameters["@Codigo"].Value);
        Assert.Equal(DBNull.Value, cmd.Parameters["@EstoqueMinimo"].Value);
    }

    [Fact]
    public void Texto_vai_como_varchar_ansi_nao_unicode()
    {
        var cmd = MontadorComando.Montar(Op, Params("""{ "CodigoFilial": 1, "Codigo": "AB12" }"""));

        var p = cmd.Parameters["@Codigo"];
        Assert.Equal(SqlDbType.VarChar, p.SqlDbType);
        Assert.Equal("AB12", p.Value);
    }

    [Fact]
    public void Decimal_e_inteiro_convertem_certo()
    {
        var cmd = MontadorComando.Montar(Op, Params(
            """{ "CodigoFilial": 3, "EstoqueMinimo": 12.5 }"""));

        Assert.Equal(3L, cmd.Parameters["@CodigoFilial"].Value);
        Assert.Equal(12.5m, cmd.Parameters["@EstoqueMinimo"].Value);
        Assert.Equal(SqlDbType.Decimal, cmd.Parameters["@EstoqueMinimo"].SqlDbType);
    }
}
