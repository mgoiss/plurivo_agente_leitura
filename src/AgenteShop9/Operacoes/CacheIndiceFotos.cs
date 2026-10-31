using Microsoft.Extensions.Logging;

namespace AgenteShop9.Operacoes;

/// <summary>
/// Cache do indice de fotos validas por escopo (issue #119). Nao conhece SQL:
/// quem usa entrega delegates que constroem o indice e (opcional) leem a
/// impressao digital barata do escopo.
///
/// - Indice expirado continua sendo servido (stale-while-revalidate); a
///   atualizacao roda em segundo plano, uma por escopo por vez.
/// - So a primeira carga de um escopo (sem indice nenhum) faz o chamador
///   esperar; chamadores simultaneos compartilham a mesma construcao.
/// - Se a impressao digital for igual a da ultima construcao, so renova a
///   validade sem reler as fotos — mas forca releitura completa a cada
///   <see cref="OpcoesCacheIndiceFotos.MaxRenovacoes"/> renovacoes ou apos
///   <see cref="OpcoesCacheIndiceFotos.IdadeMaxima"/>, porque a impressao
///   (contagem, maior ordem, soma de tamanhos, checksum de ordem/posicao) nao
///   enxerga troca de foto por outra de mesmo tamanho.
/// - Uma construcao por vez em todo o cache (semaforo global): o aquecimento
///   dispara varios escopos no boot, junto com o Shop9, e cada indice ocupa
///   dezenas de MB. A primeira carga de um escopo novo pode esperar a de outro.
///   A espera na fila nao conta para o limite de tempo da construcao, que e
///   cronometrado dentro do proprio construtor.
/// - Falha na atualizacao mantem o indice antigo e so tenta de novo depois do
///   recuo, sem laco apertado.
/// </summary>
public sealed class CacheIndiceFotos(
    ILogger log, OpcoesCacheIndiceFotos? opcoes = null, Func<DateTimeOffset>? agora = null)
    : IDisposable
{
    public delegate Task<IReadOnlyDictionary<int, int>> Construtor(CancellationToken ct);

    /// <summary>Texto que muda quando o conjunto elegivel de fotos muda.</summary>
    public delegate Task<string> LeitorImpressao(CancellationToken ct);

    private sealed record Entrada(
        IReadOnlyDictionary<int, int> Fotos, DateTimeOffset Expira, string? Impressao,
        int Renovacoes, DateTimeOffset ConstruidoEm);

    private readonly OpcoesCacheIndiceFotos _opcoes = opcoes ?? new();
    private readonly Func<DateTimeOffset> _agora = agora ?? (() => DateTimeOffset.UtcNow);
    private readonly CancellationTokenSource _encerramento = new();
    private readonly object _trava = new();
    private readonly SemaphoreSlim _umaPorVez = new(1, 1);
    private readonly Dictionary<string, Entrada> _entradas = new();
    private readonly Dictionary<string, Task<IReadOnlyDictionary<int, int>>> _emAndamento = new();
    private readonly Dictionary<string, DateTimeOffset> _proximaTentativa = new();

    /// <summary>
    /// Devolve o indice do escopo. Sem entrada, espera a primeira construcao;
    /// com entrada (mesmo expirada), devolve na hora e atualiza em segundo plano.
    /// `impressao` nulo = catalogo sem a operacao: atualiza relendo tudo.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, int>> ObterAsync(
        string chave, Construtor construir, LeitorImpressao? impressao, CancellationToken ct)
    {
        Task<IReadOnlyDictionary<int, int>> primeiraCarga;
        lock (_trava)
        {
            if (_entradas.TryGetValue(chave, out var entrada))
            {
                if (_agora() >= entrada.Expira && !_emAndamento.ContainsKey(chave)
                    && (!_proximaTentativa.TryGetValue(chave, out var depois) || _agora() >= depois))
                    IniciarLocked(chave, construir, impressao);
                return entrada.Fotos;
            }
            primeiraCarga = _emAndamento.TryGetValue(chave, out var existente)
                ? existente
                : IniciarLocked(chave, construir, impressao);
        }
        // A espera respeita o cancelamento da requisicao; a construcao nao:
        // ela e compartilhada e pertence ao cache.
        return await primeiraCarga.WaitAsync(ct);
    }

    /// <summary>
    /// Aquecimento: dispara a construcao se o escopo nao tem indice nem
    /// construcao em andamento. Nao bloqueia; a tarefa devolvida nunca falha.
    /// </summary>
    public Task Aquecer(string chave, Construtor construir, LeitorImpressao? impressao)
    {
        Task<IReadOnlyDictionary<int, int>> tarefa;
        lock (_trava)
        {
            if (_entradas.ContainsKey(chave))
                return Task.CompletedTask;
            tarefa = _emAndamento.TryGetValue(chave, out var existente)
                ? existente
                : IniciarLocked(chave, construir, impressao);
        }
        return tarefa.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private Task<IReadOnlyDictionary<int, int>> IniciarLocked(
        string chave, Construtor construir, LeitorImpressao? impressao)
    {
        // Task.Run: nada da atualizacao roda sincronamente dentro da trava.
        var tarefa = Task.Run(() => AtualizarAsync(chave, construir, impressao));
        _emAndamento[chave] = tarefa;
        // Observa a falha para nunca virar excecao nao observada; quem espera
        // (primeira carga) ainda recebe a excecao pelo proprio await.
        _ = tarefa.ContinueWith(t =>
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
                    "Atualizacao do indice de fotos falhou ({Tipo}); {Situacao} e tenta de novo apos {RecuoSegundos:0}s.",
                    t.Exception!.GetBaseException().GetType().Name,
                    haIndiceAnterior ? "mantem o indice anterior" : "ainda nao ha indice",
                    _opcoes.Recuo.TotalSeconds);
            else if (t.IsFaulted)
                _ = t.Exception;
        }, TaskScheduler.Default);
        return tarefa;
    }

    private async Task<IReadOnlyDictionary<int, int>> AtualizarAsync(
        string chave, Construtor construir, LeitorImpressao? impressao)
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

        // Fila global: impressao digital e construcao rodam sob o mesmo semaforo.
        await _umaPorVez.WaitAsync(ct);
        try
        {
            return await AtualizarSerializadoAsync(chave, construir, impressao, ct);
        }
        finally
        {
            _umaPorVez.Release();
        }
    }

    private async Task<IReadOnlyDictionary<int, int>> AtualizarSerializadoAsync(
        string chave, Construtor construir, LeitorImpressao? impressao, CancellationToken ct)
    {
        Entrada? atual;
        lock (_trava)
            _entradas.TryGetValue(chave, out atual);

        // A impressao e lida ANTES da construcao: se o banco mudar no meio, a
        // impressao guardada fica velha e a proxima checagem reconstroi.
        string? nova = null;
        if (impressao is not null)
            try
            {
                nova = await impressao(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Impressao digital do indice de fotos falhou ({Tipo}); relendo as fotos.",
                    ex.GetType().Name);
            }

        if (atual is not null && nova is not null && nova == atual.Impressao
            && atual.Renovacoes < _opcoes.MaxRenovacoes
            && _agora() - atual.ConstruidoEm < _opcoes.IdadeMaxima)
        {
            lock (_trava)
                _entradas[chave] = atual with
                {
                    Expira = _agora() + _opcoes.Validade, Renovacoes = atual.Renovacoes + 1,
                };
            return atual.Fotos;
        }

        var fotos = await construir(ct);
        lock (_trava)
        {
            var agora = _agora();
            _entradas[chave] = new Entrada(fotos, agora + _opcoes.Validade, nova, 0, agora);
            _proximaTentativa.Remove(chave);
            if (_entradas.Count > _opcoes.MaxEscopos)
                _entradas.Remove(_entradas
                    .Where(par => par.Key != chave).MinBy(par => par.Value.Expira)!.Key);
        }
        return fotos;
    }

    public void Dispose()
    {
        _encerramento.Cancel();
        // O token ja cancelado continua valido para quem o copiou; o semaforo
        // nao tem handle alocado, nao precisa de Dispose.
        _encerramento.Dispose();
    }
}

/// <summary>Valores padrao: validade de 5 min (igual ao TTL anterior), recuo de 15 s, ate 12 renovacoes (~1 h).</summary>
public sealed record OpcoesCacheIndiceFotos
{
    public TimeSpan Validade { get; init; } = TimeSpan.FromSeconds(300);
    public TimeSpan Recuo { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxRenovacoes { get; init; } = 12;
    public TimeSpan IdadeMaxima { get; init; } = TimeSpan.FromHours(1);
    public int MaxEscopos { get; init; } = 16;
}
