using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using AgenteShop9.Catalogos;
using AgenteShop9.Conexao;
using AgenteShop9.Configuracao;
using AgenteShop9.Mensagens;
using AgenteShop9.Operacoes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgenteShop9;

/// <summary>
/// O robo em si: conecta no backend, manda heartbeat, executa operacoes do
/// catalogo e reconecta sozinho. Roda como serviço do Windows (ver Program.cs).
/// Itens 1.2 e 1.4 do roteiro.
/// </summary>
public sealed class TrabalhadorAgente(
    ILogger<TrabalhadorAgente> log,
    ConfiguracaoAgente config,
    OpcoesAgente opcoes,
    GestorCatalogo gestorCatalogo,
    BaixadorCatalogo baixadorCatalogo,
    IExecutorOperacao executor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        gestorCatalogo.Inicializar();
        // Issue #119: aquece o indice de fotos sem esperar e sem atrasar a conexao.
        (executor as IAquecedorIndiceFotos)?.Aquecer();

        var url = new Uri(config.BackendUrl);
        var politica = new PoliticaReconexao(opcoes.ReconexaoEsperaInicial, opcoes.ReconexaoEsperaMaxima);

        // Item 4.1: verificacao de catalogo novo roda em paralelo, independente
        // da sessao WebSocket (e uma chamada HTTPS separada — nao precisa do
        // /agente conectado para funcionar).
        var verificacaoCatalogo = baixadorCatalogo.ExecutarPeriodicamenteAsync(
            opcoes.IntervaloVerificacaoCatalogo, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UmaSessaoAsync(url, politica, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning("Conexao com o backend caiu ou falhou: {Motivo}", ex.Message);
            }

            if (stoppingToken.IsCancellationRequested) break;

            var espera = politica.ProximaEspera();
            log.LogInformation("Reconectando em {Segundos:0}s.", espera.TotalSeconds);
            try
            {
                await Task.Delay(espera, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await verificacaoCatalogo;
    }

    private async Task UmaSessaoAsync(Uri url, PoliticaReconexao politica, CancellationToken stoppingToken)
    {
        await using var cliente = new ClienteBackend(config.TokenAgente);

        log.LogInformation("Conectando ao backend em {Url} ...", url);
        await cliente.ConectarAsync(url, stoppingToken);
        politica.Resetar();
        log.LogInformation("Conectado ao backend.");

        using var sessao = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        var vagas = new SemaphoreSlim(Math.Max(1, opcoes.ExecucoesSimultaneas));
        var emAndamento = new ConcurrentDictionary<Task, byte>();

        var heartbeat = LacoHeartbeatAsync(cliente, sessao.Token);
        var recepcao = cliente.ReceberAteFecharAsync(
            msg => TratarMensagemAsync(cliente, msg, vagas, emAndamento, sessao.Token), sessao.Token);

        var terminou = await Task.WhenAny(heartbeat, recepcao);
        await sessao.CancelAsync();                       // encerra os outros lacos e as execucoes
        await Task.WhenAll(heartbeat, recepcao).ContinueWith(
            _ => { }, TaskScheduler.Default);             // ignora o cancelamento esperado
        await Task.WhenAll(emAndamento.Keys).ContinueWith(
            _ => { }, TaskScheduler.Default);             // espera as execucoes soltarem a conexao
        vagas.Dispose();
        await terminou;                                   // re-lanca a causa real da queda, se houve
    }

    private async Task TratarMensagemAsync(
        ClienteBackend cliente, string msgJson, SemaphoreSlim vagas,
        ConcurrentDictionary<Task, byte> emAndamento, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(msgJson);
        }
        catch (JsonException)
        {
            log.LogWarning("Mensagem do backend nao e JSON valido; ignorando.");
            return;
        }

        using (doc)
        {
            var tipo = doc.RootElement.TryGetProperty("tipo", out var t) ? t.GetString() : null;
            switch (tipo)
            {
                case "heartbeat_ok":
                    return; // ack do proprio heartbeat

                case "executar":
                    // Roda fora do laco de recepcao: o laco segue lendo e outras
                    // requisicoes executam em paralelo, ate o limite de vagas.
                    var idExecucao = doc.RootElement.TryGetProperty("id_requisicao", out var ie) ? ie.GetString() : null;
                    var tarefa = ExecutarEmParaleloAsync(cliente, msgJson, idExecucao, vagas, ct);
                    emAndamento[tarefa] = 0;
                    _ = tarefa.ContinueWith(
                        t => emAndamento.TryRemove(t, out _), TaskScheduler.Default);
                    return;

                default:
                    log.LogWarning("Tipo de mensagem nao suportado: {Tipo}", tipo ?? "(ausente)");
                    var id = doc.RootElement.TryGetProperty("id_requisicao", out var i) ? i.GetString() : null;
                    if (id is not null)
                        await cliente.EnviarJsonAsync(
                            MensagemResultado.Erro(id, "tipo_desconhecido", "tipo de mensagem nao suportado."), ct);
                    return;
            }
        }
    }

    private async Task ExecutarEmParaleloAsync(
        ClienteBackend cliente, string msgJson, string? idRequisicao, SemaphoreSlim vagas, CancellationToken ct)
    {
        try
        {
            await vagas.WaitAsync(ct);
            try
            {
                using var doc = JsonDocument.Parse(msgJson);
                var resposta = await executor.ExecutarAsync(doc.RootElement, ct);
                await cliente.EnviarJsonAsync(resposta, ct);
            }
            finally
            {
                vagas.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // sessao encerrando; o backend trata a queda da conexao
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Falha ao executar a requisicao {Id}.", idRequisicao);
            if (idRequisicao is null) return;
            try
            {
                await cliente.EnviarJsonAsync(
                    MensagemResultado.Erro(idRequisicao, "erro_interno", "falha ao executar a operacao."), ct);
            }
            catch (Exception)
            {
                // conexao ja caiu
            }
        }
    }

    private async Task LacoHeartbeatAsync(ClienteBackend cliente, CancellationToken ct)
    {
        var versao = VersaoAgente.Atual;
        while (!ct.IsCancellationRequested && cliente.Estado == WebSocketState.Open)
        {
            await cliente.EnviarJsonAsync(
                new Heartbeat
                {
                    VersaoAgente = versao,
                    VersaoCatalogo = gestorCatalogo.VersaoAceita,
                    ExecucoesSimultaneas = Math.Max(1, opcoes.ExecucoesSimultaneas),
                }, ct);
            log.LogDebug("Heartbeat enviado.");
            await Task.Delay(opcoes.IntervaloHeartbeat, ct);
        }
    }
}
