using AgenteShop9.Operacoes;

namespace AgenteShop9.Testes;

/// <summary>Issue #182: indice de fotos por metadados + cache de validacao (sem SQL e sem GDI+).</summary>
public class ConstrutorIndiceFotosPorMetadadosTestes : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private string Caminho => Path.Combine(_pasta, "dados", "validacao_fotos.json");
    private DateTimeOffset _agora = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    private TimeSpan _decorrido = TimeSpan.Zero;

    // Banco de mentira: Ordem da foto -> texto. "ok" valida; qualquer outro texto e invalido.
    private readonly Dictionary<int, string> _banco = new();
    private readonly List<int> _leituras = new();
    private int _validacoes;
    private TimeSpan _custoPorLeitura = TimeSpan.Zero;

    public void Dispose()
    {
        if (Directory.Exists(_pasta)) Directory.Delete(_pasta, true);
    }

    private CacheValidacaoFotos NovoCache() => new(Caminho, () => _agora);

    private static MetadadoFoto M(int produto, int foto, long tamanho = 100) => new(produto, foto, tamanho);

    private Task<string?> Ler(int ordem, CancellationToken ct)
    {
        _leituras.Add(ordem);
        _decorrido += _custoPorLeitura;
        return Task.FromResult(_banco.TryGetValue(ordem, out var t) ? t : null);
    }

    private bool Validar(string? texto)
    {
        _validacoes++;
        return texto == "ok";
    }

    private Task<(IReadOnlyDictionary<int, int> Fotos, ResumoConstrucaoIndiceFotos Resumo)> Construir(
        CacheValidacaoFotos cache, IReadOnlyList<MetadadoFoto> metadados,
        TimeSpan? tempoMaximo = null) =>
        ConstrutorIndiceFotosPorMetadados.ConstruirAsync(
            metadados, cache, Ler, Validar, () => _decorrido,
            tempoMaximo ?? TimeSpan.FromSeconds(12), CancellationToken.None);

    private void Banco(params int[] validas)
    {
        foreach (var ordem in validas)
            _banco[ordem] = "ok";
    }

    [Fact]
    public async Task CA11_segunda_construcao_com_cache_cheio_le_zero_fotos_e_so_a_nova_e_lida()
    {
        Banco(1, 2, 3);
        var metadados = new[] { M(10, 1), M(20, 2), M(30, 3) };
        var cache = NovoCache();

        var (primeira, resumo1) = await Construir(cache, metadados);
        Assert.Equal(3, resumo1.LidasDoBanco);
        Assert.Equal(0, resumo1.DoCache);
        Assert.Equal(3, primeira.Count);

        _leituras.Clear();
        _validacoes = 0;
        var (segunda, resumo2) = await Construir(cache, metadados);
        Assert.Empty(_leituras);
        Assert.Equal(0, _validacoes);
        Assert.Equal(0, resumo2.LidasDoBanco);
        Assert.Equal(3, resumo2.DoCache);
        Assert.Equal(3, resumo2.ProdutosComFoto);
        Assert.Equal(primeira, segunda);

        Banco(4);
        var com_nova = metadados.Append(M(40, 4)).ToArray();
        var (terceira, resumo3) = await Construir(cache, com_nova);
        Assert.Equal(new[] { 4 }, _leituras);
        Assert.Equal(1, resumo3.LidasDoBanco);
        Assert.Equal(3, resumo3.DoCache);
        Assert.Equal(4, resumo3.TotalFotos);
        Assert.Equal(4, terceira.Count);
    }

    [Fact]
    public async Task CA12_tamanho_alterado_e_revalidado()
    {
        Banco(1, 2);
        var cache = NovoCache();
        await Construir(cache, [M(10, 1, 100), M(20, 2, 100)]);

        _leituras.Clear();
        _banco[2] = "corrompida"; // foto trocada: tamanho e conteudo novos
        var (fotos, resumo) = await Construir(cache, [M(10, 1, 100), M(20, 2, 150)]);

        Assert.Equal(new[] { 2 }, _leituras);
        Assert.Equal(1, resumo.DoCache);
        Assert.Equal(new Dictionary<int, int> { [10] = 1 }, fotos);
    }

    [Fact]
    public async Task CA12_foto_removida_sai_do_indice_na_hora_e_do_cache_apos_vencer()
    {
        Banco(1, 2);
        var cache = NovoCache();
        await Construir(cache, [M(10, 1), M(20, 2)]);
        Assert.Equal(2, cache.Quantidade);

        var (fotos, _) = await Construir(cache, [M(10, 1)]);

        Assert.Equal(new Dictionary<int, int> { [10] = 1 }, fotos);
        Assert.Equal(2, cache.Quantidade); // dentro das 24 h pode ser de outro escopo

        _agora += TimeSpan.FromHours(25);
        await Construir(cache, [M(10, 1)]);
        Assert.Equal(1, cache.Quantidade);
        Assert.Equal(1, NovoCache().Quantidade); // gravado em disco, ja podado
    }

    [Fact]
    public async Task Dois_escopos_alternando_nao_apagam_o_cache_um_do_outro()
    {
        for (var i = 1; i <= 6; i++)
            Banco(i);
        var escopoA = new[] { M(10, 1), M(20, 2), M(30, 3), M(40, 4) };
        var escopoB = new[] { M(30, 3), M(40, 4), M(50, 5), M(60, 6) }; // 5 e 6 so no B, 1 e 2 so no A
        var cache = NovoCache();

        var lidas = new List<int>();
        foreach (var escopo in new[] { escopoA, escopoB, escopoA, escopoB, escopoA, escopoB })
        {
            _leituras.Clear();
            await Construir(cache, escopo);
            lidas.Add(_leituras.Count);
        }

        // A le 4; B so as 2 exclusivas dele; a partir da 3a construcao, zero leituras.
        Assert.Equal(new[] { 4, 2, 0, 0, 0, 0 }, lidas);
    }

    [Fact]
    public async Task CA13_nova_instancia_reaproveita_o_arquivo_em_disco()
    {
        Banco(1, 2);
        await Construir(NovoCache(), [M(10, 1), M(20, 2)]);

        _leituras.Clear();
        var (fotos, resumo) = await Construir(NovoCache(), [M(10, 1), M(20, 2)]);

        Assert.Empty(_leituras);
        Assert.Equal(2, resumo.DoCache);
        Assert.Equal(2, fotos.Count);
    }

    [Fact]
    public async Task CA13_arquivo_corrompido_nao_quebra_e_a_construcao_le_tudo()
    {
        Banco(1);
        Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
        File.WriteAllText(Caminho, "{{{ lixo");

        var (fotos, resumo) = await Construir(NovoCache(), [M(10, 1)]);

        Assert.Equal(1, resumo.LidasDoBanco);
        Assert.Single(fotos);
    }

    [Fact]
    public async Task Entrada_vencida_apos_24_horas_e_relida()
    {
        Banco(1);
        var cache = NovoCache();
        await Construir(cache, [M(10, 1)]);

        _agora += TimeSpan.FromHours(25);
        _leituras.Clear();
        var (_, resumo) = await Construir(cache, [M(10, 1)]);

        Assert.Equal(new[] { 1 }, _leituras);
        Assert.Equal(1, resumo.LidasDoBanco);
    }

    [Fact]
    public async Task Foto_invalida_em_cache_nao_e_relida()
    {
        _banco[1] = "lixo";
        _banco[2] = "ok";
        var metadados = new[] { M(10, 1), M(10, 2) };
        var cache = NovoCache();
        await Construir(cache, metadados);

        _leituras.Clear();
        var (fotos, resumo) = await Construir(cache, metadados);

        Assert.Empty(_leituras);
        Assert.Equal(2, resumo.DoCache);
        Assert.Equal(2, fotos[10]);
    }

    [Fact]
    public async Task Produto_com_primeira_foto_invalida_cai_na_segunda_e_para_na_primeira_valida()
    {
        _banco[1] = "lixo";
        Banco(2, 3);

        var (fotos, resumo) = await Construir(NovoCache(), [M(10, 1), M(10, 2), M(10, 3)]);

        Assert.Equal(new Dictionary<int, int> { [10] = 2 }, fotos);
        Assert.Equal(new[] { 1, 2 }, _leituras); // a terceira nao e lida
        Assert.Equal(2, resumo.LidasDoBanco);
        Assert.Equal(3, resumo.TotalFotos);
    }

    [Fact]
    public async Task Foto_que_sumiu_entre_metadados_e_leitura_e_invalida_e_nao_vai_para_o_cache()
    {
        Banco(2);
        var cache = NovoCache();
        // Ordem 1 nao esta no banco de mentira: a leitura devolve nulo.
        var (fotos, _) = await Construir(cache, [M(10, 1), M(10, 2)]);

        Assert.Equal(2, fotos[10]);
        Assert.False(cache.TentarObter(1, 100, out _));
        Assert.True(cache.TentarObter(2, 100, out _));
    }

    [Fact]
    public async Task Limite_de_fotos_por_produto_continua_valendo()
    {
        for (var i = 1; i <= 9; i++)
            _banco[i] = "lixo";
        var metadados = Enumerable.Range(1, 9).Select(i => M(10, i)).ToArray();

        var erro = await Assert.ThrowsAsync<IndiceFotosCapacidadeException>(
            () => Construir(NovoCache(), metadados));
        Assert.Equal("fotos_por_produto", erro.Motivo);
    }

    [Fact]
    public async Task Limite_de_linhas_continua_valendo()
    {
        var metadados = Enumerable.Range(1, AcumuladorIndiceFotosPublicas.MaxLinhas + 1)
            .Select(i => M(i, i)).ToArray();
        var cache = NovoCache();
        // Tudo ja conhecido no cache: o limite de linhas vale mesmo sem ler nada.
        foreach (var m in metadados)
            cache.Registrar(m.ImagemOrdem, m.Caracteres, true);

        var erro = await Assert.ThrowsAsync<IndiceFotosCapacidadeException>(
            () => Construir(cache, metadados));
        Assert.Equal("linhas", erro.Motivo);
    }

    [Fact]
    public async Task Limite_de_tamanho_da_foto_continua_valendo()
    {
        var erro = await Assert.ThrowsAsync<IndiceFotosCapacidadeException>(
            () => Construir(NovoCache(), [M(10, 1, IndiceFotosPublicas.MaxCaracteres + 1)]));
        Assert.Equal("tamanho_foto", erro.Motivo);
    }

    [Fact]
    public async Task Volume_de_texto_conta_so_o_que_foi_lido()
    {
        // 600 fotos de 1.000.000 de caracteres passam do limite de 512 MiB se forem lidas;
        // vindas do cache nao pesam, e o indice sai completo.
        const long tamanho = IndiceFotosPublicas.MaxCaracteres;
        var metadados = Enumerable.Range(1, 600).Select(i => M(i, i, tamanho)).ToArray();
        var cache = NovoCache();
        foreach (var m in metadados)
            cache.Registrar(m.ImagemOrdem, m.Caracteres, true);

        var (fotos, resumo) = await Construir(cache, metadados);
        Assert.Equal(600, fotos.Count);
        Assert.Equal(0, resumo.LidasDoBanco);

        File.Delete(Caminho); // a construcao acima gravou o cache em disco
        for (var i = 1; i <= 600; i++)
            Banco(i);
        var erro = await Assert.ThrowsAsync<IndiceFotosCapacidadeException>(
            () => Construir(NovoCache(), metadados));
        Assert.Equal("volume_texto", erro.Motivo);
    }

    [Fact]
    public async Task Tempo_estourado_grava_o_progresso_para_a_proxima_tentativa()
    {
        for (var i = 1; i <= 10; i++)
            Banco(i);
        var metadados = Enumerable.Range(1, 10).Select(i => M(i, i)).ToArray();
        _custoPorLeitura = TimeSpan.FromSeconds(5); // cada leitura gasta 5 s: so 3 cabem em 12 s

        var cache = NovoCache();
        var tempo = await Assert.ThrowsAsync<IndiceFotosTempoException>(() => Construir(cache, metadados));
        Assert.Equal(_leituras.Count, tempo.LidasDoBanco);
        Assert.Equal(0, tempo.DoCache);

        var lidasAteAqui = _leituras.Count;
        Assert.InRange(lidasAteAqui, 1, 9);
        Assert.Equal(lidasAteAqui, NovoCache().Quantidade);

        // Proxima tentativa (do zero em tempo) avanca: so le o que faltou.
        _leituras.Clear();
        _decorrido = TimeSpan.Zero;
        _custoPorLeitura = TimeSpan.Zero;
        var (fotos, resumo) = await Construir(NovoCache(), metadados);
        Assert.Equal(10, fotos.Count);
        Assert.Equal(10 - lidasAteAqui, resumo.LidasDoBanco);
        Assert.Equal(lidasAteAqui, resumo.DoCache);
    }

    [Fact]
    public async Task Cancelamento_propaga()
    {
        Banco(1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConstrutorIndiceFotosPorMetadados.ConstruirAsync(
                [M(10, 1)], NovoCache(), Ler, Validar, () => _decorrido,
                TimeSpan.FromSeconds(12), cts.Token));
    }

    [Fact]
    public async Task Sem_metadados_devolve_indice_vazio_e_limpa_o_cache_so_apos_vencer()
    {
        var cache = NovoCache();
        cache.Registrar(1, 100, true);

        var (fotos, resumo) = await Construir(cache, []);

        Assert.Empty(fotos);
        Assert.Equal(0, resumo.TotalFotos);
        Assert.Equal(1, cache.Quantidade);

        _agora += TimeSpan.FromHours(25);
        await Construir(cache, []);
        Assert.Equal(0, cache.Quantidade);
    }

    // ---- Aquecimento paralelo da leitura fria (issue #182) ----

    private readonly System.Collections.Concurrent.ConcurrentQueue<int> _leiturasSeguras = new();
    private int _simultaneas;
    private int _maxSimultaneas;

    private async Task<string?> LerSeguro(int ordem, CancellationToken ct)
    {
        _leiturasSeguras.Enqueue(ordem);
        var agora = Interlocked.Increment(ref _simultaneas);
        int visto;
        while ((visto = Volatile.Read(ref _maxSimultaneas)) < agora
               && Interlocked.CompareExchange(ref _maxSimultaneas, agora, visto) != visto) { }
        await Task.Delay(20, ct);
        Interlocked.Decrement(ref _simultaneas);
        lock (_banco)
            return _banco.TryGetValue(ordem, out var t) ? t : null;
    }

    private static bool ValidarSeguro(string? texto) => texto == "ok";

    private Task<(IReadOnlyDictionary<int, int> Fotos, ResumoConstrucaoIndiceFotos Resumo)> ConstruirParalelo(
        CacheValidacaoFotos cache, IReadOnlyList<MetadadoFoto> metadados, int paralelismo,
        TimeSpan? tempoMaximo = null, Func<TimeSpan>? decorrido = null) =>
        ConstrutorIndiceFotosPorMetadados.ConstruirAsync(
            metadados, cache, LerSeguro, ValidarSeguro, decorrido ?? (() => TimeSpan.Zero),
            tempoMaximo ?? TimeSpan.FromSeconds(12), CancellationToken.None, paralelismo);

    [Fact]
    public async Task Paralelo_a_frio_da_o_mesmo_indice_que_o_sequencial_e_le_em_paralelo()
    {
        var metadados = new List<MetadadoFoto>();
        for (var p = 1; p <= 40; p++)
        {
            Banco(p * 10 + 1);
            metadados.Add(M(p, p * 10 + 1));
        }
        var sequencial = await Construir(NovoCache(), metadados);

        _banco.Clear();
        foreach (var m in metadados) Banco(m.ImagemOrdem);
        var cache = new CacheValidacaoFotos(Path.Combine(_pasta, "paralelo.json"), () => _agora);
        var (fotos, resumo) = await ConstruirParalelo(cache, metadados, paralelismo: 4);

        Assert.Equal(sequencial.Fotos, fotos);
        Assert.InRange(_maxSimultaneas, 2, 4);
        Assert.Equal(40, _leiturasSeguras.Count);
        // Lidas pelo aquecimento contam como lidas do banco, nao como cache.
        Assert.Equal(new ResumoConstrucaoIndiceFotos(40, 40, 0, 40), resumo);
    }

    [Fact]
    public async Task Paralelo_nao_le_foto_extra_de_produto_cuja_primeira_e_valida()
    {
        Banco(1, 2, 3);
        var cache = NovoCache();

        await ConstruirParalelo(cache, [M(10, 1), M(10, 2), M(20, 3)], paralelismo: 4);

        Assert.Equal([1, 3], _leiturasSeguras.Order().ToArray());
    }

    [Fact]
    public async Task Paralelo_primeira_invalida_cai_na_segunda_na_passada_sequencial()
    {
        _banco[1] = "lixo";
        Banco(2);
        var cache = NovoCache();

        var (fotos, resumo) = await ConstruirParalelo(cache, [M(10, 1), M(10, 2)], paralelismo: 4);

        Assert.Equal(2, fotos[10]);
        Assert.Equal(2, resumo.LidasDoBanco);
        Assert.Equal(0, resumo.DoCache);
        Assert.Equal([1, 2], _leiturasSeguras.Order().ToArray());
    }

    [Fact]
    public async Task Paralelo_com_cache_cheio_nao_le_nada()
    {
        Banco(1, 2);
        var metadados = new[] { M(10, 1), M(20, 2) };
        var cache = NovoCache();
        await ConstruirParalelo(cache, metadados, paralelismo: 4);
        while (_leiturasSeguras.TryDequeue(out _)) { }

        var (_, resumo) = await ConstruirParalelo(cache, metadados, paralelismo: 4);

        Assert.Empty(_leiturasSeguras);
        Assert.Equal(new ResumoConstrucaoIndiceFotos(2, 0, 2, 2), resumo);
    }

    [Fact]
    public async Task Paralelo_que_estoura_o_tempo_guarda_o_progresso_e_lanca_o_timeout()
    {
        var metadados = new List<MetadadoFoto>();
        for (var p = 1; p <= 30; p++)
        {
            Banco(p);
            metadados.Add(M(p, p));
        }
        var cache = NovoCache();
        var relogio = new System.Diagnostics.Stopwatch();
        relogio.Start();

        // 60 ms de orcamento com 20 ms por leitura e 2 em paralelo: nao da para ler as 30.
        await Assert.ThrowsAsync<IndiceFotosTempoException>(() =>
            ConstruirParalelo(cache, metadados, paralelismo: 2,
                tempoMaximo: TimeSpan.FromMilliseconds(60), decorrido: () => relogio.Elapsed));

        Assert.InRange(NovoCache().Quantidade, 1, 29); // o que foi lido ficou em disco
    }

    [Fact]
    public async Task Paralelo_respeita_o_limite_de_volume_de_texto_das_fotos_lidas_no_aquecimento()
    {
        var metadados = new List<MetadadoFoto>();
        for (var p = 1; p <= 600; p++) // 600 x 1.000.000 > 512 x 1024 x 1024
        {
            Banco(p);
            metadados.Add(M(p, p, 1_000_000));
        }

        var ex = await Assert.ThrowsAsync<IndiceFotosCapacidadeException>(() =>
            ConstruirParalelo(NovoCache(), metadados, paralelismo: 4));

        Assert.Equal("volume_texto", ex.Motivo);
    }

    [Fact]
    public async Task Paralelo_que_estoura_o_tempo_conta_as_lidas_do_aquecimento_no_erro()
    {
        var metadados = new List<MetadadoFoto>();
        for (var p = 1; p <= 30; p++)
        {
            Banco(p);
            metadados.Add(M(p, p));
        }
        var relogio = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<IndiceFotosTempoException>(() =>
            ConstruirParalelo(NovoCache(), metadados, paralelismo: 2,
                tempoMaximo: TimeSpan.FromMilliseconds(60), decorrido: () => relogio.Elapsed));

        Assert.True(ex.LidasDoBanco > 0);
    }

    [Fact]
    public async Task Paralelo_avisa_quando_o_aquecimento_falha_e_a_passada_normal_segue()
    {
        Banco(1, 2);
        Exception? falha = null;
        var jaFalhou = false;
        Task<string?> LerQuebrado(int ordem, CancellationToken ct)
        {
            if (ordem == 1 && !jaFalhou)
            {
                jaFalhou = true;
                throw new InvalidOperationException("banco");
            }
            return LerSeguro(ordem, ct);
        }

        var (fotos, _) = await ConstrutorIndiceFotosPorMetadados.ConstruirAsync(
            [M(10, 1), M(20, 2)], NovoCache(), LerQuebrado, ValidarSeguro, () => TimeSpan.Zero,
            TimeSpan.FromSeconds(12), CancellationToken.None, 2, (e, _) => falha = e);

        Assert.IsType<InvalidOperationException>(falha);
        Assert.Contains(20, fotos.Keys); // a foto 2 foi lida pelo aquecimento ou pela passada normal
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Foto_sem_memoria_fica_fora_do_indice_mas_nao_vai_para_o_cache(int paralelismo)
    {
        Banco(1);
        var cache = NovoCache();

        var (fotos, _) = await ConstrutorIndiceFotosPorMetadados.ConstruirAsync(
            [M(10, 1)], cache, LerSeguro, _ => throw new FotoSemMemoriaException(),
            () => TimeSpan.Zero, TimeSpan.FromSeconds(12), CancellationToken.None, paralelismo);

        Assert.Empty(fotos);
        Assert.Equal(0, cache.Quantidade); // nada gravado: a proxima construcao tenta de novo
    }
}
