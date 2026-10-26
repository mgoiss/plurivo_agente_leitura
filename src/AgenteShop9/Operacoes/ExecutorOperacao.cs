using System.Diagnostics;
using System.Data;
using System.Text;
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
///
/// ISSUE #24: operacao com `ordenacao` e paginada — busca `tamanho_pagina + 1`
/// linhas (a extra vira `tem_proxima`), e roda o `sql_contagem` quando o pedido
/// pede `incluir_total`.
/// </summary>
public sealed class ExecutorOperacao(
    ILogger<ExecutorOperacao> log,
    ConfiguracaoAgente config,
    GestorCatalogo gestorCatalogo) : IExecutorOperacao
{
    private sealed class ResultadoGrandeException : Exception;
    private readonly SemaphoreSlim _travaIndiceFotos = new(1, 1);
    private readonly Dictionary<string, (IReadOnlyDictionary<int, int> Fotos, DateTimeOffset Expira)>
        _indicesFotos = new();

    public async Task<MensagemResultado> ExecutarAsync(JsonElement pedido, CancellationToken ct)
    {
        var idRequisicao = LerTexto(pedido, "id_requisicao");
        var nomeOperacao = LerTexto(pedido, "operacao");

        if (string.IsNullOrWhiteSpace(nomeOperacao))
            return MensagemResultado.Erro(idRequisicao, "pedido_invalido", "pedido sem 'operacao'.");

        var catalogo = gestorCatalogo.CatalogoAceito;
        if (catalogo?.Operacoes is null || !catalogo.Operacoes.TryGetValue(nomeOperacao, out var op)
            || op.UsoInterno)
            return MensagemResultado.Erro(
                idRequisicao, "operacao_desconhecida", $"operacao '{nomeOperacao}' nao existe no catalogo.");

        var recebidos = LerParametros(pedido);

        var erro = ValidadorParametros.Validar(op, recebidos);
        if (erro is not null)
            return MensagemResultado.Erro(idRequisicao, erro.Codigo, erro.Mensagem);

        PaginacaoResolvida? paginacao = null;
        if (op.Ordenacao is not null)
        {
            var (resolvida, erroPag) = ValidadorPaginacao.Resolver(op, LerPaginacao(pedido));
            if (erroPag is not null)
                return MensagemResultado.Erro(idRequisicao, erroPag.Codigo, erroPag.Mensagem);
            paginacao = resolvida;
        }

        var relogio = Stopwatch.StartNew();
        var etapa = "conexao";
        try
        {
            await using var conexao = new SqlConnection(config.ConexaoSql);
            await conexao.OpenAsync(ct);
            if (op.UsaFotosValidadas)
            {
                etapa = "indice_fotos";
                var fotos = await ObterIndiceFotos(catalogo, recebidos, conexao, ct);
                etapa = "tabela_fotos";
                await PrepararTabelaFotos(conexao, fotos, ct);
            }

            etapa = "consulta";
            return paginacao is null
                ? await ExecutarSemPaginacao(idRequisicao, op, recebidos, conexao, relogio, ct)
                : await ExecutarPaginado(idRequisicao, op, recebidos, paginacao, conexao, relogio, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex) when (ex.Number is -2 or 258)
        {
            log.LogWarning("Operacao {Op} estourou o timeout SQL na etapa {Etapa} apos {DuracaoMs} ms (codigo {CodigoSql}).",
                nomeOperacao, etapa, relogio.ElapsedMilliseconds, ex.Number);
            return MensagemResultado.Erro(idRequisicao, "timeout", "a consulta demorou demais e foi cancelada.");
        }
        catch (TimeoutException)
        {
            log.LogWarning("Operacao {Op} estourou o tempo de preparacao na etapa {Etapa} apos {DuracaoMs} ms.",
                nomeOperacao, etapa, relogio.ElapsedMilliseconds);
            return MensagemResultado.Erro(idRequisicao, "timeout", "a preparacao de fotos excedeu o tempo permitido.");
        }
        catch (ResultadoGrandeException)
        {
            log.LogWarning("Operacao {Op} excedeu o limite de resposta na etapa {Etapa} apos {DuracaoMs} ms.",
                nomeOperacao, etapa, relogio.ElapsedMilliseconds);
            return MensagemResultado.Erro(idRequisicao, "resultado_grande",
                "a resposta excedeu o limite de texto da operacao.");
        }
        catch (IndiceFotosCapacidadeException ex)
        {
            log.LogWarning("Operacao {Op} excedeu o indice de fotos na etapa {Etapa}: {Motivo}, {Linhas} linhas, {Caracteres} caracteres, {DuracaoMs} ms.",
                nomeOperacao, etapa, ex.Motivo, ex.LinhasLidas, ex.CaracteresLidos,
                relogio.ElapsedMilliseconds);
            return MensagemResultado.Erro(idRequisicao, "resultado_grande",
                "o indice de fotos desse catalogo excedeu o limite permitido.");
        }
        catch (SqlException ex) when (ex.Number is 4060 or 208)
        {
            // 4060: banco indicado nao abre. 208: a conexao abriu, mas nao
            // encontrou uma estrutura esperada do Shop9 — comum quando o
            // Database foi omitido e o SQL Server abriu o banco padrao.
            // O detalhe do SQL Server fica apenas no log local.
            log.LogError(ex, "Banco Shop9 indisponivel ou incorreto na operacao {Op}.", nomeOperacao);
            return MensagemResultado.Erro(
                idRequisicao,
                "banco_shop9_incorreto",
                "O banco configurado nao contem os dados esperados do Shop9. " +
                "Revise Database=... em conexao_sql no config.json do agente.");
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

    private async Task<IReadOnlyDictionary<int, int>> ObterIndiceFotos(
        Catalogo catalogo, IReadOnlyDictionary<string, JsonElement> recebidos,
        SqlConnection conexao, CancellationToken ct)
    {
        var parametros = IndiceFotosPublicas.ParametrosEscopo(recebidos);
        var chaveEscopo = IndiceFotosPublicas.ChaveEscopo(catalogo.VersaoCatalogo, parametros);

        await _travaIndiceFotos.WaitAsync(ct);
        try
        {
            if (_indicesFotos.TryGetValue(chaveEscopo, out var existente)
                && DateTimeOffset.UtcNow < existente.Expira)
                return existente.Fotos;
            if (catalogo.Operacoes is null
                || !catalogo.Operacoes.TryGetValue("catalogo_indice_fotos", out var operacao)
                || !operacao.UsoInterno)
                throw new InvalidOperationException("Catalogo publico sem indice de fotos assinado.");
            if (ValidadorParametros.Validar(operacao, parametros) is not null)
                throw new ArgumentException("Escopo do indice de fotos invalido.");

            var acumulador = new AcumuladorIndiceFotosPublicas();
            var relogioIndice = Stopwatch.StartNew();
            await using (var comando = MontadorComando.Montar(
                operacao, parametros, conexao: conexao))
            await using (var leitor = await comando.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess, ct))
            {
                while (await leitor.ReadAsync(ct))
                {
                    if (relogioIndice.Elapsed > TimeSpan.FromSeconds(5))
                        throw new TimeoutException();
                    var produtoOrdem = leitor.GetInt32(0);
                    var imagemOrdem = leitor.GetInt32(1);
                    var tamanho = Convert.ToInt64(leitor.GetValue(2));
                    var texto = acumulador.PrecisaLerFoto(produtoOrdem)
                        ? leitor.GetString(3) : null;
                    acumulador.Incluir(produtoOrdem, imagemOrdem, tamanho,
                                      texto, IndiceFotosPublicas.FotoValida);
                }
            }
            if (relogioIndice.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException();
            _indicesFotos[chaveEscopo] = (acumulador.Fotos, DateTimeOffset.UtcNow.AddSeconds(300));
            if (_indicesFotos.Count > 16)
            {
                var antiga = _indicesFotos.MinBy(par => par.Value.Expira).Key;
                _indicesFotos.Remove(antiga);
            }
            return acumulador.Fotos;
        }
        finally
        {
            _travaIndiceFotos.Release();
        }
    }

    private static async Task PrepararTabelaFotos(
        SqlConnection conexao, IReadOnlyDictionary<int, int> fotos, CancellationToken ct)
    {
        await using (var comando = conexao.CreateCommand())
        {
            comando.CommandText = "CREATE TABLE #FotosValidas (Produto_Ordem int NOT NULL PRIMARY KEY, Imagem_Ordem int NOT NULL)";
            await comando.ExecuteNonQueryAsync(ct);
        }
        if (fotos.Count == 0)
            return;
        var tabela = new DataTable();
        tabela.Columns.Add("Produto_Ordem", typeof(int));
        tabela.Columns.Add("Imagem_Ordem", typeof(int));
        foreach (var (produto, imagem) in fotos)
            tabela.Rows.Add(produto, imagem);
        using var copia = new SqlBulkCopy(conexao)
        {
            DestinationTableName = "#FotosValidas",
            BatchSize = 1000,
            BulkCopyTimeout = 2,
        };
        await copia.WriteToServerAsync(tabela, ct);
    }

    private static async Task<MensagemResultado> ExecutarSemPaginacao(
        string? idRequisicao, Operacao op, IReadOnlyDictionary<string, JsonElement> recebidos,
        SqlConnection conexao, Stopwatch relogio, CancellationToken ct)
    {
        await using var cmd = MontadorComando.Montar(op, recebidos, conexao: conexao);
        await using var leitor = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);

        var linhas = new List<IReadOnlyDictionary<string, object?>>();
        while (await leitor.ReadAsync(ct))
        {
            if (linhas.Count >= op.MaxLinhas)
                return MensagemResultado.Erro(idRequisicao, "resultado_grande",
                    $"a consulta passou de {op.MaxLinhas} linhas; refine os filtros.");

            linhas.Add(await LerLinha(leitor, op.MaxCaracteresTexto, ct));
        }

        return new MensagemResultado
        {
            IdRequisicao = idRequisicao,
            Status = "ok",
            Linhas = linhas,
            DuracaoMs = relogio.ElapsedMilliseconds,
        };
    }

    private static async Task<MensagemResultado> ExecutarPaginado(
        string? idRequisicao, Operacao op, IReadOnlyDictionary<string, JsonElement> recebidos,
        PaginacaoResolvida pag, SqlConnection conexao, Stopwatch relogio, CancellationToken ct)
    {
        var linhas = new List<IReadOnlyDictionary<string, object?>>();
        await using (var cmd = MontadorComando.Montar(op, recebidos, pag, conexao))
        {
            // Buscamos tamanho_pagina + 1: a linha extra so serve para saber que
            // ha proxima pagina — ela nao volta na resposta. O leitor fecha aqui,
            // antes da contagem (sem MARS, a conexao so aguenta um por vez).
            await using var leitor = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            while (linhas.Count < pag.Buscar && await leitor.ReadAsync(ct))
                linhas.Add(await LerLinha(leitor, op.MaxCaracteresTexto, ct));
        }

        var temProxima = linhas.Count > pag.TamanhoPagina;
        if (temProxima)
            linhas.RemoveAt(linhas.Count - 1);

        long? totalLinhas = null;
        int? totalPaginas = null;
        if (pag.IncluirTotal)
        {
            await using var cmdContagem = MontadorComando.MontarContagem(op, recebidos, conexao);
            var bruto = await cmdContagem.ExecuteScalarAsync(ct);
            totalLinhas = bruto is null or DBNull ? 0L : Convert.ToInt64(bruto);
            totalPaginas = (int)Math.Ceiling(totalLinhas.Value / (double)pag.TamanhoPagina);
        }

        return new MensagemResultado
        {
            IdRequisicao = idRequisicao,
            Status = "ok",
            Linhas = linhas,
            Paginacao = new RespostaPaginacao
            {
                Pagina = pag.Pagina,
                TamanhoPagina = pag.TamanhoPagina,
                TemProxima = temProxima,
                TotalLinhas = totalLinhas,
                TotalPaginas = totalPaginas,
                OrdenadoPor = pag.OrdenarPor,
                Direcao = pag.Direcao,
            },
            DuracaoMs = relogio.ElapsedMilliseconds,
        };
    }

    private static async Task<Dictionary<string, object?>> LerLinha(
        SqlDataReader leitor, int maxCaracteresTexto, CancellationToken ct)
    {
        var linha = new Dictionary<string, object?>(leitor.FieldCount);
        for (var i = 0; i < leitor.FieldCount; i++)
        {
            if (await leitor.IsDBNullAsync(i, ct))
            {
                linha[leitor.GetName(i)] = null;
                continue;
            }
            if (maxCaracteresTexto > 0 && leitor.GetFieldType(i) == typeof(string))
            {
                using var fonte = leitor.GetTextReader(i);
                var texto = new StringBuilder();
                var buffer = new char[8192];
                int lidos;
                while ((lidos = await fonte.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    if (texto.Length + lidos > maxCaracteresTexto)
                        throw new ResultadoGrandeException();
                    texto.Append(buffer, 0, lidos);
                }
                linha[leitor.GetName(i)] = texto.ToString();
            }
            else
                linha[leitor.GetName(i)] = leitor.GetValue(i);
        }
        return linha;
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

    /// <summary>Le o bloco `paginacao` do pedido (issue #24). Ausente = tudo padrao.</summary>
    private static PaginacaoPedido LerPaginacao(JsonElement pedido)
    {
        if (!pedido.TryGetProperty("paginacao", out var p) || p.ValueKind != JsonValueKind.Object)
            return PaginacaoPedido.Vazio;

        return new PaginacaoPedido
        {
            Pagina = LerInt(p, "pagina"),
            TamanhoPagina = LerInt(p, "tamanho_pagina"),
            OrdenarPor = LerTexto(p, "ordenar_por"),
            Direcao = LerTexto(p, "direcao"),
            IncluirTotal = p.TryGetProperty("incluir_total", out var it)
                           && it.ValueKind == JsonValueKind.True,
        };
    }

    private static int? LerInt(JsonElement el, string nome) =>
        el.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;
}
