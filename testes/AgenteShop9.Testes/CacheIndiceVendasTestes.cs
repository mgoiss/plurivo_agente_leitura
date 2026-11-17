using System.Text.Json;
using AgenteShop9.Operacoes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenteShop9.Testes;

/// <summary>Issue #179: cache do indice de vendas, stale-while-revalidate, sem espera na requisicao.</summary>
public class CacheIndiceVendasTestes
{
    private sealed class Relogio
    {
        public DateTimeOffset Agora { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Fonte
    {
        public int Construcoes;
        public int Marca = 1;
        public bool Falhar;
        public TaskCompletionSource? Portao;

        public CacheIndiceVendas.Construtor Construtor => async ct =>
        {
            Interlocked.Increment(ref Construcoes);
            if (Portao is not null)
                await Portao.Task.WaitAsync(ct);
            if (Falhar)
                throw new InvalidOperationException("falha simulada");
            return new Dictionary<int, decimal> { [1] = Marca };
        };
    }

    private static CacheIndiceVendas Novo(Relogio relogio, OpcoesCacheIndiceVendas? opcoes = null) =>
        new(NullLogger.Instance, opcoes, () => relogio.Agora);

    private static async Task Esperar(Func<bool> condicao)
    {
        for (var i = 0; i < 300 && !condicao(); i++)
            await Task.Delay(10);
        Assert.True(condicao(), "condicao nao atendida a tempo");
    }

    private static async Task<decimal> EsperarIndice(CacheIndiceVendas cache, string chave, Fonte fonte)
    {
        IReadOnlyDictionary<int, decimal>? v = null;
        await Esperar(() => (v = cache.Obter(chave, fonte.Construtor)) is not null);
        return v![1];
    }

    [Fact]
    public async Task Sem_indice_devolve_nulo_na_hora_e_dispara_uma_construcao_em_segundo_plano()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte { Portao = new(TaskCreationOptions.RunContinuationsAsynchronously) };

        Assert.Null(cache.Obter("2", fonte.Construtor));
        Assert.Null(cache.Obter("2", fonte.Construtor));
        await Esperar(() => fonte.Construcoes == 1);
        Assert.Null(cache.Obter("2", fonte.Construtor));
        Assert.Equal(1, fonte.Construcoes);

        fonte.Portao.SetResult();
        Assert.Equal(1m, await EsperarIndice(cache, "2", fonte));
        Assert.Equal(1, fonte.Construcoes);
    }

    [Fact]
    public async Task CA3_depois_da_primeira_carga_nenhuma_requisicao_espera_a_reconstrucao_lenta()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte();
        Assert.Equal(1m, await EsperarIndice(cache, "2", fonte));

        // Expira; o construtor passa a ser lento (portao fechado) e devolve outra marca.
        relogio.Agora += TimeSpan.FromMinutes(11);
        fonte.Marca = 2;
        fonte.Portao = new(TaskCreationOptions.RunContinuationsAsynchronously);

        for (var i = 0; i < 20; i++)
        {
            var obtido = await Task.Run(() => cache.Obter("2", fonte.Construtor)).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1m, obtido![1]);   // serve o antigo, sem esperar
        }
        await Esperar(() => fonte.Construcoes == 2);
        Assert.Equal(2, fonte.Construcoes);   // uma reconstrucao so, mesmo com 20 requisicoes

