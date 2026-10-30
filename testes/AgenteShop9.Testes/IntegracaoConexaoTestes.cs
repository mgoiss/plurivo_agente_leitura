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

    [Fact]
    public async Task Robo_executa_duas_requisicoes_ao_mesmo_tempo_e_anuncia_o_limite()
    {
        await using var servidor = new BackendDeMentira();
        await servidor.IniciarAsync();

        // Cada execucao so termina quando as duas estao dentro: em serie, trava.
        var executor = new ExecutorEmEncontro(2);
        var opcoes = OpcoesRapidas with { ExecucoesSimultaneas = 2 };
        var robo = CriarRobo(servidor.UrlAgente, "token-de-teste", executor, opcoes);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await robo.StartAsync(cts.Token);
        try
        {
            await servidor.EsperarConexaoAsync(cts.Token);
            var hb = await servidor.EsperarMensagemDoTipoAsync("heartbeat", cts.Token);
            using (var docHb = JsonDocument.Parse(hb))
                Assert.Equal(2, docHb.RootElement.GetProperty("execucoes_simultaneas").GetInt32());

            await servidor.EnviarAoAgenteAsync("""{"tipo":"executar","id_requisicao":"a","operacao":"x","parametros":{}}""");
            await servidor.EnviarAoAgenteAsync("""{"tipo":"executar","id_requisicao":"b","operacao":"x","parametros":{}}""");

            var ids = new HashSet<string?>();
            for (var n = 0; n < 2; n++)
            {
                using var doc = JsonDocument.Parse(await servidor.EsperarMensagemDoTipoAsync("resultado", cts.Token));
                ids.Add(doc.RootElement.GetProperty("id_requisicao").GetString());
            }

            Assert.Equal(new HashSet<string?> { "a", "b" }, ids);
        }
        finally
        {
            await robo.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Robo_nao_passa_do_limite_de_execucoes_simultaneas()
    {
        await using var servidor = new BackendDeMentira();
        await servidor.IniciarAsync();

        var executor = new ExecutorContador();
        var opcoes = OpcoesRapidas with { ExecucoesSimultaneas = 2 };
        var robo = CriarRobo(servidor.UrlAgente, "token-de-teste", executor, opcoes);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await robo.StartAsync(cts.Token);
        try
        {
            await servidor.EsperarConexaoAsync(cts.Token);
            await servidor.EsperarMensagemDoTipoAsync("heartbeat", cts.Token);

            for (var n = 0; n < 6; n++)
                await servidor.EnviarAoAgenteAsync("{\"tipo\":\"executar\",\"id_requisicao\":\"r" + n + "\",\"operacao\":\"x\",\"parametros\":{}}");
            for (var n = 0; n < 6; n++)
                await servidor.EsperarMensagemDoTipoAsync("resultado", cts.Token);

            Assert.Equal(2, executor.Pico);
        }
        finally
        {
            await robo.StopAsync(CancellationToken.None);
        }
    }

    private static TrabalhadorAgente CriarRobo(
        Uri url, string token, IExecutorOperacao? executor = null, OpcoesAgente? opcoes = null)
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
        var baixador = new AgenteShop9.Catalogos.BaixadorCatalogo(
            NullLogger<AgenteShop9.Catalogos.BaixadorCatalogo>.Instance, config, gestor, new HttpClient());

        return new TrabalhadorAgente(
            NullLogger<TrabalhadorAgente>.Instance, config, opcoes ?? OpcoesRapidas, gestor, baixador,
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

    private static MensagemResultado Ok(JsonElement pedido) => new()
    {
        IdRequisicao = pedido.TryGetProperty("id_requisicao", out var i) ? i.GetString() : null,
        Status = "ok",
        Linhas = new List<IReadOnlyDictionary<string, object?>>(),
        DuracaoMs = 1,
    };

    /// <summary>Segura cada execucao ate `n` estarem dentro ao mesmo tempo.</summary>
    private sealed class ExecutorEmEncontro(int n) : IExecutorOperacao
    {
        private readonly Barrier _barreira = new(n);

        public Task<MensagemResultado> ExecutarAsync(JsonElement pedido, CancellationToken ct)
        {
            var resultado = Ok(pedido);
            return Task.Run(() =>
            {
                if (!_barreira.SignalAndWait(TimeSpan.FromSeconds(5), ct))
                    throw new TimeoutException("execucoes nao ficaram simultaneas");
                return resultado;
            }, ct);
        }
    }

    /// <summary>Guarda o maior numero de execucoes ao mesmo tempo.</summary>
    private sealed class ExecutorContador : IExecutorOperacao
    {
        private int _dentro;
        public int Pico { get; private set; }

        public async Task<MensagemResultado> ExecutarAsync(JsonElement pedido, CancellationToken ct)
        {
            var resultado = Ok(pedido);
            lock (this) Pico = Math.Max(Pico, Interlocked.Increment(ref _dentro));
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref _dentro);
            return resultado;
        }
    }
}
