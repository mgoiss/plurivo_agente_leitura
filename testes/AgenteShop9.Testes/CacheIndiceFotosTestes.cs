using AgenteShop9.Operacoes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenteShop9.Testes;

/// <summary>Issue #119: cache do indice de fotos com stale-while-revalidate.</summary>
public class CacheIndiceFotosTestes
{
    private sealed class Relogio
    {
        public DateTimeOffset Agora { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private static IReadOnlyDictionary<int, int> Indice(int marca) =>
        new Dictionary<int, int> { [1] = marca };

    private sealed class Fonte
    {
        public int Construcoes;
        public int Impressoes;
        public string ImpressaoAtual = "a";
        public int Marca = 1;
        public bool FalharConstrucao;
        public bool FalharImpressao;
        public TaskCompletionSource? Portao;

        public CacheIndiceFotos.Construtor Construtor => async ct =>
        {
            Interlocked.Increment(ref Construcoes);
            if (Portao is not null)
                await Portao.Task.WaitAsync(ct);
            if (FalharConstrucao)
                throw new InvalidOperationException("falha simulada");
            return Indice(Marca);
        };

        public CacheIndiceFotos.LeitorImpressao Leitor => _ =>
        {
            Interlocked.Increment(ref Impressoes);
            if (FalharImpressao)
                throw new InvalidOperationException("falha simulada");
            return Task.FromResult(ImpressaoAtual);
        };
    }

    private static CacheIndiceFotos Novo(Relogio relogio, OpcoesCacheIndiceFotos? opcoes = null) =>
        new(NullLogger.Instance, opcoes, () => relogio.Agora);

    private static async Task Esperar(Func<bool> condicao)
    {
        for (var i = 0; i < 200 && !condicao(); i++)
            await Task.Delay(10);
        Assert.True(condicao(), "condicao nao atendida a tempo");
    }

    [Fact]
    public async Task Primeira_carga_espera_a_construcao_e_chamadores_simultaneos_compartilham()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte { Portao = new(TaskCreationOptions.RunContinuationsAsynchronously) };

        var a = cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);
        var b = cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);
        await Task.Delay(50);
        Assert.False(a.IsCompleted);
        Assert.False(b.IsCompleted);

