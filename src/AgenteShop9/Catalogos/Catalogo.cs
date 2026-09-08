using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenteShop9.Catalogos;

public sealed class CatalogoInvalidoException(string mensagem) : Exception(mensagem);

/// <summary>
/// O catalogo de operacoes (spec 3.3.1). Nesta versao o agente so precisa do
/// numero da versao (heartbeat + anti-retrocesso); executar operacoes vem no
/// item 1.4.
/// </summary>
public sealed record Catalogo
{
    [JsonPropertyName("versao_catalogo")]
    public int VersaoCatalogo { get; init; }

    [JsonPropertyName("operacoes")]
    public Dictionary<string, JsonElement>? Operacoes { get; init; }

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

        return cat;
    }
}
