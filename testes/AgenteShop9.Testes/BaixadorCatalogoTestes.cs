using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenteShop9.Assinatura;
using AgenteShop9.Catalogos;
using AgenteShop9.Configuracao;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenteShop9.Testes;

/// <summary>
/// Auto-update do catalogo (item 4.1, issue #48): download HTTP + gravacao do
/// candidato local + reavaliacao pelo GestorCatalogo. A logica de ACEITAR o
/// que foi baixado (assinatura/SHA-256/anti-retrocesso) ja e coberta por
/// GestorCatalogoTestes; aqui o foco e o download em si.
/// </summary>
public class BaixadorCatalogoTestes : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("baixador_cat").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string CandidatoPath => Path.Combine(_dir, "catalogo.json");

    private ConfiguracaoAgente Config(string backendUrl = "wss://backend.teste/agente") => new()
    {
        ConexaoSql = "x",
        BackendUrl = backendUrl,
        TokenAgente = "token-secreto",
        CatalogoCaminho = CandidatoPath,
    };

    private static int LerVersao(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.GetProperty("versao_catalogo").GetInt32();
    }

    // Mesmo stub de GestorCatalogoTestes: assinatura sempre valida, trusted
    // comment coerente com os bytes recebidos.
    private static GestorCatalogo.Verificador StubCoerente => (bytes, _) =>
        ResultadoVerificacao.Ok(
            $"arquivo=catalogo.json sha256={Convert.ToHexStringLower(SHA256.HashData(bytes))} " +
            $"data=2026-01-01T00:00:00Z versao={LerVersao(bytes)}");

    private sealed class HandlerDeMentira(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public HttpRequestMessage? UltimoPedido { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UltimoPedido = request;
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage RespostaCatalogo(int versao, byte[]? catalogoBytes = null)
    {
        var bytes = catalogoBytes
            ?? Encoding.UTF8.GetBytes("{\"versao_catalogo\": " + versao + ", \"operacoes\": {}}");
        var corpo = JsonSerializer.Serialize(new
        {
            versao_catalogo = versao,
            catalogo_base64 = Convert.ToBase64String(bytes),
            assinatura_base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes($"assinatura-v{versao}")),
        });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corpo) };
    }

    [Theory]
    [InlineData("wss://backend.teste/agente", "https://backend.teste/api/agente/catalogo?versao_atual=0")]
    [InlineData("wss://backend.teste:8443/agente", "https://backend.teste:8443/api/agente/catalogo?versao_atual=0")]
    [InlineData("ws://localhost:5000/agente", "http://localhost:5000/api/agente/catalogo?versao_atual=0")]
    public void MontarUrlCatalogo_deriva_https_do_backend_url(string backendUrl, string esperada)
    {
        Assert.Equal(new Uri(esperada), BaixadorCatalogo.MontarUrlCatalogo(backendUrl, versaoAtual: 0));
    }

    [Fact]
    public async Task Resposta_204_nao_grava_nada_nem_promove()
    {
        var handler = new HandlerDeMentira(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var gestor = new GestorCatalogo(NullLogger<GestorCatalogo>.Instance, Config(), StubCoerente);
        gestor.Inicializar();
        var baixador = new BaixadorCatalogo(
            NullLogger<BaixadorCatalogo>.Instance, Config(), gestor, new HttpClient(handler));

        await baixador.VerificarUmaVezAsync(CancellationToken.None);

        Assert.Equal(0, gestor.VersaoAceita);
        Assert.False(File.Exists(CandidatoPath));
    }

    [Fact]
    public async Task Resposta_200_grava_candidato_e_promove_via_gestor()
    {
        var handler = new HandlerDeMentira(_ => RespostaCatalogo(9));
        var gestor = new GestorCatalogo(NullLogger<GestorCatalogo>.Instance, Config(), StubCoerente);
        gestor.Inicializar();
        var baixador = new BaixadorCatalogo(
            NullLogger<BaixadorCatalogo>.Instance, Config(), gestor, new HttpClient(handler));

        await baixador.VerificarUmaVezAsync(CancellationToken.None);

        Assert.Equal(9, gestor.VersaoAceita);
        Assert.True(File.Exists(CandidatoPath));
        Assert.True(File.Exists(CandidatoPath + ".minisig"));
        Assert.Equal("assinatura-v9", File.ReadAllText(CandidatoPath + ".minisig"));
    }

    [Fact]
    public async Task Envia_token_no_cabecalho_e_versao_atual_na_query()
    {
        var handler = new HandlerDeMentira(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var gestor = new GestorCatalogo(NullLogger<GestorCatalogo>.Instance, Config(), StubCoerente);
        gestor.Inicializar(); // sem candidato -> VersaoAceita = 0
        var baixador = new BaixadorCatalogo(
            NullLogger<BaixadorCatalogo>.Instance, Config(), gestor, new HttpClient(handler));

        await baixador.VerificarUmaVezAsync(CancellationToken.None);

        Assert.NotNull(handler.UltimoPedido);
        Assert.Equal("Bearer", handler.UltimoPedido!.Headers.Authorization?.Scheme);
        Assert.Equal("token-secreto", handler.UltimoPedido.Headers.Authorization?.Parameter);
        Assert.Equal("/api/agente/catalogo", handler.UltimoPedido.RequestUri?.AbsolutePath);
        Assert.Contains("versao_atual=0", handler.UltimoPedido.RequestUri?.Query);
    }

    [Fact]
    public async Task Catalogo_com_assinatura_invalida_nao_e_promovido_mesmo_baixado()
    {
        var handler = new HandlerDeMentira(_ => RespostaCatalogo(9));
        var gestor = new GestorCatalogo(
            NullLogger<GestorCatalogo>.Instance, Config(),
            (_, __) => ResultadoVerificacao.Falha("assinatura de teste invalida"));
        gestor.Inicializar();
        var baixador = new BaixadorCatalogo(
            NullLogger<BaixadorCatalogo>.Instance, Config(), gestor, new HttpClient(handler));

        await baixador.VerificarUmaVezAsync(CancellationToken.None);

        // O arquivo foi baixado (fica gravado como candidato)...
        Assert.True(File.Exists(CandidatoPath));
        // ...mas o GestorCatalogo recusou promove-lo.
        Assert.Equal(0, gestor.VersaoAceita);
    }

    [Fact]
    public async Task ExecutarPeriodicamente_nao_derruba_o_robo_quando_o_backend_falha()
    {
        var handler = new HandlerDeMentira(_ => throw new HttpRequestException("backend fora do ar"));
        var gestor = new GestorCatalogo(NullLogger<GestorCatalogo>.Instance, Config(), StubCoerente);
        gestor.Inicializar();
        var baixador = new BaixadorCatalogo(
            NullLogger<BaixadorCatalogo>.Instance, Config(), gestor, new HttpClient(handler));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        // Nao deve lancar mesmo com toda tentativa falhando.
        await baixador.ExecutarPeriodicamenteAsync(TimeSpan.FromMilliseconds(20), cts.Token);
    }
}
