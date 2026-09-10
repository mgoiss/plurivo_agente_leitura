using System.Text.Json.Serialization;

namespace AgenteShop9.Operacoes;

/// <summary>
/// Bloco de paginacao devolvido no `resultado` (issue #24). `total_*` so vem
/// quando o pedido pediu `incluir_total`. Campos nulos sao omitidos.
/// </summary>
public sealed record RespostaPaginacao
{
    [JsonPropertyName("pagina")] public required int Pagina { get; init; }
    [JsonPropertyName("tamanho_pagina")] public required int TamanhoPagina { get; init; }

    /// <summary>Veio a linha extra (tamanho_pagina + 1)? Entao ha proxima pagina.</summary>
    [JsonPropertyName("tem_proxima")] public required bool TemProxima { get; init; }

    [JsonPropertyName("total_linhas")] public long? TotalLinhas { get; init; }
    [JsonPropertyName("total_paginas")] public int? TotalPaginas { get; init; }

    [JsonPropertyName("ordenado_por")] public required string OrdenadoPor { get; init; }
    [JsonPropertyName("direcao")] public required string Direcao { get; init; }
}

/// <summary>
/// Resposta do agente para o backend apos um pedido `executar` (spec 3.4):
///   sucesso: {"tipo":"resultado","id_requisicao":"...","status":"ok","linhas":[...],"duracao_ms":320}
///   erro:    {"tipo":"resultado","id_requisicao":"...","status":"erro","codigo_erro":"...","mensagem":"..."}
/// Campos nulos sao omitidos na serializacao (ver ClienteBackend).
/// </summary>
public sealed record MensagemResultado
{
    [JsonPropertyName("tipo")]
    public string Tipo { get; } = "resultado";

    [JsonPropertyName("id_requisicao")]
    public string? IdRequisicao { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; } // "ok" | "erro"

    [JsonPropertyName("linhas")]
    public IReadOnlyList<IReadOnlyDictionary<string, object?>>? Linhas { get; init; }

    [JsonPropertyName("paginacao")]
    public RespostaPaginacao? Paginacao { get; init; }

    [JsonPropertyName("duracao_ms")]
    public long? DuracaoMs { get; init; }

    [JsonPropertyName("codigo_erro")]
    public string? CodigoErro { get; init; }

    [JsonPropertyName("mensagem")]
    public string? Mensagem { get; init; }

    public static MensagemResultado Erro(string? idRequisicao, string codigo, string mensagem) =>
        new() { IdRequisicao = idRequisicao, Status = "erro", CodigoErro = codigo, Mensagem = mensagem };
}
