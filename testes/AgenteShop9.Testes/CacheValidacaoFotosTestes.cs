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
    public void Entrada_vale_24_horas_e_depois_e_revalidada()
    {
        var cache = Novo();
        cache.Registrar(10, 500, true);

        _agora += TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59);
        Assert.True(cache.TentarObter(10, 500, out _));

        _agora += TimeSpan.FromMinutes(2);
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
        _agora += Validade + TimeSpan.FromMinutes(1);
        a.Gravar(new HashSet<int> { 10 });

        var b = Novo();
        Assert.Equal(1, b.Quantidade);
        Assert.False(b.TentarObter(11, 700, out _));
        _agora -= Validade + TimeSpan.FromMinutes(1);
        Assert.True(b.TentarObter(10, 500, out _));
    }

    private static TimeSpan Validade => CacheValidacaoFotos.Validade;

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
