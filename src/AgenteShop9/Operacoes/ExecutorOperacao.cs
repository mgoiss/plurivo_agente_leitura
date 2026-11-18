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

/// <summary>Aquecimento do indice de fotos ao iniciar o agente (issue #119).</summary>
public interface IAquecedorIndiceFotos
{
    /// <summary>Dispara a construcao do indice em segundo plano; nao bloqueia nem lanca.</summary>
    void Aquecer();
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
    GestorCatalogo gestorCatalogo) : IExecutorOperacao, IAquecedorIndiceFotos, IDisposable
{
    private const string OperacaoIndiceFotos = "catalogo_indice_fotos";
    private const string OperacaoImpressaoFotos = "catalogo_indice_fotos_impressao";
    // Catalogo v44 (#182): metadados sem o conteudo + leitura de uma foto por Ordem.
    private const string OperacaoMetadadosFotos = "catalogo_indice_fotos_metadados";
    private const string OperacaoFotoBase64 = "catalogo_foto_base64";
    private const string OperacaoVendasIndice = "catalogo_vendas_indice";
    public const string CodigoRankingIndisponivel = "ranking_indisponivel";

    private sealed class ResultadoGrandeException : Exception;
    // Homologacao #109: 1.252 fotos elegiveis exigiram 105 MiB e ultrapassaram
    // os limites iniciais. O prazo HTTP ainda e de 10 s; preparar o indice
    // pode concluir depois dele, para medir as consultas com cache aquecido.
    private static readonly TimeSpan TempoMaximoIndiceFotos = TimeSpan.FromSeconds(12);
    private readonly CacheIndiceFotos _cacheFotos = new(log);
    private readonly EscoposIndiceFotos _escoposFotos = new(Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(config.CatalogoCaminho)) ?? ".",
        "dados", "escopos_indice_fotos.json"));
    // Validacao por foto (#182): global (Ordem da foto e unica), nao por escopo nem por versao.
    private readonly CacheValidacaoFotos _validacaoFotos = new(Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(config.CatalogoCaminho)) ?? ".",
        "dados", "validacao_fotos.json"), log: log);
    // Indice de vendas (#179): chave = OrdemFilial, sem versao do catalogo.
    private readonly CacheIndiceVendas _cacheVendas = new(log);
    private readonly EscoposIndiceFotos _escoposVendas = new(Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(config.CatalogoCaminho)) ?? ".",
        "dados", "escopos_indice_vendas.json"));

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
        IReadOnlyDictionary<int, decimal>? vendas = null;
        if (op.UsaVendasIndice)
        {
            try
            {
                vendas = ObterIndiceVendas(catalogo, recebidos);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Falha ao preparar o indice de vendas da operacao {Op}.", nomeOperacao);
                return MensagemResultado.Erro(idRequisicao, "erro_consulta", "falha ao executar a consulta no banco.");
            }
            if (vendas is null && op.VendasIndiceOpcional)
                // A construcao ja foi disparada em ObterIndiceVendas; esta consulta segue sem ranking.
                vendas = new Dictionary<int, decimal>();
            else if (vendas is null)
                // Nao espera a construcao: o broker responde na ordem alfabetica.
                return MensagemResultado.Erro(idRequisicao, CodigoRankingIndisponivel,
                    "o ranking de vendas ainda nao esta pronto.");
        }
        try
        {
            await using var conexao = new SqlConnection(config.ConexaoSql);
            await conexao.OpenAsync(ct);
            if (op.UsaFotosValidadas)
            {
                etapa = "indice_fotos";
                var fotos = await ObterIndiceFotos(catalogo, recebidos, ct);
                etapa = "tabela_fotos";
                await PrepararTabelaFotos(conexao, fotos, ct);
            }

            if (vendas is not null)
            {
                etapa = "tabela_vendas";
                await PrepararTabelaVendas(conexao, vendas, ct);
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
        catch (IndiceFotosTempoException ex)
        {
            log.LogWarning("Operacao {Op} excedeu o tempo do indice de fotos apos {DuracaoMs} ms: {Linhas} linhas, {Caracteres} caracteres, {Lidas} fotos lidas do banco, {DoCache} do cache.",
                nomeOperacao, relogio.ElapsedMilliseconds, ex.LinhasLidas, ex.CaracteresLidos,
                ex.LidasDoBanco, ex.DoCache);
            return MensagemResultado.Erro(idRequisicao, "timeout", "a preparacao de fotos excedeu o tempo permitido.");
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
            log.LogWarning("Operacao {Op} excedeu o indice de fotos na etapa {Etapa}: {Motivo}, {Linhas} linhas, {Caracteres} caracteres, {Lidas} fotos lidas do banco, {DoCache} do cache, {DuracaoMs} ms.",
                nomeOperacao, etapa, ex.Motivo, ex.LinhasLidas, ex.CaracteresLidos,
                ex.LidasDoBanco, ex.DoCache, relogio.ElapsedMilliseconds);
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
        CancellationToken ct)
    {
        var parametros = IndiceFotosPublicas.ParametrosEscopo(recebidos);
        var chaveEscopo = IndiceFotosPublicas.ChaveEscopo(catalogo.VersaoCatalogo, parametros);
        var (construtor, impressao) = CriarAcessoIndiceFotos(catalogo, parametros);
        _escoposFotos.Registrar(parametros);
        return await _cacheFotos.ObterAsync(chaveEscopo, construtor, impressao, ct);
    }

    /// <summary>
    /// Aquecimento ao iniciar (issue #119): constroi em segundo plano o indice
    /// dos escopos ja vistos em requisicoes anteriores (guardados em disco).
    /// Escopo de um servidor novo, ainda nunca visto, so e conhecido na primeira
    /// requisicao — esta paga a carga completa uma vez. Nunca bloqueia nem lanca.
    /// </summary>
    public void Aquecer()
    {
        AquecerVendas();
        try
        {
            var catalogo = gestorCatalogo.CatalogoAceito;
            if (catalogo?.Operacoes is null
                || !catalogo.Operacoes.TryGetValue(OperacaoIndiceFotos, out var op) || !op.UsoInterno)
                return;
            foreach (var parametros in _escoposFotos.Listar())
            {
                try
                {
                    var chave = IndiceFotosPublicas.ChaveEscopo(catalogo.VersaoCatalogo, parametros);
                    var (construtor, impressao) = CriarAcessoIndiceFotos(catalogo, parametros);
                    _ = _cacheFotos.Aquecer(chave, construtor, impressao);
                }
                catch (ArgumentException)
                {
                    _escoposFotos.Remover(parametros);
                    log.LogWarning("Escopo guardado do indice de fotos invalido; removido do disco.");
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning("Aquecimento do indice de fotos nao iniciou ({Tipo}).", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Aquecimento do indice de vendas (#179): uma construcao por filial ja vista
    /// (guardada em disco). Nunca bloqueia nem lanca.
    /// </summary>
    private void AquecerVendas()
    {
        try
        {
            var catalogo = gestorCatalogo.CatalogoAceito;
            if (catalogo?.Operacoes is null
                || !catalogo.Operacoes.TryGetValue(OperacaoVendasIndice, out var op) || !op.UsoInterno)
                return;
            foreach (var parametros in _escoposVendas.Listar())
            {
                try
                {
                    _ = _cacheVendas.Aquecer(ChaveVendas(parametros),
                        CriarConstrutorVendas(catalogo, parametros));
                }
                catch (ArgumentException)
                {
                    _escoposVendas.Remover(parametros);
                    log.LogWarning("Escopo guardado do indice de vendas invalido; removido do disco.");
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning("Aquecimento do indice de vendas nao iniciou ({Tipo}).", ex.GetType().Name);
        }
    }

    public void Dispose()
    {
        _cacheFotos.Dispose();
        _cacheVendas.Dispose();
    }

    /// <summary>Chave do indice de vendas: so a filial. Nunca a versao do catalogo.</summary>
    public static string ChaveVendas(IReadOnlyDictionary<string, JsonElement> parametros)
    {
        if (!parametros.TryGetValue("OrdemFilial", out var filial)
            || filial.ValueKind != JsonValueKind.Number || !filial.TryGetInt32(out var ordem)
            || ordem < 1)
            throw new ArgumentException("Filial do indice de vendas invalida.");
        return ordem.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private IReadOnlyDictionary<int, decimal>? ObterIndiceVendas(
        Catalogo catalogo, IReadOnlyDictionary<string, JsonElement> recebidos)
    {
        var parametros = new Dictionary<string, JsonElement>();
        if (recebidos.TryGetValue("OrdemFilial", out var filial))
            parametros["OrdemFilial"] = filial;
        var chave = ChaveVendas(parametros);
        var construtor = CriarConstrutorVendas(catalogo, parametros);
        _escoposVendas.Registrar(parametros);
        return _cacheVendas.Obter(chave, construtor);
    }

    private CacheIndiceVendas.Construtor CriarConstrutorVendas(
        Catalogo catalogo, Dictionary<string, JsonElement> parametros)
    {
        if (catalogo.Operacoes is null
            || !catalogo.Operacoes.TryGetValue(OperacaoVendasIndice, out var operacao)
            || !operacao.UsoInterno)
            throw new InvalidOperationException("Catalogo sem indice de vendas assinado.");
        if (ValidadorParametros.Validar(operacao, parametros) is not null)
            throw new ArgumentException("Escopo do indice de vendas invalido.");
        return ct => ConstruirIndiceVendas(operacao, parametros, ct);
    }

    private async Task<IReadOnlyDictionary<int, decimal>> ConstruirIndiceVendas(
        Operacao operacao, Dictionary<string, JsonElement> parametros, CancellationToken ct)
    {
        await using var conexao = new SqlConnection(config.ConexaoSql);
        await conexao.OpenAsync(ct);
        var vendas = new Dictionary<int, decimal>();
        await using var comando = MontadorComando.Montar(operacao, parametros, conexao: conexao);
        await using var leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            if (vendas.Count >= operacao.MaxLinhas)
                throw new IndiceVendasCapacidadeException();
            if (leitor.IsDBNull(0))
                continue;
            vendas[leitor.GetInt32(0)] = leitor.IsDBNull(1) ? 0m : leitor.GetDecimal(1);
        }
        return vendas;
    }

    /// <summary>Teto da carga das tabelas temporarias; o <c>CancellationToken</c> segue valendo antes dele.</summary>
    private const int TimeoutCargaTabelaTemporariaSegundos = 60;

    private sealed class IndiceVendasCapacidadeException : Exception;

    private static async Task PrepararTabelaVendas(
        SqlConnection conexao, IReadOnlyDictionary<int, decimal> vendas, CancellationToken ct)
    {
        await using (var comando = conexao.CreateCommand())
        {
            comando.CommandText = "CREATE TABLE #VendasIndice (Produto_Ordem int NOT NULL PRIMARY KEY, Quantidade decimal(19,4) NOT NULL)";
            await comando.ExecuteNonQueryAsync(ct);
        }
        if (vendas.Count == 0)
            return;
        var tabela = new DataTable();
        tabela.Columns.Add("Produto_Ordem", typeof(int));
        tabela.Columns.Add("Quantidade", typeof(decimal));
        foreach (var (produto, quantidade) in vendas)
            tabela.Rows.Add(produto, quantidade);
        using var copia = new SqlBulkCopy(conexao)
        {
            DestinationTableName = "#VendasIndice",
            BatchSize = 1000,
            BulkCopyTimeout = TimeoutCargaTabelaTemporariaSegundos,
        };
        await copia.WriteToServerAsync(tabela, ct);
    }

    /// <summary>
    /// Delegates do cache para o escopo. Cada um abre a PROPRIA conexao: a da
    /// requisicao fecha antes de a atualizacao em segundo plano terminar.
    /// Sem a operacao de impressao no catalogo (catalogo antigo), a impressao e
    /// nula e o cache relê tudo ao atualizar.
    /// </summary>
    private (CacheIndiceFotos.Construtor, CacheIndiceFotos.LeitorImpressao?) CriarAcessoIndiceFotos(
        Catalogo catalogo, Dictionary<string, JsonElement> parametros)
    {
        if (catalogo.Operacoes is null
            || !catalogo.Operacoes.TryGetValue(OperacaoIndiceFotos, out var operacao)
            || !operacao.UsoInterno)
            throw new InvalidOperationException("Catalogo publico sem indice de fotos assinado.");
        if (ValidadorParametros.Validar(operacao, parametros) is not null)
            throw new ArgumentException("Escopo do indice de fotos invalido.");

        CacheIndiceFotos.LeitorImpressao? impressao = null;
        if (catalogo.Operacoes.TryGetValue(OperacaoImpressaoFotos, out var opImpressao)
            && opImpressao.UsoInterno
            && ValidadorParametros.Validar(opImpressao, parametros) is null)
            impressao = ct => LerImpressaoFotos(opImpressao, parametros, ct);

        // Catalogo v44+: metadados + cache de validacao (le so as fotos novas). Sem as duas
        // operacoes assinadas e validas, segue no caminho antigo (relê todas as fotos).
        if (TentarObterOperacoesPorMetadados(catalogo, parametros, out var opMetadados, out var opFoto))
            return (ct => ConstruirIndiceFotosPorMetadados(opMetadados, opFoto, parametros, ct), impressao);

        return (ct => ConstruirIndiceFotos(operacao, parametros, ct), impressao);
    }

    /// <summary>
    /// True se o catalogo traz as duas operacoes do indice por metadados (#182), internas e com
    /// parametros validos para o escopo. Catalogo v43 ou anterior: false (caminho antigo).
    /// </summary>
    public static bool TentarObterOperacoesPorMetadados(
        Catalogo catalogo, IReadOnlyDictionary<string, JsonElement> parametros,
        out Operacao opMetadados, out Operacao opFoto)
    {
        opMetadados = opFoto = null!;
        if (catalogo.Operacoes is null
            || !catalogo.Operacoes.TryGetValue(OperacaoMetadadosFotos, out var metadados)
            || !metadados.UsoInterno
            || ValidadorParametros.Validar(metadados, parametros) is not null
            || !catalogo.Operacoes.TryGetValue(OperacaoFotoBase64, out var foto)
            || !foto.UsoInterno
            || ValidadorParametros.Validar(foto, ParametrosFoto(1)) is not null)
            return false;
        opMetadados = metadados;
        opFoto = foto;
        return true;
    }

    private static Dictionary<string, JsonElement> ParametrosFoto(int ordemFoto) =>
        new() { ["OrdemFoto"] = JsonSerializer.SerializeToElement(ordemFoto) };

    /// <summary>
    /// Indice de fotos com cache de validacao (#182): le os metadados (sem bytes) e, na mesma
    /// conexao, so o Base64 das fotos que o cache nao conhece. Mesmos limites do caminho antigo.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, int>> ConstruirIndiceFotosPorMetadados(
        Operacao opMetadados, Operacao opFoto, Dictionary<string, JsonElement> parametros,
        CancellationToken ct)
    {
        var relogioIndice = Stopwatch.StartNew();
        await using var conexao = new SqlConnection(config.ConexaoSql);
        await conexao.OpenAsync(ct);
        var metadados = new List<MetadadoFoto>();
        await using (var comando = MontadorComando.Montar(opMetadados, parametros, conexao: conexao))
        await using (var leitor = await comando.ExecuteReaderAsync(ct))
        {
            // O leitor fecha antes das leituras de foto (sem MARS, a conexao so aguenta um por vez).
            while (await leitor.ReadAsync(ct))
            {
                if (metadados.Count >= AcumuladorIndiceFotosPublicas.MaxLinhas)
                    throw new IndiceFotosCapacidadeException("linhas", metadados.Count + 1, 0);
                metadados.Add(new MetadadoFoto(
                    leitor.GetInt32(0), leitor.GetInt32(1), Convert.ToInt64(leitor.GetValue(2))));
            }
        }

        async Task<string?> LerFoto(int ordemFoto, CancellationToken token)
        {
            await using var cmd = MontadorComando.Montar(opFoto, ParametrosFoto(ordemFoto), conexao: conexao);
            var bruto = await cmd.ExecuteScalarAsync(token);
            return bruto as string;
        }

        IReadOnlyDictionary<int, int> fotos;
        ResumoConstrucaoIndiceFotos resumo;
        try
        {
            (fotos, resumo) = await ConstrutorIndiceFotosPorMetadados.ConstruirAsync(
                metadados, _validacaoFotos, LerFoto, IndiceFotosPublicas.FotoValida,
                () => relogioIndice.Elapsed, TempoMaximoIndiceFotos, ct);
        }
        catch (IndiceFotosTempoException ex)
        {
            // Evidencia tambem na construcao em segundo plano (so numeros); o progresso ja foi gravado no cache.
            log.LogWarning(
                "Indice de fotos interrompido por tempo: {Total} fotos no escopo, {Lidas} lidas do banco, {DoCache} do cache, {DuracaoMs} ms.",
                metadados.Count, ex.LidasDoBanco, ex.DoCache, relogioIndice.ElapsedMilliseconds);
            throw;
        }
        catch (IndiceFotosCapacidadeException ex)
        {
            log.LogWarning(
                "Indice de fotos interrompido por limite ({Motivo}): {Total} fotos no escopo, {Lidas} lidas do banco, {DoCache} do cache, {DuracaoMs} ms.",
                ex.Motivo, metadados.Count, ex.LidasDoBanco, ex.DoCache, relogioIndice.ElapsedMilliseconds);
            throw;
        }
        log.LogInformation(
            "Indice de fotos construido: {Total} fotos no escopo, {Lidas} lidas do banco, {DoCache} do cache, {Produtos} produtos com foto valida, {DuracaoMs} ms.",
            resumo.TotalFotos, resumo.LidasDoBanco, resumo.DoCache, resumo.ProdutosComFoto,
            relogioIndice.ElapsedMilliseconds);
        return fotos;
    }

    private async Task<string> LerImpressaoFotos(
        Operacao operacao, Dictionary<string, JsonElement> parametros, CancellationToken ct)
    {
        await using var conexao = new SqlConnection(config.ConexaoSql);
        await conexao.OpenAsync(ct);
        await using var comando = MontadorComando.Montar(operacao, parametros, conexao: conexao);
        await using var leitor = await comando.ExecuteReaderAsync(ct);
        if (!await leitor.ReadAsync(ct))
            throw new InvalidOperationException("Impressao digital do indice de fotos sem linha.");
        return string.Join("|", Enumerable.Range(0, leitor.FieldCount)
            .Select(i => Convert.ToInt64(leitor.GetValue(i))));
    }

    private async Task<IReadOnlyDictionary<int, int>> ConstruirIndiceFotos(
        Operacao operacao, Dictionary<string, JsonElement> parametros, CancellationToken ct)
    {
        await using var conexao = new SqlConnection(config.ConexaoSql);
        await conexao.OpenAsync(ct);
        var acumulador = new AcumuladorIndiceFotosPublicas();
        var relogioIndice = Stopwatch.StartNew();
        await using (var comando = MontadorComando.Montar(
            operacao, parametros, conexao: conexao))
        await using (var leitor = await comando.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess, ct))
        {
            while (await leitor.ReadAsync(ct))
            {
                if (relogioIndice.Elapsed > TempoMaximoIndiceFotos)
                    throw new IndiceFotosTempoException(
                        acumulador.LinhasLidas, acumulador.CaracteresLidos);
                var produtoOrdem = leitor.GetInt32(0);
                var imagemOrdem = leitor.GetInt32(1);
                var tamanho = Convert.ToInt64(leitor.GetValue(2));
                var texto = acumulador.PrecisaLerFoto(produtoOrdem)
                    ? leitor.GetString(3) : null;
                acumulador.Incluir(produtoOrdem, imagemOrdem, tamanho,
                                  texto, IndiceFotosPublicas.FotoValida);
            }
        }
        if (relogioIndice.Elapsed > TempoMaximoIndiceFotos)
            throw new IndiceFotosTempoException(
                acumulador.LinhasLidas, acumulador.CaracteresLidos);
        return acumulador.Fotos;
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
            BulkCopyTimeout = TimeoutCargaTabelaTemporariaSegundos,
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
            // max_linhas e limite exato: ler a linha max_linhas+1 ja devolve resultado_grande (#117).
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