        fonte.Portao.SetResult();
        await Esperar(() => cache.Obter("2", fonte.Construtor)![1] == 2m);
    }

    [Fact]
    public async Task Indice_dentro_da_validade_nao_reconstroi()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte();
        await EsperarIndice(cache, "2", fonte);

        relogio.Agora += TimeSpan.FromMinutes(9);
        for (var i = 0; i < 5; i++)
            Assert.NotNull(cache.Obter("2", fonte.Construtor));
        await Task.Delay(50);
        Assert.Equal(1, fonte.Construcoes);
    }

    [Fact]
    public async Task CA6_falha_na_renovacao_mantem_o_indice_antigo_e_respeita_o_recuo()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio, new OpcoesCacheIndiceVendas { Recuo = TimeSpan.FromSeconds(60) });
        var fonte = new Fonte();
        await EsperarIndice(cache, "2", fonte);

        relogio.Agora += TimeSpan.FromMinutes(11);
        fonte.Falhar = true;
        Assert.Equal(1m, cache.Obter("2", fonte.Construtor)![1]);
        await Esperar(() => fonte.Construcoes == 2);
        await Task.Delay(100);   // deixa a falha ser registrada

        // Dentro do recuo: serve o antigo e NAO tenta de novo (sem laco apertado).
        for (var i = 0; i < 10; i++)
            Assert.Equal(1m, cache.Obter("2", fonte.Construtor)![1]);
        await Task.Delay(50);
        Assert.Equal(2, fonte.Construcoes);

        // Passado o recuo: tenta de novo, ainda mantem o antigo enquanto falha.
        relogio.Agora += TimeSpan.FromSeconds(61);
        Assert.Equal(1m, cache.Obter("2", fonte.Construtor)![1]);
        await Esperar(() => fonte.Construcoes == 3);

        // Quando volta a funcionar, o indice novo entra.
        await Task.Delay(100);
        fonte.Falhar = false;
        fonte.Marca = 7;
        relogio.Agora += TimeSpan.FromSeconds(61);
        await Esperar(() => cache.Obter("2", fonte.Construtor)![1] == 7m);
    }

    [Fact]
    public async Task CA6_timeout_da_construcao_vale_como_falha_e_mantem_o_antigo()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte();
        await EsperarIndice(cache, "2", fonte);
        relogio.Agora += TimeSpan.FromMinutes(11);

        var construcoes = 0;
        CacheIndiceVendas.Construtor estoura = _ =>
        {
            Interlocked.Increment(ref construcoes);
            throw new TimeoutException("simulado");
        };
        Assert.Equal(1m, cache.Obter("2", estoura)![1]);
        await Esperar(() => construcoes == 1);
        await Task.Delay(100);
        Assert.Equal(1m, cache.Obter("2", estoura)![1]);
        await Task.Delay(50);
        Assert.Equal(1, construcoes);
    }

    [Fact]
    public async Task Sem_indice_e_com_falha_na_primeira_carga_continua_nulo_e_respeita_o_recuo()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte { Falhar = true };

        Assert.Null(cache.Obter("2", fonte.Construtor));
        await Esperar(() => fonte.Construcoes == 1);
        await Task.Delay(100);
        for (var i = 0; i < 10; i++)
            Assert.Null(cache.Obter("2", fonte.Construtor));
        await Task.Delay(50);
        Assert.Equal(1, fonte.Construcoes);
    }

    [Fact]
    public async Task CA7_filiais_tem_indices_separados()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var a = new Fonte { Marca = 10 };
        var b = new Fonte { Marca = 20 };

        Assert.Equal(10m, await EsperarIndice(cache, "1", a));
        Assert.Equal(20m, await EsperarIndice(cache, "2", b));
        Assert.Equal(10m, cache.Obter("1", a.Construtor)![1]);
        Assert.Equal(20m, cache.Obter("2", b.Construtor)![1]);
        Assert.Equal(1, a.Construcoes);
        Assert.Equal(1, b.Construcoes);
    }

    [Fact]
    public async Task Falha_numa_filial_nao_atrapalha_a_outra()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var ruim = new Fonte { Falhar = true };
        var boa = new Fonte();
        Assert.Null(cache.Obter("1", ruim.Construtor));
        Assert.Equal(1m, await EsperarIndice(cache, "2", boa));
    }

    [Fact]
    public async Task Aquecer_constroi_uma_vez_e_nao_repete_se_ja_ha_indice()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var fonte = new Fonte();

        await cache.Aquecer("2", fonte.Construtor);
        await cache.Aquecer("2", fonte.Construtor);
        Assert.Equal(1, fonte.Construcoes);
        Assert.Equal(1m, cache.Obter("2", fonte.Construtor)![1]);
    }

    [Fact]
    public async Task Aquecer_de_construcao_que_falha_nao_lanca()
    {
        using var cache = Novo(new Relogio());
        await cache.Aquecer("2", new Fonte { Falhar = true }.Construtor);
    }

    [Fact]
    public void Chave_e_so_a_filial_e_nao_inclui_a_versao_do_catalogo()
    {
        var parametros = new Dictionary<string, JsonElement>
        {
            ["OrdemFilial"] = JsonSerializer.SerializeToElement(2),
            ["Tabela_01"] = JsonSerializer.SerializeToElement(7),
        };
        Assert.Equal("2", ExecutorOperacao.ChaveVendas(parametros));
        Assert.Throws<ArgumentException>(() =>
            ExecutorOperacao.ChaveVendas(new Dictionary<string, JsonElement>()));
    }

    [Fact]
    public async Task Indice_sobrevive_a_troca_de_catalogo_porque_a_chave_nao_muda()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var v41 = new Fonte { Marca = 5 };
        await EsperarIndice(cache, "2", v41);

        // "Catalogo novo": outro construtor, mesma chave. Continua servido sem reconstruir.
        var v42 = new Fonte { Marca = 6 };
        Assert.Equal(5m, cache.Obter("2", v42.Construtor)![1]);
        await Task.Delay(50);
        Assert.Equal(0, v42.Construcoes);
    }

    [Fact]
    public async Task Filial_sem_vendas_indice_vazio_e_valido_e_nao_devolve_nulo()
    {
        var relogio = new Relogio();
        using var cache = Novo(relogio);
        var construcoes = 0;
        CacheIndiceVendas.Construtor vazio = _ =>
        {
            Interlocked.Increment(ref construcoes);
            return Task.FromResult<IReadOnlyDictionary<int, decimal>>(new Dictionary<int, decimal>());
        };

        Assert.Null(cache.Obter("7", vazio));
        IReadOnlyDictionary<int, decimal>? indice = null;
        await Esperar(() => (indice = cache.Obter("7", vazio)) is not null);
        Assert.Empty(indice!);
        Assert.NotNull(cache.Obter("7", vazio));
        Assert.Equal(1, construcoes);
    }
}
