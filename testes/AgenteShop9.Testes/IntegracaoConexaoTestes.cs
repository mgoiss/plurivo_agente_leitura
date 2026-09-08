using System.Text.Json;
using AgenteShop9;
using AgenteShop9.Configuracao;
using AgenteShop9.Operacoes;
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

    [Fact]
    public async Task Robo_responde_executar_com_resultado()
    {
        await using var servidor = new BackendDeMentira();
        await servidor.IniciarAsync();

        var executor = new ExecutorFake();
        var robo = CriarRobo(servidor.UrlAgente, "token-de-teste", executor);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await robo.StartAsync(cts.Token);
        try
        {
            await servidor.EsperarConexaoAsync(cts.Token);
            await servidor.EsperarMensagemDoTipoAsync("heartbeat", cts.Token); // robo ja no laco

            await servidor.EnviarAoAgenteAsync(
                """{"tipo":"executar","id_requisicao":"req-1","operacao":"listar_produtos_estoque","parametros":{"CodigoFilial":1}}""");

            var resp = await servidor.EsperarMensagemDoTipoAsync("resultado", cts.Token);
            using var doc = JsonDocument.Parse(resp);
            Assert.Equal("req-1", doc.RootElement.GetProperty("id_requisicao").GetString());
            Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("listar_produtos_estoque", executor.UltimaOperacao);
        }
        finally
        {
            await robo.StopAsync(CancellationToken.None);
        }
    }

    private static TrabalhadorAgente CriarRobo(Uri url, string token, IExecutorOperacao? executor = null)
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
            NullLogger<TrabalhadorAgente>.Instance, config, OpcoesRapidas, gestor,
            executor ?? new ExecutorFake());
    }

    private sealed class ExecutorFake : IExecutorOperacao
    {
        public string? UltimaOperacao { get; private set; }

        public Task<MensagemResultado> ExecutarAsync(JsonElement pedido, CancellationToken ct)
        {
            UltimaOperacao = pedido.TryGetProperty("operacao", out var o) ? o.GetString() : null;
            var id = pedido.TryGetProperty("id_requisicao", out var i) ? i.GetString() : null;
            return Task.FromResult(new MensagemResultado
            {
                IdRequisicao = id,
                Status = "ok",
                Linhas = new List<IReadOnlyDictionary<string, object?>>(),
                DuracaoMs = 1,
            });
        }
    }
}
