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

    [Theory]
    [InlineData(-1)]
    [InlineData(1400001)]
    public void Limite_textual_fora_do_intervalo_recusa_catalogo(int limite)
    {
        var json = Encoding.UTF8.GetBytes($$"""
            { "versao_catalogo": 99, "operacoes": {
                "catalogo_foto": { "sql": "SELECT Foto FROM Prod_Serv_Fotos",
                    "parametros": [], "max_caracteres_texto": {{limite}} }
            } }
            """);
        var erro = Assert.Throws<CatalogoInvalidoException>(() => Catalogo.Analisar(json));
        Assert.Contains("max_caracteres_texto", erro.Message);
    }

    // ---- paginacao (issue #24) -----------------------------------------

    private static byte[] ComOrdenacao(string ordenacao, string extra = "") => Encoding.UTF8.GetBytes($$"""
        {
          "versao_catalogo": 99,
          "operacoes": {
            "x": {
              "sql": "SELECT prod.Codigo FROM Prod_Serv AS prod",
              "parametros": [],
              "sql_contagem": "SELECT COUNT(*) FROM Prod_Serv AS prod",
              "desempate": ["prod.Ordem"]
              {{extra}},
              "ordenacao": {{ordenacao}}
            }
          }
        }
        """);

    [Fact]
    public void Operacao_sem_ordenacao_e_valida_e_nao_e_paginada()
    {
        var json = Encoding.UTF8.GetBytes("""
            { "versao_catalogo": 99, "operacoes": { "x": { "sql": "SELECT 1", "parametros": [] } } }
            """);
        var cat = Catalogo.Analisar(json);
        Assert.Null(cat.Operacoes!["x"].Ordenacao);
        Assert.Equal(50, cat.Operacoes["x"].TamanhoPaginaPadrao);
        Assert.Equal(200, cat.Operacoes["x"].TamanhoPaginaMax);
    }

    [Fact]
    public void Ordenacao_completa_parseia()
    {
        var cat = Catalogo.Analisar(ComOrdenacao(
            """{ "colunas": { "codigo": "prod.Codigo", "nome": "prod.Nome" }, "padrao": { "coluna": "codigo", "direcao": "asc" } }"""));
        var op = cat.Operacoes!["x"];
        Assert.Equal("prod.Codigo", op.Ordenacao!.Colunas["codigo"]);
        Assert.Equal("codigo", op.Ordenacao.Padrao.Coluna);
        Assert.Equal(["prod.Ordem"], op.Desempate);
        Assert.Contains("COUNT(*)", op.SqlContagem);
    }

    [Fact]
    public void Ordenacao_com_colunas_vazio_e_invalida()
    {
        var ex = Assert.Throws<CatalogoInvalidoException>(() => Catalogo.Analisar(ComOrdenacao(
            """{ "colunas": {}, "padrao": { "coluna": "codigo", "direcao": "asc" } }""")));
        Assert.Contains("ordenacao.colunas", ex.Message);
    }

    [Fact]
    public void Padrao_coluna_fora_da_allowlist_e_invalido()
    {
        var ex = Assert.Throws<CatalogoInvalidoException>(() => Catalogo.Analisar(ComOrdenacao(
            """{ "colunas": { "codigo": "prod.Codigo" }, "padrao": { "coluna": "nome", "direcao": "asc" } }""")));
        Assert.Contains("padrao.coluna", ex.Message);
    }

    [Fact]
    public void Padrao_direcao_invalida_e_recusada()
    {
        var ex = Assert.Throws<CatalogoInvalidoException>(() => Catalogo.Analisar(ComOrdenacao(
            """{ "colunas": { "codigo": "prod.Codigo" }, "padrao": { "coluna": "codigo", "direcao": "cima" } }""")));
        Assert.Contains("direcao", ex.Message);
    }

    [Fact]
    public void Ordenacao_sem_desempate_e_invalida()
    {
        var json = Encoding.UTF8.GetBytes("""
            {
              "versao_catalogo": 99,
              "operacoes": {
                "x": {
                  "sql": "SELECT 1", "parametros": [],
                  "sql_contagem": "SELECT COUNT(*) FROM Prod_Serv",
                  "ordenacao": { "colunas": { "codigo": "prod.Codigo" }, "padrao": { "coluna": "codigo", "direcao": "asc" } }
                }
              }
            }
            """);
        var ex = Assert.Throws<CatalogoInvalidoException>(() => Catalogo.Analisar(json));
        Assert.Contains("desempate", ex.Message);
    }

    [Fact]
    public void Ordenacao_sem_sql_contagem_e_invalida()
    {
        var json = Encoding.UTF8.GetBytes("""
            {
              "versao_catalogo": 99,
              "operacoes": {
                "x": {
                  "sql": "SELECT 1", "parametros": [], "desempate": ["prod.Ordem"],
                  "ordenacao": { "colunas": { "codigo": "prod.Codigo" }, "padrao": { "coluna": "codigo", "direcao": "asc" } }
                }
              }
            }
            """);
        var ex = Assert.Throws<CatalogoInvalidoException>(() => Catalogo.Analisar(json));
        Assert.Contains("sql_contagem", ex.Message);
    }

    [Fact]
    public void Tamanho_pagina_padrao_acima_do_max_e_invalido()
    {
        var ex = Assert.Throws<CatalogoInvalidoException>(() => Catalogo.Analisar(ComOrdenacao(
            """{ "colunas": { "codigo": "prod.Codigo" }, "padrao": { "coluna": "codigo", "direcao": "asc" } }""",
            extra: ""","tamanho_pagina_padrao": 500, "tamanho_pagina_max": 200""")));
        Assert.Contains("tamanho_pagina_padrao", ex.Message);
    }
}
