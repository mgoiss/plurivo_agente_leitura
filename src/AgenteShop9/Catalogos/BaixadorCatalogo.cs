using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenteShop9.Configuracao;
using Microsoft.Extensions.Logging;

namespace AgenteShop9.Catalogos;

/// <summary>Corpo de GET /api/agente/catalogo quando ha catalogo novo (HTTP 200).</summary>
internal sealed record RespostaCatalogoAgente
{
    [JsonPropertyName("versao_catalogo")]
    public int VersaoCatalogo { get; init; }

    [JsonPropertyName("catalogo_base64")]
    public required string CatalogoBase64 { get; init; }

    [JsonPropertyName("assinatura_base64")]
    public required string AssinaturaBase64 { get; init; }
}

/// <summary>
/// Auto-update do catalogo (item 4.1 do roteiro, issue #48): verifica
/// periodicamente se o backend tem um catalogo mais novo LIBERADO para este
/// servidor (GET /api/agente/catalogo, mesma autenticacao por hash do
/// token_agente do WebSocket /agente) e, se tiver, grava como candidato local
/// e pede ao <see cref="GestorCatalogo"/> para reavaliar.
///
/// Roda independente da sessao WebSocket — e uma chamada HTTPS separada. A
/// decisao de ACEITAR o que foi baixado (assinatura, SHA-256, anti-retrocesso)
/// continua sendo so do GestorCatalogo; este download nunca promove sozinho.
/// </summary>
public sealed class BaixadorCatalogo(
    ILogger<BaixadorCatalogo> log,
    ConfiguracaoAgente config,
    GestorCatalogo gestorCatalogo,
    HttpClient http)
{
    /// <summary>Roda para sempre (ate o token cancelar), verificando a cada `intervalo`.</summary>
    public async Task ExecutarPeriodicamenteAsync(TimeSpan intervalo, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await VerificarUmaVezAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Uma falha aqui (rede, backend fora do ar) nunca pode derrubar o
                // robo — so tenta de novo no proximo ciclo.
                log.LogWarning("Falha ao verificar catalogo novo: {Motivo}", ex.Message);
            }

            try
            {
                await Task.Delay(intervalo, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Uma verificacao. Publica para os testes chamarem sem esperar o timer.</summary>
    public async Task VerificarUmaVezAsync(CancellationToken ct)
    {
        var url = MontarUrlCatalogo(config.BackendUrl, gestorCatalogo.VersaoAceita);
        using var pedido = new HttpRequestMessage(HttpMethod.Get, url);
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.TokenAgente);

        using var resposta = await http.SendAsync(pedido, ct);
        if (resposta.StatusCode == HttpStatusCode.NoContent)
        {
            log.LogDebug("Nenhum catalogo novo liberado para este servidor.");
            return;
        }
        resposta.EnsureSuccessStatusCode();

        var corpo = await resposta.Content.ReadAsStringAsync(ct);
        var dados = JsonSerializer.Deserialize<RespostaCatalogoAgente>(corpo)
            ?? throw new InvalidOperationException("resposta vazia do backend em /api/agente/catalogo.");

        var candidato = config.CatalogoCaminho;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(candidato)) ?? ".");
        await File.WriteAllBytesAsync(candidato, Convert.FromBase64String(dados.CatalogoBase64), ct);
        await File.WriteAllBytesAsync(
            candidato + ".minisig", Convert.FromBase64String(dados.AssinaturaBase64), ct);

        log.LogInformation(
            "Catalogo candidato versao {Versao} baixado do backend; reavaliando.", dados.VersaoCatalogo);
        gestorCatalogo.ReavaliarCandidato();
    }

    /// <summary>
    /// Deriva a URL HTTPS do endpoint de catalogo a partir do `backend_url`
    /// (ex.: wss://api.dominio.com.br/agente) do config.json — sem exigir um
    /// campo novo no config de cada cliente. wss-&gt;https, ws-&gt;http (so em
    /// teste local, spec 3.6 continua valendo: producao e sempre wss/https).
    /// </summary>
    public static Uri MontarUrlCatalogo(string backendUrl, int versaoAtual)
    {
        var uri = new Uri(backendUrl);
        var http = string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase);
        var esquema = http ? "http" : "https";
        var porta = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return new Uri($"{esquema}://{uri.Host}{porta}/api/agente/catalogo?versao_atual={versaoAtual}");
    }
}
