using System.Security.Cryptography;
using System.Text.Json;
using AgenteShop9.Assinatura;
using AgenteShop9.Catalogos;
using AgenteShop9.Configuracao;
using AgenteShop9.Operacoes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenteShop9.Testes;

/// <summary>
/// Issue #179: sem indice de vendas pronto a consulta de ranking responde `ranking_indisponivel` sem esperar nem
/// tocar o banco; ordenacao sem vendas nao depende do indice. Nao ha SQL Server aqui: so o caminho antes do banco.
/// </summary>
public class ExecutorIndiceVendasTestes : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("exec_vendas").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class LogCapturado : ILogger<ExecutorOperacao>
    {
        public readonly List<string> Mensagens = [];
        public IDisposable? BeginScope<TState>(TState estado) where TState : notnull => null;
        public bool IsEnabled(LogLevel nivel) => true;
        public void Log<TState>(LogLevel nivel, EventId id, TState estado, Exception? ex,
            Func<TState, Exception?, string> formatador)
        {
            lock (Mensagens) Mensagens.Add(formatador(estado, ex));
        }
        public bool Tem(string trecho) { lock (Mensagens) return Mensagens.Any(m => m.Contains(trecho)); }
    }

    private ExecutorOperacao Executor(ILogger<ExecutorOperacao>? logger = null)
    {
        var origem = Path.Combine(AppContext.BaseDirectory, "fixtures", "catalogo.candidato-vendas-indice-agente-v42.json");
        var caminho = Path.Combine(_dir, "catalogo.json");
        File.Copy(origem, caminho);
        File.WriteAllText(caminho + ".minisig", "assinatura-ficticia");
        var config = new ConfiguracaoAgente
        {
            ConexaoSql = "x", BackendUrl = "wss://x/agente", TokenAgente = "t", CatalogoCaminho = caminho,
        };
        var gestor = new GestorCatalogo(NullLogger<GestorCatalogo>.Instance, config, (bytes, _) =>
            ResultadoVerificacao.Ok(
                $"arquivo=catalogo.json sha256={Convert.ToHexStringLower(SHA256.HashData(bytes))} " +
                "data=2026-01-01T00:00:00Z versao=42"));
        gestor.Inicializar();
        Assert.Equal(42, gestor.VersaoAceita);
        return new ExecutorOperacao(logger ?? NullLogger<ExecutorOperacao>.Instance, config, gestor);
    }

    private static JsonElement Pedido(string operacao, string ordenarPor)
    {
        var json = JsonSerializer.Serialize(new
        {
            id_requisicao = "r1",
            operacao,
            parametros = new Dictionary<string, object>
            {
                ["OrdemFilial"] = 2, ["TabelaEscolhida"] = 1, ["Tabela_01"] = 1, ["Inicio"] = "2026-09-19",
            },
            paginacao = new { ordenar_por = ordenarPor, direcao = "desc" },
        });
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task Sem_indice_pronto_ranking_responde_ranking_indisponivel_sem_esperar()
    {
        using var executor = Executor();
        var resposta = await executor.ExecutarAsync(Pedido("catalogo_produtos_ids", "mais_vendidos"), default)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("erro", resposta.Status);
        Assert.Equal("ranking_indisponivel", resposta.CodigoErro);
        Assert.Equal("r1", resposta.IdRequisicao);
    }

    [Fact]
    public async Task Operacao_interna_do_indice_nao_pode_ser_pedida_de_fora()
    {
        using var executor = Executor();
        var pedido = JsonDocument.Parse(
            "{\"id_requisicao\":\"r\",\"operacao\":\"catalogo_vendas_indice\",\"parametros\":{\"OrdemFilial\":2}}")
            .RootElement.Clone();
        var resposta = await executor.ExecutarAsync(pedido, default);
        Assert.Equal("operacao_desconhecida", resposta.CodigoErro);
    }

    [Fact]
    public async Task Ordenacao_por_nome_na_operacao_sem_vendas_nao_devolve_ranking_indisponivel()
    {
        using var executor = Executor();
        var resposta = await executor.ExecutarAsync(Pedido("catalogo_produtos_ids_sem_vendas", "nome"), default);
        // Sem SQL Server aqui o resultado e erro de banco, nunca ranking_indisponivel.
        Assert.NotEqual("ranking_indisponivel", resposta.CodigoErro);
    }

    [Fact]
    public async Task Inicio_sem_indice_pronto_nao_devolve_ranking_indisponivel_e_dispara_a_construcao()
    {
        var log = new LogCapturado();
        using var executor = Executor(log);
        var pedido = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id_requisicao = "r1",
            operacao = "catalogo_inicio",
            parametros = new Dictionary<string, object>
            {
                ["OrdemFilial"] = 2, ["Tabela_01"] = 1, ["Inicio"] = "2026-09-19",
            },
        })).RootElement.Clone();

        var resposta = await executor.ExecutarAsync(pedido, default).WaitAsync(TimeSpan.FromSeconds(5));

        // Sem SQL Server a consulta em si falha no banco; o que importa: o indice nao a barrou.
        Assert.NotEqual("ranking_indisponivel", resposta.CodigoErro);
        // Prova do disparo: a construcao em segundo plano rodou e falhou (conexao "x" invalida).
        for (var i = 0; i < 300 && !log.Tem("Atualizacao do indice de vendas falhou"); i++)
            await Task.Delay(10);
        Assert.True(log.Tem("Atualizacao do indice de vendas falhou"), string.Join(" | ", log.Mensagens) + " | codigo=" + resposta.CodigoErro);
    }

    [Fact]
    public async Task Produtos_ids_por_nome_ou_preco_na_operacao_com_vendas_tambem_so_depende_do_indice_no_ranking()
    {
        // O broker usa catalogo_produtos_ids_sem_vendas para nome/preco; a operacao com vendas
        // continua exigindo o indice, mesmo se alguem a chamar com outra ordenacao.
        using var executor = Executor();
        var resposta = await executor.ExecutarAsync(Pedido("catalogo_produtos_ids", "em_alta"), default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ranking_indisponivel", resposta.CodigoErro);
    }
}
