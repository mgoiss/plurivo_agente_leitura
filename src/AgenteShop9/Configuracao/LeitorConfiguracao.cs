using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AgenteShop9.Configuracao;

public sealed class ConfiguracaoInvalidaException(string mensagem) : Exception(mensagem);

/// <summary>Le e valida o config.json do cliente.</summary>
public static class LeitorConfiguracao
{
    private sealed record Dto
    {
        // ISSUE #3 / spec 3.5: NAO adicionar tenant_id aqui. O robo nunca informa
        // o proprio tenant — quem decide de qual loja ele e o backend, pelo hash
        // do token_agente. Se o arquivo trouxer tenant_id, e ignorado (abaixo).
        [JsonPropertyName("conexao_sql")] public string? ConexaoSql { get; init; }
        [JsonPropertyName("backend_url")] public string? BackendUrl { get; init; }
        [JsonPropertyName("token_agente")] public string? TokenAgente { get; init; }
    }

    public static ConfiguracaoAgente Ler(string caminho, ILogger? logger = null)
    {
        if (!File.Exists(caminho))
            throw new ConfiguracaoInvalidaException(
                $"Arquivo de configuracao nao encontrado: {caminho}. Use config.exemplo.json como modelo.");

        var texto = File.ReadAllText(caminho);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(texto);
        }
        catch (JsonException ex)
        {
            throw new ConfiguracaoInvalidaException($"Arquivo de configuracao nao e um JSON valido: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("tenant_id", out _))
            {
                logger?.LogWarning(
                    "config.json contem 'tenant_id' — IGNORADO. O robo nunca informa o proprio tenant (spec 3.5).");
            }
        }

        var dto = JsonSerializer.Deserialize<Dto>(texto)
                  ?? throw new ConfiguracaoInvalidaException("Arquivo de configuracao vazio.");

        var faltando = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.ConexaoSql)) faltando.Add("conexao_sql");
        if (string.IsNullOrWhiteSpace(dto.BackendUrl)) faltando.Add("backend_url");
        if (string.IsNullOrWhiteSpace(dto.TokenAgente)) faltando.Add("token_agente");
        if (faltando.Count > 0)
            throw new ConfiguracaoInvalidaException(
                "Configuracao incompleta, faltam os campos: " + string.Join(", ", faltando) + ".");

        if (!Uri.TryCreate(dto.BackendUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "ws" && uri.Scheme != "wss"))
            throw new ConfiguracaoInvalidaException(
                $"backend_url deve comecar com ws:// ou wss:// (recebido: {dto.BackendUrl}).");

        return new ConfiguracaoAgente
        {
            ConexaoSql = dto.ConexaoSql!.Trim(),
            BackendUrl = dto.BackendUrl!.Trim(),
            TokenAgente = dto.TokenAgente!.Trim(),
        };
    }
}