        fonte.Portao.SetResult();
        Assert.Equal(1, (await a)[1]);
        Assert.Equal(1, (await b)[1]);
        Assert.Equal(1, fonte.Construcoes);
    }

    [Fact]
    public async Task Primeira_carga_cancelada_pela_requisicao_nao_derruba_a_construcao_compartilhada()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte { Portao = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var cts = new CancellationTokenSource();

        var cancelada = cache.ObterAsync("k", fonte.Construtor, null, cts.Token);
        var outra = cache.ObterAsync("k", fonte.Construtor, null, default);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelada);

        fonte.Portao.SetResult();
        Assert.Equal(1, (await outra)[1]);
        Assert.Equal(1, fonte.Construcoes);
    }

    [Fact]
    public async Task Dentro_da_validade_serve_sem_construir_de_novo()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte();
        await cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);
        relogio.Agora += TimeSpan.FromSeconds(299);

        await cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);

        Assert.Equal(1, fonte.Construcoes);
        Assert.Equal(1, fonte.Impressoes);
    }

    [Fact]
    public async Task Expirado_serve_o_antigo_e_dispara_uma_unica_reconstrucao_com_varias_requisicoes()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte();
        await cache.ObterAsync("k", fonte.Construtor, null, default);
        relogio.Agora += TimeSpan.FromSeconds(301);
        fonte.Marca = 2;
        fonte.Portao = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var respostas = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => cache.ObterAsync("k", fonte.Construtor, null, default))));

        Assert.All(respostas, r => Assert.Equal(1, r[1]));   // antigo, sem esperar o portao
        await Esperar(() => fonte.Construcoes == 2);
        await Task.Delay(50);
        Assert.Equal(2, fonte.Construcoes);                   // uma so, nunca em paralelo

        fonte.Portao.SetResult();
        await Esperar(() => cache.ObterAsync("k", fonte.Construtor, null, default).Result[1] == 2);
        Assert.Equal(2, fonte.Construcoes);
    }

    [Fact]
    public async Task Falha_da_reconstrucao_mantem_o_indice_antigo_e_so_tenta_de_novo_apos_o_recuo()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte();
        await cache.ObterAsync("k", fonte.Construtor, null, default);
        relogio.Agora += TimeSpan.FromSeconds(301);
        fonte.FalharConstrucao = true;

        Assert.Equal(1, (await cache.ObterAsync("k", fonte.Construtor, null, default))[1]);
        await Esperar(() => fonte.Construcoes == 2);
        await Task.Delay(50);

        // Dentro do recuo: serve o antigo e nao tenta de novo.
        for (var i = 0; i < 5; i++)
            Assert.Equal(1, (await cache.ObterAsync("k", fonte.Construtor, null, default))[1]);
        await Task.Delay(50);
        Assert.Equal(2, fonte.Construcoes);

        // Passado o recuo, tenta de novo e, se o banco voltou, troca o indice.
        relogio.Agora += TimeSpan.FromSeconds(16);
        fonte.FalharConstrucao = false;
        fonte.Marca = 3;
        await cache.ObterAsync("k", fonte.Construtor, null, default);
        await Esperar(() => cache.ObterAsync("k", fonte.Construtor, null, default).Result[1] == 3);
    }

    [Fact]
    public async Task Falha_na_primeira_carga_chega_ao_chamador_e_a_proxima_requisicao_tenta_de_novo()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte { FalharConstrucao = true };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.ObterAsync("k", fonte.Construtor, null, default));
        fonte.FalharConstrucao = false;

        Assert.Equal(1, (await cache.ObterAsync("k", fonte.Construtor, null, default))[1]);
        Assert.Equal(2, fonte.Construcoes);
    }

    [Fact]
    public async Task Impressao_igual_renova_a_validade_sem_reler_as_fotos()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte();
        await cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);

        relogio.Agora += TimeSpan.FromSeconds(301);
        await cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);
        await Esperar(() => fonte.Impressoes == 2);
        await Task.Delay(50);
        Assert.Equal(1, fonte.Construcoes);

        // Validade renovada: dentro dela nem a impressao e consultada.
        relogio.Agora += TimeSpan.FromSeconds(100);
        await cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);
        await Task.Delay(50);
        Assert.Equal(2, fonte.Impressoes);
        Assert.Equal(1, fonte.Construcoes);
    }

    [Fact]
    public async Task Impressao_diferente_reconstroi()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte();
        await cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);

        relogio.Agora += TimeSpan.FromSeconds(301);
        fonte.ImpressaoAtual = "b";
        fonte.Marca = 2;
        await cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);

        await Esperar(() => cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default).Result[1] == 2);
        Assert.Equal(2, fonte.Construcoes);
    }

    [Fact]
    public async Task Falha_na_impressao_cai_na_releitura_completa()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte();
        await cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);

        relogio.Agora += TimeSpan.FromSeconds(301);
        fonte.FalharImpressao = true;
        fonte.Marca = 2;
        await cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);

        await Esperar(() => cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default).Result[1] == 2);
    }

    [Fact]
    public async Task Operacao_ausente_no_catalogo_reconstrui_lendo_tudo_a_cada_expiracao()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte();
        await cache.ObterAsync("k", fonte.Construtor, null, default);

        relogio.Agora += TimeSpan.FromSeconds(301);
        await cache.ObterAsync("k", fonte.Construtor, null, default);

        await Esperar(() => fonte.Construcoes == 2);
        Assert.Equal(0, fonte.Impressoes);
    }

    [Fact]
    public async Task Forca_releitura_completa_apos_o_numero_maximo_de_renovacoes()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio, new OpcoesCacheIndiceFotos { MaxRenovacoes = 2 });
        var fonte = new Fonte();
        await cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);

        for (var i = 1; i <= 2; i++)
        {
            relogio.Agora += TimeSpan.FromSeconds(301);
            await cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);
            await Esperar(() => fonte.Impressoes == i + 1);
            await Task.Delay(30);
        }
        Assert.Equal(1, fonte.Construcoes);

        relogio.Agora += TimeSpan.FromSeconds(301);
        await cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);
        await Esperar(() => fonte.Construcoes == 2);
    }

    [Fact]
    public async Task Forca_releitura_completa_apos_o_teto_de_idade()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte();
        await cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);

        relogio.Agora += TimeSpan.FromMinutes(61);
        await cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);

        await Esperar(() => fonte.Construcoes == 2);
    }

    [Fact]
    public async Task Aquecimento_nao_bloqueia_e_a_primeira_requisicao_aproveita_a_construcao()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte { Portao = new(TaskCreationOptions.RunContinuationsAsynchronously) };

        var aquecimento = cache.Aquecer("k", fonte.Construtor, fonte.Leitor);
        Assert.False(aquecimento.IsCompleted);

        var requisicao = cache.ObterAsync("k", fonte.Construtor, fonte.Leitor, default);
        fonte.Portao.SetResult();
        Assert.Equal(1, (await requisicao)[1]);
        await aquecimento;
        Assert.Equal(1, fonte.Construcoes);

        // Com indice pronto, aquecer de novo nao faz nada.
        await cache.Aquecer("k", fonte.Construtor, fonte.Leitor);
        Assert.Equal(1, fonte.Construcoes);
    }

    [Fact]
    public async Task Falha_no_aquecimento_nao_propaga()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte { FalharConstrucao = true };

        await cache.Aquecer("k", fonte.Construtor, null);   // nao lanca
        Assert.Equal(1, fonte.Construcoes);
    }

    [Fact]
    public async Task Desligar_cancela_a_construcao_em_segundo_plano()
    {
        var relogio = new Relogio();
        var cache = Novo(relogio);
        var fonte = new Fonte { Portao = new(TaskCreationOptions.RunContinuationsAsynchronously) };

        var aquecimento = cache.Aquecer("k", fonte.Construtor, null);
        await Esperar(() => fonte.Construcoes == 1);
        cache.Dispose();

        await aquecimento.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static CacheIndiceFotos.Construtor ConstrutorContado(
        int[] atual, int[] maximo, TaskCompletionSource portao, Func<Task>? aoComecar = null) => async ct =>
    {
        var agora = Interlocked.Increment(ref atual[0]);
        int visto;
        while (agora > (visto = Volatile.Read(ref maximo[0]))
               && Interlocked.CompareExchange(ref maximo[0], agora, visto) != visto) { }
        try
        {
            if (aoComecar is not null)
                await aoComecar();
            await portao.Task.WaitAsync(ct);
            return Indice(1);
        }
        finally
        {
            Interlocked.Decrement(ref atual[0]);
        }
    };

    [Fact]
    public async Task Escopos_diferentes_nunca_constroem_em_paralelo()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var portao = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int[] atual = [0], maximo = [0], iniciadas = [0];
        var construtor = ConstrutorContado(atual, maximo, portao, () =>
        {
            Interlocked.Increment(ref iniciadas[0]);
            return Task.CompletedTask;
        });

        var aquecimentos = Enumerable.Range(0, 8)
            .Select(i => cache.Aquecer($"k{i}", construtor, null)).ToArray();
        await Esperar(() => Volatile.Read(ref iniciadas[0]) == 1);
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref iniciadas[0]));   // as outras esperam na fila

        portao.SetResult();
        await Task.WhenAll(aquecimentos).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, maximo[0]);
        Assert.Equal(8, Volatile.Read(ref iniciadas[0]));

        // Expiradas juntas: a reconstrucao em segundo plano tambem e uma por vez.
        relogio.Agora += TimeSpan.FromSeconds(301);
        foreach (var i in Enumerable.Range(0, 8))
            await cache.ObterAsync($"k{i}", construtor, null, default);
        await Esperar(() => Volatile.Read(ref iniciadas[0]) == 16);
        await Task.Delay(100);
        Assert.Equal(1, maximo[0]);
    }

    [Fact]
    public async Task Espera_na_fila_nao_consome_o_tempo_de_quem_constroi_e_o_expirado_nao_espera()
    {
        // O limite de 12 s vive no construtor (cronometro proprio, iniciado ao
        // comecar a construir): o cache so chama o construtor depois de obter a vez.
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var primeiro = new Fonte { Portao = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var segundo = new Fonte();

        var aquecimento = cache.Aquecer("a", primeiro.Construtor, null);
        await Esperar(() => primeiro.Construcoes == 1);
        var espera = cache.ObterAsync("b", segundo.Construtor, null, default);
        await Task.Delay(100);
        Assert.Equal(0, segundo.Construcoes);   // construtor nem foi chamado na fila
        Assert.False(espera.IsCompleted);

        primeiro.Portao.SetResult();
        await aquecimento;
        Assert.Equal(1, (await espera.WaitAsync(TimeSpan.FromSeconds(5)))[1]);
        Assert.Equal(1, segundo.Construcoes);

        // Expirado de "b" com a fila ocupada: serve o antigo na hora.
        relogio.Agora += TimeSpan.FromSeconds(301);
        primeiro.Portao = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocupando = cache.Aquecer("c", primeiro.Construtor, null);
        await Esperar(() => primeiro.Construcoes == 2);
        Assert.Equal(1, (await cache.ObterAsync("b", segundo.Construtor, null, default))[1]);
        primeiro.Portao.SetResult();
        await ocupando;
    }

    [Fact]
    public async Task Desligar_durante_a_espera_na_fila_libera_sem_travar()
    {
        var relogio = new Relogio();
        var cache = Novo(relogio);
        var ocupada = new Fonte { Portao = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var naFila = new Fonte();

        var primeiro = cache.Aquecer("a", ocupada.Construtor, null);
        await Esperar(() => ocupada.Construcoes == 1);
        var segundo = cache.Aquecer("b", naFila.Construtor, null);
        var requisicao = cache.ObterAsync("b", naFila.Construtor, null, default);
        await Task.Delay(50);

        cache.Dispose();

        await primeiro.WaitAsync(TimeSpan.FromSeconds(5));
        await segundo.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => requisicao.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, naFila.Construcoes);
    }
}
