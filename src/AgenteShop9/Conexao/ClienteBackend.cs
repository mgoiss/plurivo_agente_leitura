using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AgenteShop9.Conexao;

/// <summary>
/// Uma conexao WebSocket com o backend. Uma instancia por conexao; se cair, o
/// TrabalhadorAgente cria outra.
///
/// ISSUE #3 / spec 3.6: a validacao do certificado TLS do servidor fica ATIVA
/// (e o padrao do .NET). NAO definir Options.RemoteCertificateValidationCallback
/// para aceitar tudo — proibido, mesmo em teste. Para teste local, usar ws://
/// (sem TLS) ou um certificado de teste valido.
/// </summary>
public sealed class ClienteBackend : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();

    public ClienteBackend(string tokenAgente)
    {
        // Autenticacao no handshake — mesmo formato que o backend (issue #1) espera.
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {tokenAgente}");
    }

    public WebSocketState Estado => _ws.State;

    public Task ConectarAsync(Uri url, CancellationToken ct) => _ws.ConnectAsync(url, ct);

    public async Task EnviarJsonAsync<T>(T mensagem, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(mensagem);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    /// <summary>
    /// Le mensagens do backend ate a conexao fechar ou o token cancelar. Nesta
    /// versao o conteudo e so repassado ao callback (para log); roteamento de
    /// operacoes vem em tarefa posterior.
    /// </summary>
    public async Task ReceberAteFecharAsync(Action<string> aoReceber, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult resultado;
            do
            {
                resultado = await _ws.ReceiveAsync(buffer, ct);
                if (resultado.MessageType == WebSocketMessageType.Close)
                {
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    return;
                }

                ms.Write(buffer, 0, resultado.Count);
            } while (!resultado.EndOfMessage);

            aoReceber(Encoding.UTF8.GetString(ms.ToArray()));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch
        {
            // fechando de qualquer jeito
        }

        _ws.Dispose();
    }
}
