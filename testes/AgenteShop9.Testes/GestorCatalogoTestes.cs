using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenteShop9.Assinatura;
using AgenteShop9.Catalogos;
using AgenteShop9.Configuracao;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenteShop9.Testes;

/// <summary>
/// Lógica de aceitação do catálogo: SHA-256, `versao=` == `versao_catalogo`,
/// anti-retrocesso, catálogo aceito guardado. A verificação da assinatura em si
/// é coberta por VerificadorAssinaturaTestes; aqui ela entra por um stub, exceto
/// no teste que usa a fixture real.
/// </summary>
public class GestorCatalogoTestes : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gestor_cat").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string CandidatoPath => Path.Combine(_dir, "catalogo.json");
    private string AceitoPath => Path.Combine(_dir, "dados", "catalogo.json");

    private ConfiguracaoAgente Config() => new()
    {
        ConexaoSql = "x",
        BackendUrl = "wss://x/agente",
        TokenAgente = "t",
        CatalogoCaminho = CandidatoPath,
    };

    private GestorCatalogo Gestor(GestorCatalogo.Verificador? verificar = null) =>
        new(NullLogger<GestorCatalogo>.Instance, Config(), verificar);

    /// <summary>Grava um catálogo candidato (+ .minisig fictício, ignorado pelos stubs).</summary>
    private void GravarCandidato(int versaoCatalogo)
    {
        var json = "{\"versao_catalogo\": " + versaoCatalogo + ", \"operacoes\": {}}";
        File.WriteAllBytes(CandidatoPath, Encoding.UTF8.GetBytes(json));
        File.WriteAllText(CandidatoPath + ".minisig", "assinatura-ficticia");
    }

    private static int LerVersao(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.GetProperty("versao_catalogo").GetInt32();
    }

    // assinatura sempre válida; trusted comment coerente com os bytes recebidos
    private static GestorCatalogo.Verificador StubCoerente => (bytes, _) =>
        ResultadoVerificacao.Ok(
            $"arquivo=catalogo.json sha256={Convert.ToHexStringLower(SHA256.HashData(bytes))} " +
            $"data=2026-01-01T00:00:00Z versao={LerVersao(bytes)}");

    [Fact]
    public void Sem_candidato_a_versao_e_zero()
    {
        var g = Gestor((_, __) => ResultadoVerificacao.Falha("nao chamado"));
        g.Inicializar();

        Assert.Equal(0, g.VersaoAceita);
    }

    [Fact]
    public void Assinatura_invalida_recusa_o_candidato()
    {
        GravarCandidato(3);
        var g = Gestor((_, __) => ResultadoVerificacao.Falha("assinatura de teste invalida"));
        g.Inicializar();

        Assert.Equal(0, g.VersaoAceita);
        Assert.False(File.Exists(AceitoPath));
    }

    [Fact]
    public void Candidato_valido_e_aceito_e_guardado()
    {
        GravarCandidato(3);
        var g = Gestor(StubCoerente);
        g.Inicializar();

        Assert.Equal(3, g.VersaoAceita);
        Assert.True(File.Exists(AceitoPath));
        Assert.True(File.Exists(AceitoPath + ".minisig"));
    }

    [Fact]
    public void Upgrade_para_versao_maior_e_aceito()
    {
        GravarCandidato(3);
        Gestor(StubCoerente).Inicializar();

        GravarCandidato(5);
        var g2 = Gestor(StubCoerente);
        g2.Inicializar();

        Assert.Equal(5, g2.VersaoAceita);
    }

    [Fact]
    public void Anti_retrocesso_recusa_versao_igual_ou_menor()
    {
        GravarCandidato(5);
        Gestor(StubCoerente).Inicializar();

        GravarCandidato(5); // igual
        var g2 = Gestor(StubCoerente);
        g2.Inicializar();
        Assert.Equal(5, g2.VersaoAceita);

        GravarCandidato(4); // menor
        var g3 = Gestor(StubCoerente);
        g3.Inicializar();
        Assert.Equal(5, g3.VersaoAceita);
    }

    [Fact]
    public void Versao_do_comentario_diferente_do_json_e_recusado()
    {
        GravarCandidato(7);
        GestorCatalogo.Verificador stubVersaoErrada = (bytes, _) =>
            ResultadoVerificacao.Ok(
                $"arquivo=catalogo.json sha256={Convert.ToHexStringLower(SHA256.HashData(bytes))} " +
                $"data=2026-01-01T00:00:00Z versao={LerVersao(bytes) + 1}");

        var g = Gestor(stubVersaoErrada);
        g.Inicializar();

        Assert.Equal(0, g.VersaoAceita);
    }

    [Fact]
    public void Sha256_do_comentario_divergente_e_recusado()
    {
        GravarCandidato(3);
        GestorCatalogo.Verificador stubShaErrado = (bytes, _) =>
            ResultadoVerificacao.Ok(
                $"arquivo=catalogo.json sha256=deadbeef data=2026-01-01T00:00:00Z versao={LerVersao(bytes)}");

        var g = Gestor(stubShaErrado);
        g.Inicializar();

        Assert.Equal(0, g.VersaoAceita);
    }

    [Fact]
    public void Fixture_real_v12_e_aceita_pelo_verificador_real()
    {
        File.WriteAllBytes(CandidatoPath, Fixtures.CatalogoBytes);
        File.WriteAllText(CandidatoPath + ".minisig", Fixtures.CatalogoMinisig);

        var g = Gestor(); // verificador real
        g.Inicializar();

        Assert.Equal(12, g.VersaoAceita);
    }
}
