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

/// <summary>Uma operacao nomeada do catalogo: SQL parametrizado fixo + limites.</summary>
public sealed record Operacao
{
    [JsonPropertyName("sql")] public string Sql { get; init; } = "";
    [JsonPropertyName("parametros")] public List<ParametroDeclarado> Parametros { get; init; } = [];
    [JsonPropertyName("max_linhas")] public int MaxLinhas { get; init; } = 10_000;
    [JsonPropertyName("timeout_ms")] public int TimeoutMs { get; init; } = 30_000;

    /// <summary>
    /// Janela maxima, em dias, entre os parametros `inicio` e `fim`. So faz
    /// sentido em operacao com faixa de datas. Ausente = sem limite.
    /// Aplicado em <see cref="Operacoes.ValidadorParametros"/> antes do banco.
    /// </summary>
    [JsonPropertyName("periodo_max_dias")] public int? PeriodoMaxDias { get; init; }
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
        }

        return cat;
    }
}
