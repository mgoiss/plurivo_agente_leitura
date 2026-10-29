using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenteShop9.Catalogos;

public sealed class CatalogoInvalidoException(string mensagem) : Exception(mensagem);

/// <summary>Um parametro declarado por uma operacao do catalogo (spec 3.3).</summary>
public sealed record ParametroDeclarado
{
    [JsonPropertyName("nome")] public string Nome { get; init; } = "";

    /// <summary>"inteiro" | "texto" | "decimal" | "data".</summary>
    [JsonPropertyName("tipo")] public string Tipo { get; init; } = "";

    [JsonPropertyName("obrigatorio")] public bool Obrigatorio { get; init; }
}

/// <summary>Coluna padrao de ordenacao de uma operacao (chave + direcao).</summary>
public sealed record OrdenacaoPadrao
{
    [JsonPropertyName("coluna")] public string Coluna { get; init; } = "";
    [JsonPropertyName("direcao")] public string Direcao { get; init; } = "asc";
}

/// <summary>
/// Ordenacao permitida de uma operacao paginada (issue #24). `colunas` mapeia a
/// CHAVE que o front manda em `ordenar_por` para a coluna real do SQL — o front
/// nunca manda nome de coluna. Armadilha da spec 3.3: entrada do front nunca
/// vira `ORDER BY`; aqui ela so ESCOLHE um fragmento fixo, vindo do catalogo
/// assinado.
/// </summary>
public sealed record Ordenacao
{
    [JsonPropertyName("colunas")] public Dictionary<string, string> Colunas { get; init; } = [];
    [JsonPropertyName("padrao")] public OrdenacaoPadrao Padrao { get; init; } = new();
}

/// <summary>Uma operacao nomeada do catalogo: SQL parametrizado fixo + limites.</summary>
public sealed record Operacao
{
    [JsonPropertyName("sql")] public string Sql { get; init; } = "";
    [JsonPropertyName("parametros")] public List<ParametroDeclarado> Parametros { get; init; } = [];
    [JsonPropertyName("max_linhas")] public int MaxLinhas { get; init; } = 10_000;
    [JsonPropertyName("timeout_ms")] public int TimeoutMs { get; init; } = 30_000;

    /// <summary>Limite por coluna textual lida em fluxo. Zero preserva o legado.</summary>
    [JsonPropertyName("max_caracteres_texto")] public int MaxCaracteresTexto { get; init; }

    /// <summary>Prepara #FotosValidas com bytes verificados antes desta consulta.</summary>
    [JsonPropertyName("usa_fotos_validadas")] public bool UsaFotosValidadas { get; init; }

    /// <summary>Operacao assinada usada somente pelo agente, nunca por pedido remoto.</summary>
    [JsonPropertyName("uso_interno")] public bool UsoInterno { get; init; }

    /// <summary>
    /// Janela maxima, em dias, entre os parametros `inicio` e `fim`. So faz
    /// sentido em operacao com faixa de datas. Ausente = sem limite.
    /// Aplicado em <see cref="Operacoes.ValidadorParametros"/> antes do banco.
    /// </summary>
    [JsonPropertyName("periodo_max_dias")] public int? PeriodoMaxDias { get; init; }

    // ---- Paginacao (issue #24) ------------------------------------------
    // As quatro proximas andam JUNTAS: se a operacao declara `ordenacao`, ela e
    // paginada e PRECISA tambem de `desempate` e `sql_contagem` (Analisar exige).
    // O padrao do catalogo v16 em diante e toda operacao ser paginada; operacao
    // sem `ordenacao` cai no caminho antigo (SELECT do proprio SQL, sem OFFSET).

    /// <summary>Colunas que o front pode escolher em `ordenar_por` (chave -&gt; coluna real).</summary>
    [JsonPropertyName("ordenacao")] public Ordenacao? Ordenacao { get; init; }

    /// <summary>
    /// Colunas (do catalogo, nunca do front) coladas no fim do `ORDER BY` para a
    /// ordenacao ser deterministica — sem isso a paginacao repete/pula linha
    /// quando o criterio escolhido empata. Numa operacao com PK unica e so a PK;
    /// numa com `GROUP BY` e a tupla inteira do agrupamento. Se a coluna
    /// escolhida em `ordenar_por` ja estiver aqui, o montador a remove desta
    /// lista (SQL Server recusa coluna repetida no ORDER BY — erro 169).
    /// </summary>
    [JsonPropertyName("desempate")] public List<string> Desempate { get; init; } = [];

    /// <summary>
    /// SQL que conta o total de linhas (mesmo `FROM`/`WHERE`, sem `ORDER BY`/
    /// `OFFSET`, com `COUNT`). Rodado so quando o pedido traz `incluir_total`.
    /// </summary>
    [JsonPropertyName("sql_contagem")] public string? SqlContagem { get; init; }

    [JsonPropertyName("tamanho_pagina_padrao")] public int TamanhoPaginaPadrao { get; init; } = 50;
    [JsonPropertyName("tamanho_pagina_max")] public int TamanhoPaginaMax { get; init; } = 200;
}

