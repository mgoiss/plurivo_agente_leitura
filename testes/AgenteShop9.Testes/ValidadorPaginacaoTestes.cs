using AgenteShop9.Catalogos;
using AgenteShop9.Operacoes;

namespace AgenteShop9.Testes;

/// <summary>
/// Resolucao da paginacao contra o catalogo (issue #24): padroes aplicados,
/// entrada fora da regra vira `parametro_invalido` ANTES do banco.
/// </summary>
public class ValidadorPaginacaoTestes
{
    private static readonly Operacao Op = new()
    {
        Sql = "SELECT 1",
        SqlContagem = "SELECT COUNT(*)",
        Desempate = ["prod.Ordem"],
        TamanhoPaginaPadrao = 50,
        TamanhoPaginaMax = 200,
        Ordenacao = new Ordenacao
        {
            Colunas = new() { ["codigo"] = "prod.Codigo", ["nome"] = "prod.Nome" },
            Padrao = new OrdenacaoPadrao { Coluna = "codigo", Direcao = "asc" },
        },
    };

    [Fact]
    public void Vazio_aplica_os_padroes_da_operacao()
    {
        var (r, erro) = ValidadorPaginacao.Resolver(Op, PaginacaoPedido.Vazio);

        Assert.Null(erro);
        Assert.Equal(0, r!.Pagina);
        Assert.Equal(50, r.TamanhoPagina);
        Assert.Equal("codigo", r.OrdenarPor);
        Assert.Equal("asc", r.Direcao);
        Assert.False(r.IncluirTotal);
        Assert.Equal(0L, r.Offset);
        Assert.Equal(51, r.Buscar);
    }

    [Fact]
    public void Offset_e_pagina_vezes_tamanho()
    {
        var (r, _) = ValidadorPaginacao.Resolver(Op, new PaginacaoPedido { Pagina = 3, TamanhoPagina = 25 });
        Assert.Equal(75L, r!.Offset);
        Assert.Equal(26, r.Buscar);
    }

    [Fact]
    public void Pagina_negativa_e_parametro_invalido()
    {
        var (r, erro) = ValidadorPaginacao.Resolver(Op, new PaginacaoPedido { Pagina = -1 });
        Assert.Null(r);
        Assert.Equal("parametro_invalido", erro!.Codigo);
        Assert.Contains("pagina", erro.Mensagem);
    }

    [Fact]
    public void Tamanho_pagina_acima_do_max_da_operacao_e_recusado()
    {
        var (r, erro) = ValidadorPaginacao.Resolver(Op, new PaginacaoPedido { TamanhoPagina = 201 });
        Assert.Null(r);
        Assert.Equal("parametro_invalido", erro!.Codigo);
        Assert.Contains("200", erro.Mensagem);
    }

    [Fact]
    public void Tamanho_pagina_zero_e_recusado()
    {
        var (_, erro) = ValidadorPaginacao.Resolver(Op, new PaginacaoPedido { TamanhoPagina = 0 });
        Assert.Equal("parametro_invalido", erro!.Codigo);
    }

    [Fact]
    public void Ordenar_por_fora_da_allowlist_e_recusado()
    {
        var (r, erro) = ValidadorPaginacao.Resolver(Op, new PaginacaoPedido { OrdenarPor = "prod.Nome DESC" });
        Assert.Null(r);
        Assert.Equal("parametro_invalido", erro!.Codigo);
        Assert.Contains("nao e permitida", erro.Mensagem);
    }

    [Fact]
    public void Ordenar_por_valido_resolve_a_chave()
    {
        var (r, erro) = ValidadorPaginacao.Resolver(Op, new PaginacaoPedido { OrdenarPor = "nome", Direcao = "DESC" });
        Assert.Null(erro);
        Assert.Equal("nome", r!.OrdenarPor);
        Assert.Equal("desc", r.Direcao);   // normaliza para minuscula
    }

    [Fact]
    public void Direcao_invalida_e_recusada()
    {
        var (_, erro) = ValidadorPaginacao.Resolver(Op, new PaginacaoPedido { Direcao = "crescente" });
        Assert.Equal("parametro_invalido", erro!.Codigo);
        Assert.Contains("direcao", erro.Mensagem);
    }

    [Fact]
    public void Incluir_total_passa_adiante()
    {
        var (r, _) = ValidadorPaginacao.Resolver(Op, new PaginacaoPedido { IncluirTotal = true });
        Assert.True(r!.IncluirTotal);
    }

    [Fact]
    public void Operacao_sem_ordenacao_recusa()
    {
        var semOrdenacao = new Operacao { Sql = "SELECT 1" };
        var (r, erro) = ValidadorPaginacao.Resolver(semOrdenacao, PaginacaoPedido.Vazio);
        Assert.Null(r);
        Assert.Equal("parametro_invalido", erro!.Codigo);
    }
}
