namespace AgenteShop9.Operacoes;

/// <summary>Uma foto do escopo, sem o conteudo: o que `catalogo_indice_fotos_metadados` devolve.</summary>
public readonly record struct MetadadoFoto(int ProdutoOrdem, int ImagemOrdem, long Caracteres);

/// <summary>Contagens de uma construcao, para o log (sem dado sensivel).</summary>
public sealed record ResumoConstrucaoIndiceFotos(
    int TotalFotos, int LidasDoBanco, int DoCache, int ProdutosComFoto);

/// <summary>
/// Constroi o indice de fotos validas a partir dos metadados (issue #182): le os bytes
/// so das fotos que o <see cref="CacheValidacaoFotos"/> nao conhece (nova, tamanho
/// alterado ou entrada vencida). Nao conhece SQL nem GDI+: a leitura da foto e a
/// validacao entram como delegates.
///
/// Mantem a regra e as protecoes de <see cref="AcumuladorIndiceFotosPublicas"/>:
/// por produto percorre as fotos na ordem recebida e para na primeira valida (as
/// demais nem sao lidas); limites de linhas, fotos por produto, tamanho e volume
/// de texto (so do que foi lido) seguem valendo, assim como o tempo maximo.
/// </summary>
public static class ConstrutorIndiceFotosPorMetadados
{
    public static async Task<(IReadOnlyDictionary<int, int> Fotos, ResumoConstrucaoIndiceFotos Resumo)>
        ConstruirAsync(
            IReadOnlyList<MetadadoFoto> metadados,
            CacheValidacaoFotos cache,
            Func<int, CancellationToken, Task<string?>> lerFoto,
            Func<string?, bool> validar,
            Func<TimeSpan> decorrido,
            TimeSpan tempoMaximo,
            CancellationToken ct,
            int paralelismo = 1)
    {
        var acumulador = new AcumuladorIndiceFotosPublicas();
        var lidas = 0;
        var doCache = 0;
        IReadOnlySet<int> lidasNoAquecimento = new HashSet<int>();
        try
        {
            if (paralelismo > 1)
                lidasNoAquecimento = await PreAquecerAsync(
                    metadados, cache, lerFoto, validar, decorrido, tempoMaximo, paralelismo, ct);

            foreach (var meta in metadados)
            {
                ct.ThrowIfCancellationRequested();
                if (decorrido() > tempoMaximo)
                    throw new IndiceFotosTempoException(
                        acumulador.LinhasLidas, acumulador.CaracteresLidos);

                string? texto = null;
                bool? validaNoCache = null;
                if (acumulador.PrecisaLerFoto(meta.ProdutoOrdem))
                {
                    if (cache.TentarObter(meta.ImagemOrdem, meta.Caracteres, out var valida))
                    {
                        validaNoCache = valida;
                        // Lida do banco pelo aquecimento nesta mesma construcao: conta como lida, nao como cache.
                        if (lidasNoAquecimento.Contains(meta.ImagemOrdem))
                            lidas++;
                        else
                            doCache++;
                    }
                    else
                    {
                        texto = await lerFoto(meta.ImagemOrdem, ct);
                        lidas++;
                    }
                }

                var lido = texto;
                var m = meta;
                acumulador.Incluir(m.ProdutoOrdem, m.ImagemOrdem, m.Caracteres, lido, t =>
                {
                    if (validaNoCache is not null)
                        return validaNoCache.Value;
                    var ok = validar(t);
                    // Foto que sumiu entre os metadados e a leitura (texto nulo) nao entra no cache.
                    if (t is not null)
                        cache.Registrar(m.ImagemOrdem, m.Caracteres, ok);
                    return ok;
                });
            }
            if (decorrido() > tempoMaximo)
                throw new IndiceFotosTempoException(
                    acumulador.LinhasLidas, acumulador.CaracteresLidos);
        }
        catch (Exception ex)
        {
            // Tempo estourado, limite ou cancelamento: guarda o que ja foi validado
            // (sem podar) para a proxima tentativa avancar.
            cache.Gravar();
            switch (ex)
            {
                case IndiceFotosTempoException tempo:
                    tempo.LidasDoBanco = lidas;
                    tempo.DoCache = doCache;
                    break;
                case IndiceFotosCapacidadeException capacidade:
                    capacidade.LidasDoBanco = lidas;
                    capacidade.DoCache = doCache;
                    break;
            }
            throw;
        }
        cache.Gravar(metadados.Select(m => m.ImagemOrdem).ToHashSet());
        return (acumulador.Fotos,
            new ResumoConstrucaoIndiceFotos(
                metadados.Count, lidas, doCache, acumulador.Fotos.Count));
    }

    /// <summary>
    /// Leitura fria rapida: le e valida em paralelo a primeira foto ainda desconhecida de cada produto
    /// (a que a passada sequencial leria) e guarda o resultado no cache. A passada sequencial que vem
    /// depois acha quase tudo no cache e so le o que sobrou (a segunda foto de produto cuja primeira
    /// e invalida). Nao altera o resultado nem os limites: so adianta o trabalho. Cada foto lida fica
    /// no maximo 1 MB e e descartada apos validar, entao a memoria fica em paralelismo x 1 MB.
    /// Passar do tempo maximo ou qualquer falha aqui so encerra o aquecimento: o progresso ja
    /// registrado vale e a passada sequencial decide (inclusive lancar o timeout). Devolve as ordens
    /// lidas do banco aqui, para o resumo contar como lidas e nao como cache.
    /// </summary>
    private static async Task<IReadOnlySet<int>> PreAquecerAsync(
        IReadOnlyList<MetadadoFoto> metadados,
        CacheValidacaoFotos cache,
        Func<int, CancellationToken, Task<string?>> lerFoto,
        Func<string?, bool> validar,
        Func<TimeSpan> decorrido,
        TimeSpan tempoMaximo,
        int paralelismo,
        CancellationToken ct)
    {
        var pendentes = new List<MetadadoFoto>();
        foreach (var grupo in metadados.GroupBy(m => m.ProdutoOrdem))
        {
            foreach (var foto in grupo)
            {
                if (!cache.TentarObter(foto.ImagemOrdem, foto.Caracteres, out var valida))
                {
                    pendentes.Add(foto);
                    break;
                }
                if (valida)
                    break;
            }
        }
        var lidasAgora = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        if (pendentes.Count == 0)
            return new HashSet<int>();

        try
        {
            await Parallel.ForEachAsync(
                pendentes,
                new ParallelOptions { MaxDegreeOfParallelism = paralelismo, CancellationToken = ct },
                async (foto, token) =>
                {
                    if (decorrido() > tempoMaximo)
                        throw new IndiceFotosTempoException(0, 0);
                    var texto = await lerFoto(foto.ImagemOrdem, token);
                    // Foto que sumiu entre os metadados e a leitura (texto nulo) nao entra no cache.
                    if (texto is not null)
                    {
                        cache.Registrar(foto.ImagemOrdem, foto.Caracteres, validar(texto));
                        lidasAgora[foto.ImagemOrdem] = 0;
                    }
                });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Tempo, banco ou GDI+: o aquecimento para aqui e a passada sequencial segue com o que ficou no cache.
        }
        cache.Gravar();
        return lidasAgora.Keys.ToHashSet();
    }
}