/// <summary>
/// O catalogo de operacoes (spec 3.3.1). O SQL parametrizado vive AQUI, nunca
/// vem do backend. Parametro so entra como valor.
/// </summary>
public sealed record Catalogo
{
    [JsonPropertyName("versao_catalogo")]
    public int VersaoCatalogo { get; init; }

    [JsonPropertyName("operacoes")]
    public Dictionary<string, Operacao>? Operacoes { get; init; }

    /// <summary>Le e valida a estrutura minima. Lanca CatalogoInvalidoException.</summary>
    public static Catalogo Analisar(byte[] conteudoJson)
    {
        Catalogo? cat;
        try
        {
            cat = JsonSerializer.Deserialize<Catalogo>(conteudoJson);
        }
        catch (JsonException ex)
        {
            throw new CatalogoInvalidoException($"catalogo nao e JSON valido: {ex.Message}");
        }

        if (cat is null)
            throw new CatalogoInvalidoException("catalogo vazio.");
        if (cat.VersaoCatalogo < 1)
            throw new CatalogoInvalidoException("catalogo sem 'versao_catalogo' valida (>= 1).");
        if (cat.Operacoes is null)
            throw new CatalogoInvalidoException("catalogo sem o objeto 'operacoes'.");

        foreach (var (nome, op) in cat.Operacoes)
        {
            if (string.IsNullOrWhiteSpace(op.Sql))
                throw new CatalogoInvalidoException($"operacao '{nome}' sem 'sql'.");
            if (op.MaxLinhas < 1)
                throw new CatalogoInvalidoException($"operacao '{nome}' com 'max_linhas' invalido.");
            if (op.TimeoutMs < 1)
                throw new CatalogoInvalidoException($"operacao '{nome}' com 'timeout_ms' invalido.");
            if (op.MaxCaracteresTexto < 0 || op.MaxCaracteresTexto > 1_400_000)
                throw new CatalogoInvalidoException($"operacao '{nome}' com 'max_caracteres_texto' invalido.");
            if (op.PeriodoMaxDias is < 1)
                throw new CatalogoInvalidoException(
                    $"operacao '{nome}' com 'periodo_max_dias' invalido (>= 1).");
            foreach (var p in op.Parametros)
            {
                if (string.IsNullOrWhiteSpace(p.Nome))
                    throw new CatalogoInvalidoException($"operacao '{nome}' com parametro sem 'nome'.");
                if (p.Tipo is not ("inteiro" or "texto" or "decimal" or "data" or "date"))
                    throw new CatalogoInvalidoException(
                        $"operacao '{nome}', parametro '{p.Nome}': tipo '{p.Tipo}' desconhecido.");
            }

            ValidarPaginacao(nome, op);
        }

        return cat;
    }

    /// <summary>
    /// Se a operacao declara `ordenacao`, ela e paginada e o trio
    /// ordenacao + desempate + sql_contagem tem que estar completo e coerente
    /// (issue #24). Operacao sem `ordenacao` nao e checada aqui — cai no caminho
    /// antigo, sem OFFSET/FETCH.
    /// </summary>
    private static void ValidarPaginacao(string nome, Operacao op)
    {
        if (op.Ordenacao is null)
            return;

        if (op.Ordenacao.Colunas.Count == 0)
            throw new CatalogoInvalidoException($"operacao '{nome}': 'ordenacao.colunas' vazio.");
        foreach (var (chave, coluna) in op.Ordenacao.Colunas)
            if (string.IsNullOrWhiteSpace(chave) || string.IsNullOrWhiteSpace(coluna))
                throw new CatalogoInvalidoException(
                    $"operacao '{nome}': 'ordenacao.colunas' com chave ou coluna vazia.");

        if (!op.Ordenacao.Colunas.ContainsKey(op.Ordenacao.Padrao.Coluna))
            throw new CatalogoInvalidoException(
                $"operacao '{nome}': 'ordenacao.padrao.coluna' ('{op.Ordenacao.Padrao.Coluna}') " +
                "nao esta em 'ordenacao.colunas'.");
        if (op.Ordenacao.Padrao.Direcao is not ("asc" or "desc"))
            throw new CatalogoInvalidoException(
                $"operacao '{nome}': 'ordenacao.padrao.direcao' deve ser 'asc' ou 'desc'.");

        if (op.Desempate.Count == 0)
            throw new CatalogoInvalidoException(
                $"operacao '{nome}': tem 'ordenacao' mas 'desempate' esta vazio.");
        if (op.Desempate.Any(string.IsNullOrWhiteSpace))
            throw new CatalogoInvalidoException(
                $"operacao '{nome}': 'desempate' com entrada vazia.");
        if (string.IsNullOrWhiteSpace(op.SqlContagem))
            throw new CatalogoInvalidoException(
                $"operacao '{nome}': tem 'ordenacao' mas nao tem 'sql_contagem'.");

        if (op.TamanhoPaginaMax < 1)
            throw new CatalogoInvalidoException(
                $"operacao '{nome}': 'tamanho_pagina_max' invalido (>= 1).");
        if (op.TamanhoPaginaPadrao < 1 || op.TamanhoPaginaPadrao > op.TamanhoPaginaMax)
            throw new CatalogoInvalidoException(
                $"operacao '{nome}': 'tamanho_pagina_padrao' deve estar entre 1 e 'tamanho_pagina_max'.");
    }
}
