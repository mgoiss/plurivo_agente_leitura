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

    // ---- paginacao (issue #24) ---------------------------------------------

    private static readonly Operacao OpPaginada = Op with
    {
        Sql = "SELECT prod.Codigo, prod.Nome FROM Prod_Serv AS prod WHERE (@Codigo IS NULL OR prod.Codigo = @Codigo)",
        Desempate = ["prod.Ordem"],
        SqlContagem = "SELECT COUNT(*) FROM Prod_Serv AS prod WHERE (@Codigo IS NULL OR prod.Codigo = @Codigo)",
        Ordenacao = new Ordenacao
        {
            Colunas = new() { ["codigo"] = "prod.Codigo", ["nome"] = "prod.Nome" },
            Padrao = new OrdenacaoPadrao { Coluna = "codigo", Direcao = "asc" },
        },
    };

    // Operacao com GROUP BY: o desempate e a tupla inteira, entao a coluna
    // escolhida SEMPRE ja esta la (o bug do erro 169 que a piloto pegou).
    private static readonly Operacao OpAgrupada = OpPaginada with
    {
        Desempate = ["prod.Codigo", "prod.Nome", "cfop.Codigo"],
        Ordenacao = new Ordenacao
        {
            Colunas = new() { ["codigo"] = "prod.Codigo", ["cfop"] = "cfop.Codigo" },
            Padrao = new OrdenacaoPadrao { Coluna = "codigo", Direcao = "asc" },
        },
    };

    private static PaginacaoResolvida Pag(int pagina, int tamanho, string coluna, string direcao) =>
        new()
        {
            Pagina = pagina, TamanhoPagina = tamanho, OrdenarPor = coluna,
            Direcao = direcao, IncluirTotal = false,
        };

    [Fact]
    public void Paginada_cola_order_by_com_a_coluna_do_catalogo_o_desempate_e_o_offset_fetch()
    {
        var cmd = MontadorComando.Montar(
            OpPaginada, Params("""{ "CodigoFilial": 1 }"""), Pag(2, 20, "nome", "desc"));

        Assert.EndsWith(
            "ORDER BY prod.Nome DESC, prod.Ordem OFFSET @offset ROWS FETCH NEXT @buscar ROWS ONLY",
            cmd.CommandText);
        Assert.Equal(40L, cmd.Parameters["@offset"].Value);   // pagina 2 * tamanho 20
        Assert.Equal(SqlDbType.BigInt, cmd.Parameters["@offset"].SqlDbType);
        Assert.Equal(21, cmd.Parameters["@buscar"].Value);     // tamanho + 1
    }

    [Fact]
    public void Paginada_nao_deixa_ordenar_por_virar_texto_de_sql()
    {
        // A chave escolhida so INDEXA o mapa `colunas`; o fragmento vem do catalogo.
        var cmd = MontadorComando.Montar(
            OpPaginada, Params("""{ "CodigoFilial": 1 }"""), Pag(0, 50, "codigo", "asc"));

        Assert.Contains("ORDER BY prod.Codigo ASC, prod.Ordem", cmd.CommandText);
        Assert.DoesNotContain(" codigo ASC", cmd.CommandText);   // a chave crua nunca vai pro SQL
    }

    [Fact]
    public void Coluna_escolhida_nao_repete_no_order_by_quando_ja_esta_no_desempate()
    {
        // erro 169 do SQL Server: "A column has been specified more than once in
        // the order by list". Pego na piloto (issue #24).
        var cmd = MontadorComando.Montar(
            OpAgrupada, Params("""{ "CodigoFilial": 1 }"""), Pag(0, 10, "codigo", "asc"));

        Assert.Contains(
            "ORDER BY prod.Codigo ASC, prod.Nome, cfop.Codigo OFFSET @offset ROWS",
            cmd.CommandText);
        // "prod.Codigo" aparece uma vez so na clausula ORDER BY.
        var trecho = cmd.CommandText[cmd.CommandText.IndexOf("ORDER BY")..];
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(trecho, @"prod\.Codigo").Count);
    }

    [Fact]
    public void Coluna_do_meio_do_desempate_tambem_nao_duplica()
    {
        var cmd = MontadorComando.Montar(
            OpAgrupada, Params("""{ "CodigoFilial": 1 }"""), Pag(0, 10, "cfop", "desc"));

        Assert.Contains(
            "ORDER BY cfop.Codigo DESC, prod.Codigo, prod.Nome OFFSET @offset ROWS",
            cmd.CommandText);
        var trecho = cmd.CommandText[cmd.CommandText.IndexOf("ORDER BY")..];
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(trecho, @"cfop\.Codigo").Count);
    }

    [Fact]
    public void Sem_paginacao_o_sql_fica_intacto_e_sem_offset()
    {
        var cmd = MontadorComando.Montar(OpPaginada, Params("""{ "CodigoFilial": 1 }"""));

        Assert.Equal(OpPaginada.Sql, cmd.CommandText);
        Assert.False(cmd.Parameters.Contains("@offset"));
    }

    [Fact]
    public void Contagem_usa_o_sql_contagem_com_os_parametros_declarados_e_sem_offset()
    {
        var cmd = MontadorComando.MontarContagem(OpPaginada, Params("""{ "CodigoFilial": 1, "Codigo": "AB12" }"""));

        Assert.Equal(OpPaginada.SqlContagem, cmd.CommandText);
        Assert.Equal("AB12", cmd.Parameters["@Codigo"].Value);
        Assert.False(cmd.Parameters.Contains("@offset"));
        Assert.False(cmd.Parameters.Contains("@buscar"));
    }
}
