using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgenteShop9.Testes;

/// <summary>
/// Servidor WebSocket minimo (ws://) para os testes de integracao do robo.
/// Nao e o backend real — so o suficiente para observar: chegou conexao? veio o
/// cabecalho Authorization? chegou heartbeat? E permite derrubar a conexao para
/// testar a reconexao.
/// </summary>
public sealed class BackendDeMentira : IAsyncDisposable
{
    private readonly Channel<Handshake> _conexoes = Channel.CreateUnbounded<Handshake>();
    private readonly Channel<string> _mensagens = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _encerrar = new();
    private WebApplication? _app;
    private volatile WebSocket? _conexaoAtual;

    public Uri UrlAgente { get; private set; } = null!;

    public sealed record Handshake(string? Authorization);

    public async Task IniciarAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0"); // porta 0 = o SO escolhe uma livre
        _app.UseWebSockets();
        _app.Map("/agente", TratarAsync);

        await _app.StartAsync();

        var http = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        UrlAgente = new Uri(http.Replace("http://", "ws://") + "/agente");
    }

    private async Task TratarAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        await _conexoes.Writer.WriteAsync(
            new Handshake(context.Request.Headers.Authorization.ToString()));

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        _conexaoAtual = ws;

        var buffer = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open && !_encerrar.IsCancellationRequested)
            {
                var resultado = await ws.ReceiveAsync(buffer, _encerrar.Token);
                if (resultado.MessageType == WebSocketMessageType.Close) break;

                await _mensagens.Writer.WriteAsync(
                    Encoding.UTF8.GetString(buffer, 0, resultado.Count));

                if (ws.State == WebSocketState.Open)
                {
                    var resposta = Encoding.UTF8.GetBytes("{\"tipo\":\"heartbeat_ok\"}");
                    await ws.SendAsync(resposta, WebSocketMessageType.Text, true, _encerrar.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    public ValueTask<Handshake> EsperarConexaoAsync(CancellationToken ct) =>
        _conexoes.Reader.ReadAsync(ct);

    public ValueTask<string> EsperarMensagemAsync(CancellationToken ct) =>
        _mensagens.Reader.ReadAsync(ct);

    public async Task DerrubarConexaoAtualAsync()
    {
        var ws = _conexaoAtual;
        if (ws is null) return;

        try
        {
            await ws.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "teste", CancellationToken.None);
        }
        catch
        {
            try { ws.Abort(); } catch { /* ja foi */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _encerrar.CancelAsync();
        if (_app is not null) await _app.DisposeAsync();
        _encerrar.Dispose();
    }
}
