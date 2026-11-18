using AgenteShop9.Operacoes;

namespace AgenteShop9.Testes;

public class CacheValidacaoFotosTestes : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private string Caminho => Path.Combine(_pasta, "dados", "validacao_fotos.json");
    private DateTimeOffset _agora = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private CacheValidacaoFotos Novo() => new(Caminho, () => _agora);

    public void Dispose()
    {
        if (Directory.Exists(_pasta)) Directory.Delete(_pasta, true);
    }

    [Fact]
    public void Guarda_e_devolve_por_ordem_e_tamanho()
    {
        var cache = Novo();
        cache.Registrar(10, 500, true);
        cache.Registrar(11, 700, false);

        Assert.True(cache.TentarObter(10, 500, out var valida10));
        Assert.True(valida10);
        Assert.True(cache.TentarObter(11, 700, out var valida11));
        Assert.False(valida11);
        Assert.False(cache.TentarObter(12, 1, out _));
    }

    [Fact]
    public void Tamanho_diferente_nao_reaproveita()
    {
        var cache = Novo();
        cache.Registrar(10, 500, true);
        Assert.False(cache.TentarObter(10, 501, out _));
    }

    [Fact]
    public void Entrada_vale_no_minimo_12_horas_e_no_maximo_24_e_depois_e_revalidada()
    {
        var cache = Novo();
        cache.Registrar(10, 500, true);

        _agora += TimeSpan.FromHours(11) + TimeSpan.FromMinutes(59);
        Assert.True(cache.TentarObter(10, 500, out _));

        _agora += TimeSpan.FromHours(12) + TimeSpan.FromMinutes(2);
        Assert.False(cache.TentarObter(10, 500, out _));
    }

    [Fact]
    public void Nova_instancia_le_o_arquivo_gravado()
    {
        var a = Novo();
        a.Registrar(10, 500, true);
        a.Registrar(11, 700, false);
        a.Gravar();

        var b = Novo();
        Assert.Equal(2, b.Quantidade);
        Assert.True(b.TentarObter(10, 500, out var valida));
        Assert.True(valida);
        Assert.True(b.TentarObter(11, 700, out var invalida));
        Assert.False(invalida);
        Assert.False(File.Exists(Caminho + ".tmp"));
    }

    [Fact]
    public void Gravar_com_ordens_vivas_so_poda_o_que_sumiu_E_ja_venceu()
    {
        var a = Novo();
        a.Registrar(10, 500, true);
        a.Registrar(11, 700, true);

        // Ausente da construcao, mas dentro das 24 h: fica (pode ser de outro escopo).
        a.Gravar(new HashSet<int> { 10 });
        Assert.Equal(2, Novo().Quantidade);

        // Vencida e ausente: sai. A viva fica mesmo vencida.
        _agora += ValidadeMaxima + TimeSpan.FromMinutes(1);
        a.Gravar(new HashSet<int> { 10 });

        var b = Novo();
        Assert.Equal(1, b.Quantidade);
        Assert.False(b.TentarObter(11, 700, out _));
        _agora -= ValidadeMaxima + TimeSpan.FromMinutes(1);
        Assert.True(b.TentarObter(10, 500, out _));
    }

    private static TimeSpan Validade => CacheValidacaoFotos.Validade;
    private static TimeSpan ValidadeMaxima => CacheValidacaoFotos.ValidadeMaxima;

    [Fact]
    public void Validade_por_foto_fica_entre_12_e_24_h_e_e_estavel()
    {
        for (var ordem = 1; ordem <= 5000; ordem++)
        {
            var v = CacheValidacaoFotos.ValidadeDe(ordem);
            Assert.InRange(v, Validade, ValidadeMaxima);
            Assert.Equal(v, CacheValidacaoFotos.ValidadeDe(ordem));
        }
    }

    [Fact]
    public void Fotos_validadas_juntas_nao_vencem_juntas()
    {
        // 1621 ordens seguidas, validadas no mesmo instante: a cada hora do intervalo 12-24 h vence so uma parte.
        var vencimentos = Enumerable.Range(1000, 1621)
            .Select(o => (int)CacheValidacaoFotos.ValidadeDe(o).TotalHours).GroupBy(h => h).ToList();
        Assert.Equal(12, vencimentos.Count);
        Assert.All(vencimentos, g => Assert.InRange(g.Count(), 60, 220)); // ~135 por hora
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Validade_por_foto_aceita_ordem_zero_negativa_e_extrema(int ordem)
    {
        Assert.InRange(CacheValidacaoFotos.ValidadeDe(ordem), Validade, ValidadeMaxima);
    }

    [Fact]
    public void Entrada_vale_ate_o_prazo_exato_e_vence_nele()
    {
        var cache = Novo();
        cache.Registrar(10, 500, true);
        var prazo = CacheValidacaoFotos.ValidadeDe(10);

        _agora += prazo - TimeSpan.FromTicks(1);
        Assert.True(cache.TentarObter(10, 500, out _));
        _agora += TimeSpan.FromTicks(1);
        Assert.False(cache.TentarObter(10, 500, out _));
    }

    [Fact]
    public void Poda_respeita_o_prazo_da_propria_foto()
    {
        // Acha duas ordens com prazos distintos: a de prazo menor vence antes da outra.
        var curta = Enumerable.Range(1, 200).MinBy(CacheValidacaoFotos.ValidadeDe);
        var longa = Enumerable.Range(1, 200).MaxBy(CacheValidacaoFotos.ValidadeDe);
        Assert.True(CacheValidacaoFotos.ValidadeDe(curta) + TimeSpan.FromHours(1) < CacheValidacaoFotos.ValidadeDe(longa));

        var a = Novo();
        a.Registrar(curta, 500, true);
        a.Registrar(longa, 500, true);
        _agora += CacheValidacaoFotos.ValidadeDe(curta) + TimeSpan.FromMinutes(1);
        a.Gravar(new HashSet<int>());

        var b = Novo();
        Assert.Equal(1, b.Quantidade); // so a curta foi podada; a longa ainda nao venceu
    }

    [Fact]
    public void Entrada_vence_so_depois_do_prazo_da_propria_foto()
    {
        var cache = Novo();
        cache.Registrar(10, 500, true);
        var prazo = CacheValidacaoFotos.ValidadeDe(10);

        _agora += prazo - TimeSpan.FromMinutes(1);
        Assert.True(cache.TentarObter(10, 500, out _));
        _agora += TimeSpan.FromMinutes(2);
        Assert.False(cache.TentarObter(10, 500, out _));
    }

    [Fact]
    public void Arquivo_ausente_comeca_vazio()
    {
        Assert.Equal(0, Novo().Quantidade);
    }

    [Theory]
    [InlineData("isto nao e json")]
    [InlineData("{\"10\":null}")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void Arquivo_corrompido_comeca_vazio_e_segue_funcionando(string conteudo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
        File.WriteAllText(Caminho, conteudo);

        var cache = Novo();
        Assert.Equal(0, cache.Quantidade);
        cache.Registrar(10, 500, true);
        cache.Gravar();
        Assert.True(Novo().TentarObter(10, 500, out _));
    }

    [Fact]
    public void Data_no_fim_do_calendario_no_arquivo_nao_lanca_e_a_entrada_e_descartada()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
        File.WriteAllText(Caminho,
            "{\"10\":{\"Tamanho\":500,\"Valida\":true,\"ValidadoEm\":\"9999-12-31T23:59:59+00:00\"},"
            + "\"11\":{\"Tamanho\":700,\"Valida\":true,\"ValidadoEm\":\"2026-11-17T09:30:00+00:00\"},"
            + "\"12\":{\"Tamanho\":800,\"Valida\":true,\"ValidadoEm\":\"2026-11-17T10:30:00+00:00\"}}");

        var cache = Novo();
        Assert.Equal(2, cache.Quantidade); // 10 (futuro distante) sai; 12 esta dentro da folga de 1 h
        Assert.False(cache.TentarObter(10, 500, out _));
        Assert.True(cache.TentarObter(11, 700, out _));

        cache.Gravar(new HashSet<int>()); // poda com data extrema na memoria nao estoura
        Assert.Equal(2, Novo().Quantidade);
    }

    [Fact]
    public void Gravar_com_data_extrema_registrada_nao_estoura()
    {
        var cache = new CacheValidacaoFotos(Caminho, () => DateTimeOffset.MaxValue);
        cache.Registrar(10, 500, true);
        cache.Gravar(new HashSet<int>());
        Assert.True(cache.TentarObter(10, 500, out _));
    }

    [Fact]
    public void Arquivo_acima_do_limite_comeca_vazio_sem_ler()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
        using (var f = File.Create(Caminho))
            f.SetLength(CacheValidacaoFotos.TamanhoMaximoArquivo + 1);

        var cache = Novo();
        Assert.Equal(0, cache.Quantidade);
        cache.Registrar(10, 500, true);
        cache.Gravar();
        Assert.True(Novo().TentarObter(10, 500, out _));
    }

    [Fact]
    public void Falha_de_disco_nao_propaga()
    {
        // O "diretorio" do arquivo e, na verdade, um arquivo: criar a pasta falha.
        Directory.CreateDirectory(_pasta);
        var bloqueio = Path.Combine(_pasta, "dados");
        File.WriteAllText(bloqueio, "x");

        var cache = Novo();
        cache.Registrar(10, 500, true);
        cache.Gravar();

        Assert.True(cache.TentarObter(10, 500, out _));
    }

    [Fact]
    public async Task Registro_concorrente_nao_perde_entradas()
    {
        var cache = Novo();
        await Task.WhenAll(Enumerable.Range(1, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
                cache.Registrar(t * 1000 + i, i + 1, true);
        })));
        Assert.Equal(1600, cache.Quantidade);
    }
}
