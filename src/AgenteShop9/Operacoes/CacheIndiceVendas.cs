using Microsoft.Extensions.Logging;

namespace AgenteShop9.Operacoes;

/// <summary>
/// Cache do indice de vendas (quantidade vendida por produto na janela de 59
/// dias) por escopo = OrdemFilial (issue #179, ADR 0010). Nao conhece SQL: quem
/// usa entrega o delegate que constroi o indice.
///
/// Diferencas em relacao ao <see cref="CacheIndiceFotos"/>:
/// - A requisicao NUNCA espera. Sem indice pronto, <see cref="Obter"/> dispara a
///   construcao em segundo plano e devolve nulo (o agente responde
///   `ranking_indisponivel` e o broker cai na ordem alfabetica).
/// - A chave NAO inclui a versao do catalogo: o indice so depende da filial, e
///   um catalogo novo nao pode zerar o cache (o de fotos zera, este nao).
///
/// Igual ao de fotos:
/// - Indice expirado (~10 min) continua sendo servido; a renovacao roda em
///   segundo plano, uma por escopo por vez (stale-while-revalidate).
/// - Uma construcao por vez em todo o cache (semaforo global): o aquecimento
///   dispara varias filiais no boot, junto com o Shop9.
/// - Falha ou timeout mantem o indice antigo e so tenta de novo depois do
///   recuo, sem laco apertado.
/// </summary>
public sealed class CacheIndiceVendas(
    ILogger log, OpcoesCacheIndiceVendas? opcoes = null, Func<DateTimeOffset>? agora = null)
    : IDisposable
{
    public delegate Task<IReadOnlyDictionary<int, decimal>> Construtor(CancellationToken ct);

    private sealed record Entrada(IReadOnlyDictionary<int, decimal> Vendas, DateTimeOffset Expira);

    private readonly OpcoesCacheIndiceVendas _opcoes = opcoes ?? new();
    private readonly Func<DateTimeOffset> _agora = agora ?? (() => DateTimeOffset.UtcNow);
    private readonly CancellationTokenSource _encerramento = new();
    private readonly object _trava = new();
    private readonly SemaphoreSlim _umaPorVez = new(1, 1);
    private readonly Dictionary<string, Entrada> _entradas = new();
    private readonly Dictionary<string, Task> _emAndamento = new();
    private readonly Dictionary<string, DateTimeOffset> _proximaTentativa = new();

    /// <summary>
    /// Devolve o indice do escopo na hora, sem esperar. Nulo = ainda nao ha
    /// indice (a construcao foi disparada, ou esta em recuo apos falha).
    /// Indice expirado e devolvido e a renovacao comeca em segundo plano.
    /// </summary>
    public IReadOnlyDictionary<int, decimal>? Obter(string chave, Construtor construir)
    {
        lock (_trava)
        {
            _entradas.TryGetValue(chave, out var entrada);
            var precisa = entrada is null || _agora() >= entrada.Expira;
            if (precisa && PodeIniciarLocked(chave))
                IniciarLocked(chave, construir);
            return entrada?.Vendas;
        }
    }

    /// <summary>
    /// Aquecimento: dispara a construcao se o escopo nao tem indice nem
    /// construcao em andamento. Nao bloqueia; a tarefa devolvida nunca falha.
    /// </summary>
    public Task Aquecer(string chave, Construtor construir)
    {
        lock (_trava)
        {
            if (_entradas.ContainsKey(chave))
                return Task.CompletedTask;
            if (_emAndamento.TryGetValue(chave, out var existente))
                return existente;
            return IniciarLocked(chave, construir);
        }
    }

    private bool PodeIniciarLocked(string chave) =>
        !_emAndamento.ContainsKey(chave) && !_encerramento.IsCancellationRequested
        && (!_proximaTentativa.TryGetValue(chave, out var depois) || _agora() >= depois);

    private Task IniciarLocked(string chave, Construtor construir)
    {
        // Task.Run: nada da construcao roda sincronamente dentro da trava.
        var tarefa = Task.Run(() => AtualizarAsync(chave, construir));
        // Tarefa observada: nunca vira excecao nao observada, e quem espera o
        // aquecimento nunca recebe falha.
        var observada = tarefa.ContinueWith(t =>
        {
            var falhou = t.IsFaulted && !_encerramento.IsCancellationRequested;
            bool haIndiceAnterior;
            lock (_trava)
            {
                haIndiceAnterior = _entradas.ContainsKey(chave);
                if (falhou)
                    _proximaTentativa[chave] = _agora() + _opcoes.Recuo;
                _emAndamento.Remove(chave);
            }
            if (falhou)
                // So o tipo: a mensagem pode trazer detalhe do banco (spec 3.4).
                log.LogWarning(
                    "Atualizacao do indice de vendas falhou ({Tipo}); {Situacao} e tenta de novo apos {RecuoSegundos:0}s.",
                    t.Exception!.GetBaseException().GetType().Name,
                    haIndiceAnterior ? "mantem o indice anterior" : "ainda nao ha indice",
                    _opcoes.Recuo.TotalSeconds);
            else if (t.IsFaulted)
                _ = t.Exception;
        }, TaskScheduler.Default);
        _emAndamento[chave] = observada;
        return observada;
    }

    private async Task AtualizarAsync(string chave, Construtor construir)
    {
        CancellationToken ct;
        try
        {
            ct = _encerramento.Token;
        }
        catch (ObjectDisposedException)
        {
            throw new OperationCanceledException();
        }

        await _umaPorVez.WaitAsync(ct);
        try
        {
            var vendas = await construir(ct);
            lock (_trava)
            {
                _entradas[chave] = new Entrada(vendas, _agora() + _opcoes.Validade);
                _proximaTentativa.Remove(chave);
                if (_entradas.Count > _opcoes.MaxEscopos)
                    _entradas.Remove(_entradas
                        .Where(par => par.Key != chave).MinBy(par => par.Value.Expira)!.Key);
            }
        }
        finally
        {
            _umaPorVez.Release();
        }
    }

    public void Dispose()
    {
        _encerramento.Cancel();
        _encerramento.Dispose();
        // Construcao em andamento pode ainda tocar o semaforo ao terminar (Release/WaitAsync);
        // descartar so o libera recursos nao gerenciados, e ela ja esta cancelada.
        _umaPorVez.Dispose();
    }
}

/// <summary>Valores padrao: validade de 10 min (ADR 0010) e recuo de 60 s apos falha.</summary>
public sealed record OpcoesCacheIndiceVendas
{
    public TimeSpan Validade { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan Recuo { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxEscopos { get; init; } = 32;
}
