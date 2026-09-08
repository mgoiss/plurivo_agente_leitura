using AgenteShop9.Configuracao;

namespace AgenteShop9.Testes;

public class LeitorConfiguracaoTestes : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("agente_testes").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Arquivo(string conteudo)
    {
        var caminho = Path.Combine(_dir, "config.json");
        File.WriteAllText(caminho, conteudo);
        return caminho;
    }

    [Fact]
    public void Le_config_valida()
    {
        var config = LeitorConfiguracao.Ler(Arquivo(
            """
            {
              "conexao_sql": "Server=localhost;Database=Shop9;",
              "backend_url": "wss://api.exemplo/agente",
              "token_agente": "abc123"
            }
            """));

        Assert.Equal("wss://api.exemplo/agente", config.BackendUrl);
        Assert.Equal("abc123", config.TokenAgente);
        Assert.Equal("Server=localhost;Database=Shop9;", config.ConexaoSql);
    }

    [Fact]
    public void Catalogo_caminho_default_e_ao_lado_do_config()
    {
        var config = LeitorConfiguracao.Ler(Arquivo(
            """{ "conexao_sql": "s", "backend_url": "wss://x/agente", "token_agente": "abc" }"""));

        Assert.Equal(Path.Combine(_dir, "catalogo.json"), config.CatalogoCaminho);
    }

    [Fact]
    public void Catalogo_caminho_explicito_e_respeitado()
    {
        var config = LeitorConfiguracao.Ler(Arquivo(
            """
            { "conexao_sql": "s", "backend_url": "wss://x/agente", "token_agente": "abc",
              "catalogo_caminho": "/outro/lugar/catalogo.json" }
            """));

        Assert.Equal("/outro/lugar/catalogo.json", config.CatalogoCaminho);
    }

    [Fact]
    public void Arquivo_inexistente_da_erro_claro()
    {
        var ex = Assert.Throws<ConfiguracaoInvalidaException>(
            () => LeitorConfiguracao.Ler(Path.Combine(_dir, "nao_existe.json")));

        Assert.Contains("nao encontrado", ex.Message);
    }

    [Theory]
    [InlineData("""{ "backend_url": "wss://x/agente", "token_agente": "abc" }""", "conexao_sql")]
    [InlineData("""{ "conexao_sql": "s", "token_agente": "abc" }""", "backend_url")]
    [InlineData("""{ "conexao_sql": "s", "backend_url": "wss://x/agente" }""", "token_agente")]
    public void Campo_faltando_e_apontado_pelo_nome(string json, string campoEsperado)
    {
        var ex = Assert.Throws<ConfiguracaoInvalidaException>(() => LeitorConfiguracao.Ler(Arquivo(json)));

        Assert.Contains(campoEsperado, ex.Message);
    }

    [Theory]
    [InlineData("http://x/agente")]
    [InlineData("api.suaplataforma.com.br/agente")]
    public void Backend_url_que_nao_e_websocket_da_erro(string url)
    {
        var json = $$"""{ "conexao_sql": "s", "backend_url": "{{url}}", "token_agente": "abc" }""";

        var ex = Assert.Throws<ConfiguracaoInvalidaException>(() => LeitorConfiguracao.Ler(Arquivo(json)));

        Assert.Contains("ws://", ex.Message);
    }

    [Fact]
    public void Json_invalido_da_erro_claro()
    {
        var ex = Assert.Throws<ConfiguracaoInvalidaException>(() => LeitorConfiguracao.Ler(Arquivo("{ nao e json")));

        Assert.Contains("JSON", ex.Message);
    }

    [Fact]
    public void Identidade_de_loja_no_arquivo_e_ignorada_sem_quebrar()
    {
        // O robo nunca usa identificacao de loja vinda do config (spec 3.5). O
        // campo pode estar presente (engano) e isso NAO pode quebrar a leitura.
        var config = LeitorConfiguracao.Ler(Arquivo(
            """
            {
              "conexao_sql": "s",
              "backend_url": "wss://x/agente",
              "token_agente": "abc",
              "loja_ordem": "loja-fantasma"
            }
            """));

        Assert.Equal("abc", config.TokenAgente);
    }
}
