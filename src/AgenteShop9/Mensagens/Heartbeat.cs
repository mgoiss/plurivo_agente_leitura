using System.Text.Json.Serialization;

namespace AgenteShop9.Mensagens;

/// <summary>
/// Sinal de vida do robo para o backend (spec 3.4), enviado a cada 30s.
/// Formato: {"tipo":"heartbeat","versao_agente":"x.y.z","versao_catalogo":N}.
/// </summary>
public sealed record Heartbeat
{
    [JsonPropertyName("tipo")]
    public string Tipo { get; } = "heartbeat";

    [JsonPropertyName("versao_agente")]
    public required string VersaoAgente { get; init; }

    /// <summary>0 = nenhum catalogo ainda. Passa a valer a partir do item 1.3/1.4.</summary>
    [JsonPropertyName("versao_catalogo")]
    public int VersaoCatalogo { get; init; }
}
