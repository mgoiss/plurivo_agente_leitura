using AgenteShop9.Catalogos;

namespace AgenteShop9.Operacoes;

/// <summary>
/// Paginacao como veio no pedido `executar` do backend (issue #24). Tudo
/// opcional — o que faltar recebe o padrao da operacao em
/// <see cref="ValidadorPaginacao"/>.
/// </summary>
public sealed record PaginacaoPedido
{
    public int? Pagina { get; init; }
    public int? TamanhoPagina { get; init; }

    /// <summary>Chave em `ordenacao.colunas` da operacao — nunca um nome de coluna.</summary>
    public string? OrdenarPor { get; init; }

    /// <summary>"asc" | "desc" (comparado sem diferenciar maiuscula).</summary>
    public string? Direcao { get; init; }

    /// <summary>Quando true, o executor tambem roda o `sql_contagem` da operacao.</summary>
    public bool IncluirTotal { get; init; }

    public static readonly PaginacaoPedido Vazio = new();
}

/// <summary>Paginacao ja resolvida contra o catalogo — sem nulos, pronta para o SQL.</summary>
public sealed record PaginacaoResolvida
{
    public required int Pagina { get; init; }
    public required int TamanhoPagina { get; init; }
    public required string OrdenarPor { get; init; }
    public required string Direcao { get; init; }
    public required bool IncluirTotal { get; init; }

    /// <summary>Linhas a pular: `pagina * tamanho_pagina`. `long` para nao estourar.</summary>
    public long Offset => (long)Pagina * TamanhoPagina;

    /// <summary>Linhas a buscar: `tamanho_pagina + 1` — a linha extra e o "tem proxima".</summary>
    public int Buscar => TamanhoPagina + 1;
}

/// <summary>
/// Resolve a paginacao do pedido contra a operacao do catalogo, aplicando os
/// padroes e recusando o que estiver fora da regra ANTES de tocar no banco
/// (mesma politica de <see cref="ValidadorParametros"/>). So faz sentido chamar
/// quando <see cref="Operacao.Ordenacao"/> nao e nulo.
/// </summary>
public static class ValidadorPaginacao
{
    public static (PaginacaoResolvida? resolvida, ErroValidacao? erro) Resolver(
        Operacao op, PaginacaoPedido pedido)
    {
        if (op.Ordenacao is null)
            return (null, new ErroValidacao(
                "parametro_invalido", "esta operacao nao aceita paginacao."));

        var pagina = pedido.Pagina ?? 0;
        if (pagina < 0)
            return (null, new ErroValidacao("parametro_invalido", "'pagina' nao pode ser negativa."));

        var tamanho = pedido.TamanhoPagina ?? op.TamanhoPaginaPadrao;
        if (tamanho < 1)
            return (null, new ErroValidacao("parametro_invalido", "'tamanho_pagina' tem que ser >= 1."));
        if (tamanho > op.TamanhoPaginaMax)
            return (null, new ErroValidacao(
                "parametro_invalido",
                $"'tamanho_pagina' nao pode passar de {op.TamanhoPaginaMax} nesta operacao."));

        var ordenarPor = pedido.OrdenarPor ?? op.Ordenacao.Padrao.Coluna;
        if (!op.Ordenacao.Colunas.ContainsKey(ordenarPor))
            return (null, new ErroValidacao(
                "parametro_invalido", $"ordenacao '{ordenarPor}' nao e permitida nesta operacao."));

        var direcao = (pedido.Direcao ?? op.Ordenacao.Padrao.Direcao).ToLowerInvariant();
        if (direcao is not ("asc" or "desc"))
            return (null, new ErroValidacao(
                "parametro_invalido", "'direcao' tem que ser 'asc' ou 'desc'."));

        return (new PaginacaoResolvida
        {
            Pagina = pagina,
            TamanhoPagina = tamanho,
            OrdenarPor = ordenarPor,
            Direcao = direcao,
            IncluirTotal = pedido.IncluirTotal,
        }, null);
    }
}
