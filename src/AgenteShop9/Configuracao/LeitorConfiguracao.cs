using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AgenteShop9.Configuracao;

public sealed class ConfiguracaoInvalidaException(string mensagem) : Exception(mensagem);

/// <summary>Le e valida o config.json do cliente.</summary>
public static class LeitorConfiguracao
{
    private sealed record Dto
    {
        // ISSUE #3 / spec 3.5: NAO adicionar identificacao de servidor aqui. O robo
        // nunca informa o proprio servidor — quem decide de qual servidor ele e o
        // backend, pelo hash do token_agente. Se o arquivo trouxer, e ignorado.
        [JsonPropertyName("conexao_sql")] public string? ConexaoSql { get; init; }
        [JsonPropertyName("backend_url")] public string? BackendUrl { get; init; }
        [JsonPropertyName("token_agente")] public string? TokenAgente { get; init; }
        [JsonPropertyName("catalogo_caminho")] public string? CatalogoCaminho { get; init; }
        [JsonPropertyName("execucoes_simultaneas")] public int? ExecucoesSimultaneas { get; init; }
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
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var chave in new[] { "servidor_ordem", "servidor" })
                    if (doc.RootElement.TryGetProperty(chave, out _))
                        logger?.LogWarning(
                            "config.json contem '{Chave}' — IGNORADO. O robo nunca informa o proprio servidor (spec 3.5).",
                            chave);
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

        ValidarConexaoSql(dto.ConexaoSql!);

        if (!Uri.TryCreate(dto.BackendUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "ws" && uri.Scheme != "wss"))
            throw new ConfiguracaoInvalidaException(
                $"backend_url deve comecar com ws:// ou wss:// (recebido: {dto.BackendUrl}).");

        if (dto.ExecucoesSimultaneas is < 1 or > 32)
            throw new ConfiguracaoInvalidaException(
                $"execucoes_simultaneas deve estar entre 1 e 32 (recebido: {dto.ExecucoesSimultaneas}).");

        var pastaConfig = Path.GetDirectoryName(Path.GetFullPath(caminho)) ?? ".";
        var catalogoCaminho = string.IsNullOrWhiteSpace(dto.CatalogoCaminho)
            ? Path.Combine(pastaConfig, "catalogo.json")
            : dto.CatalogoCaminho.Trim();

        return new ConfiguracaoAgente
        {
            ConexaoSql = dto.ConexaoSql!.Trim(),
            BackendUrl = dto.BackendUrl!.Trim(),
            TokenAgente = dto.TokenAgente!.Trim(),
            CatalogoCaminho = catalogoCaminho,
            ExecucoesSimultaneas = dto.ExecucoesSimultaneas,
        };
    }

    private static void ValidarConexaoSql(string texto)
    {
        SqlConnectionStringBuilder conexao;
        try
        {
            conexao = new SqlConnectionStringBuilder(texto);
        }
        catch (ArgumentException)
        {
            throw new ConfiguracaoInvalidaException(
                "conexao_sql nao e uma connection string SQL Server valida. Revise Server, Database e as credenciais.");
        }

        if (string.IsNullOrWhiteSpace(conexao.DataSource))
            throw new ConfiguracaoInvalidaException(
                "conexao_sql deve informar Server=<servidor_sql>.");

        if (string.IsNullOrWhiteSpace(conexao.InitialCatalog))
            throw new ConfiguracaoInvalidaException(
                "conexao_sql deve informar Database=<banco_do_shop9> (por exemplo, Database=S9_Real). " +
                "Sem esse campo, o SQL Server pode abrir o banco padrao e as consultas do Shop9 falham.");
    }
}
