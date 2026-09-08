using System.Text.Json;
using AgenteShop9;
using AgenteShop9.Configuracao;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenteShop9.Testes;

/// <summary>
/// O robo de verdade (ClientWebSocket real) contra um servidor WebSocket local.
/// Usa ws:// — nenhum codigo desliga a validacao de certificado (proibido, CLAUDE.md).
/// </summary>
public class IntegracaoConexaoTestes
{
    private static OpcoesAgente OpcoesRapidas => new()
    {
        IntervaloHeartbeat = TimeSpan.FromMilliseconds(80),
        ReconexaoEsperaInicial = TimeSpan.FromMilliseconds(50),
        ReconexaoEsperaMaxima = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public async Task Robo_conecta_manda_bearer_e_heartbeat()
    {
        await using var servidor = new BackendDeMentira();
        await servidor.IniciarAsync();

        var robo = CriarRobo(servidor.UrlAgente, "token-de-teste");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await robo.StartAsync(cts.Token);
        try
        {
            var handshake = await servidor.EsperarConexaoAsync(cts.Token);
            Assert.Equal("Bearer token-de-teste", handshake.Authorization);

            var mensagem = await servidor.EsperarMensagemAsync(cts.Token);
            using var doc = JsonDocument.Parse(mensagem);
            Assert.Equal("heartbeat", doc.RootElement.GetProperty("tipo").GetString());
        }
        finally
        {
            await robo.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Robo_reconecta_sozinho_depois_de_queda()
    {
        await using var servidor = new BackendDeMentira();
        await servidor.IniciarAsync();

        var robo = CriarRobo(servidor.UrlAgente, "token-de-teste");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await robo.StartAsync(cts.Token);
        try
        {
            await servidor.EsperarConexaoAsync(cts.Token);
            await servidor.DerrubarConexaoAtualAsync();

            // Uma segunda conexao tem que chegar sozinha, sem ninguem mandar.
            await servidor.EsperarConexaoAsync(cts.Token);
        }
        finally
        {
            await robo.StopAsync(CancellationToken.None);
        }
    }

    private static TrabalhadorAgente CriarRobo(Uri url, string token)
    {
        var pastaTmp = Directory.CreateTempSubdirectory("agente_int").FullName;
        var config = new ConfiguracaoAgente
        {
            ConexaoSql = "nao-usado-neste-teste",
            BackendUrl = url.ToString(),
            TokenAgente = token,
            CatalogoCaminho = Path.Combine(pastaTmp, "catalogo.json"), // nao existe: sem catalogo
        };

        var gestor = new AgenteShop9.Catalogos.GestorCatalogo(
            NullLogger<AgenteShop9.Catalogos.GestorCatalogo>.Instance, config);

        return new TrabalhadorAgente(
            NullLogger<TrabalhadorAgente>.Instance, config, OpcoesRapidas, gestor);
    }
}
