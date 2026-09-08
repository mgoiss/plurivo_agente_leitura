using System.Diagnostics;
using System.Text.Json;
using AgenteShop9.Catalogos;
using AgenteShop9.Configuracao;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AgenteShop9.Operacoes;

public interface IExecutorOperacao
{
    /// <summary>Recebe um pedido `executar` (JsonElement) e devolve a resposta `resultado`.</summary>
    Task<MensagemResultado> ExecutarAsync(JsonElement pedido, CancellationToken ct);
}

/// <summary>
/// Executa uma operacao do catalogo ACEITO contra o SQL Server local.
///
/// ISSUE #7 / spec 3.3: a operacao e achada pelo NOME no catalogo; o SQL nunca
/// vem do pedido. Parametro so entra como valor. Mensagem de erro NUNCA expoe
/// connection string nem detalhe interno do banco (spec 3.4).
/// </summary>
public sealed class ExecutorOperacao(
    ILogger<ExecutorOperacao> log,
    ConfiguracaoAgente config,
    GestorCatalogo gestorCatalogo) : IExecutorOperacao
{
    public async Task<MensagemResultado> ExecutarAsync(JsonElement pedido, CancellationToken ct)
    {
        var idRequisicao = LerTexto(pedido, "id_requisicao");
        var nomeOperacao = LerTexto(pedido, "operacao");

        if (string.IsNullOrWhiteSpace(nomeOperacao))
            return MensagemResultado.Erro(idRequisicao, "pedido_invalido", "pedido sem 'operacao'.");

        var catalogo = gestorCatalogo.CatalogoAceito;
        if (catalogo?.Operacoes is null || !catalogo.Operacoes.TryGetValue(nomeOperacao, out var op))
            return MensagemResultado.Erro(
                idRequisicao, "operacao_desconhecida", $"operacao '{nomeOperacao}' nao existe no catalogo.");

        var recebidos = LerParametros(pedido);

        var erro = ValidadorParametros.Validar(op, recebidos);
        if (erro is not null)
            return MensagemResultado.Erro(idRequisicao, erro.Codigo, erro.Mensagem);

        var relogio = Stopwatch.StartNew();
        try
        {
            await using var conexao = new SqlConnection(config.ConexaoSql);
            await conexao.OpenAsync(ct);

            await using var cmd = MontadorComando.Montar(op, recebidos, conexao);
            await using var leitor = await cmd.ExecuteReaderAsync(ct);

            var linhas = new List<IReadOnlyDictionary<string, object?>>();
            while (await leitor.ReadAsync(ct))
            {
                if (linhas.Count >= op.MaxLinhas)
                    return MensagemResultado.Erro(idRequisicao, "resultado_grande",
                        $"a consulta passou de {op.MaxLinhas} linhas; refine os filtros.");

                var linha = new Dictionary<string, object?>(leitor.FieldCount);
                for (var i = 0; i < leitor.FieldCount; i++)
                    linha[leitor.GetName(i)] = await leitor.IsDBNullAsync(i, ct) ? null : leitor.GetValue(i);
                linhas.Add(linha);
            }

            return new MensagemResultado
            {
                IdRequisicao = idRequisicao,
                Status = "ok",
                Linhas = linhas,
                DuracaoMs = relogio.ElapsedMilliseconds,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex) when (ex.Number is -2 or 258)
        {
            log.LogWarning("Operacao {Op} estourou o timeout.", nomeOperacao);
            return MensagemResultado.Erro(idRequisicao, "timeout", "a consulta demorou demais e foi cancelada.");
        }
        catch (Exception ex)
        {
            // ISSUE #7 / spec 3.4: NAO devolver ex.Message para fora — pode conter
            // schema, nome de servidor ou connection string. O detalhe fica so no
            // log local. Nao alterar.
            log.LogError(ex, "Falha ao executar a operacao {Op}.", nomeOperacao);
            return MensagemResultado.Erro(idRequisicao, "erro_consulta", "falha ao executar a consulta no banco.");
        }
    }

    private static string? LerTexto(JsonElement el, string nome) =>
        el.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Dictionary<string, JsonElement> LerParametros(JsonElement pedido)
    {
        var dict = new Dictionary<string, JsonElement>();
        if (pedido.TryGetProperty("parametros", out var ps) && ps.ValueKind == JsonValueKind.Object)
            foreach (var prop in ps.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
        return dict;
    }
}
