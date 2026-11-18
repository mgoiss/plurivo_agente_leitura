using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgenteShop9.Operacoes;

/// <summary>Resultado guardado da validacao de uma foto (issue #182).</summary>
public sealed record EntradaValidacaoFoto(long Tamanho, bool Valida, DateTimeOffset ValidadoEm);

/// <summary>
/// Cache em disco do resultado da validacao de cada foto (issue #182). Evita reler
/// e revalidar (GDI+) os bytes de fotos que nao mudaram a cada reconstrucao do
/// indice de fotos.
///
/// - Chave = Ordem da foto (Prod_Serv_Fotos.Ordem, unica no banco): global, nao por
///   escopo. Identidade = Ordem + tamanho (DATALENGTH). Tamanho diferente invalida.
/// - Entrada vale <see cref="ValidadeDe"/> (24 h mais uma dispersao fixa por foto, de 0 a 12 h):
///   passado esse prazo a foto e relida. E a releitura periodica que pega foto trocada por outra
///   de mesmo tamanho. A dispersao evita que as fotos validadas na mesma construcao a frio
///   vencam todas juntas e forcem uma nova leitura completa (11,9 s medidos com 1621 fotos).
/// - Persistencia atomica (grava em .tmp e move). Arquivo ausente ou corrompido
///   comeca vazio. Falha de disco nao propaga: so perde o reaproveitamento.
/// - O arquivo guarda so numeros e booleanos; nada sensivel.
/// </summary>
public sealed class CacheValidacaoFotos(
    string caminho, Func<DateTimeOffset>? agora = null, ILogger? log = null)
{
    /// <summary>Prazo minimo da entrada: antes disso a foto nunca e revalidada.</summary>
    public static readonly TimeSpan Validade = TimeSpan.FromHours(24);

    /// <summary>Largura da dispersao somada a <see cref="Validade"/>, fixa por foto.</summary>
    public static readonly TimeSpan Dispersao = TimeSpan.FromHours(12);

    /// <summary>Prazo maximo da entrada: depois disso toda foto ja venceu.</summary>
    public static readonly TimeSpan ValidadeMaxima = Validade + Dispersao;

    /// <summary>
    /// Prazo da foto: <see cref="Validade"/> mais um desvio de 0 a <see cref="Dispersao"/> derivado da
    /// Ordem (deterministico, sem estado: leitura e poda concordam, e reiniciar nao muda o prazo).
    /// </summary>
    public static TimeSpan ValidadeDe(int imagemOrdem)
    {
        var mistura = unchecked((uint)imagemOrdem * 2654435761u);
        var fracao = (mistura >> 8) / (double)(1u << 24);
        return Validade + TimeSpan.FromSeconds(Math.Floor(fracao * Dispersao.TotalSeconds));
    }

    /// <summary>Arquivo maior que isso e tratado como corrompido (o normal tem poucas centenas de KB).</summary>
    public const long TamanhoMaximoArquivo = 32L * 1024 * 1024;

    /// <summary>Folga aceita para ValidadoEm adiante do relogio (ajuste de hora); alem disso a entrada e descartada.</summary>
    private static readonly TimeSpan FolgaRelogio = TimeSpan.FromHours(1);

    private readonly object _trava = new();
    private readonly Func<DateTimeOffset> _agora = agora ?? (() => DateTimeOffset.UtcNow);
    private Dictionary<int, EntradaValidacaoFoto>? _entradas;
    private bool _sujo;

    public int Quantidade
    {
        get { lock (_trava) return Carregar().Count; }
    }

    /// <summary>
    /// Resultado guardado da foto, se existir com o mesmo tamanho e dentro da validade.
    /// </summary>
    public bool TentarObter(int imagemOrdem, long tamanho, out bool valida)
    {
        lock (_trava)
        {
            if (Carregar().TryGetValue(imagemOrdem, out var entrada)
                && entrada.Tamanho == tamanho
                && _agora() - entrada.ValidadoEm < ValidadeDe(imagemOrdem)
                && _agora() >= entrada.ValidadoEm)
            {
                valida = entrada.Valida;
                return true;
            }
        }
        valida = false;
        return false;
    }

    public void Registrar(int imagemOrdem, long tamanho, bool valida)
    {
        lock (_trava)
        {
            Carregar()[imagemOrdem] = new EntradaValidacaoFoto(tamanho, valida, _agora());
            _sujo = true;
        }
    }

    /// <summary>
    /// Grava em disco o que mudou desde a ultima gravacao. Com <paramref name="ordensVivas"/>,
    /// antes poda as entradas que NAO apareceram nesta construcao E ja venceram. O cache e global e
    /// cada construcao enxerga so um escopo: podar o que nao esta nele apagaria as fotos exclusivas
    /// de outro escopo. Foto removida sai do indice na hora e do cache em ate <see cref="ValidadeMaxima"/>.
    /// Falha de disco nao propaga.
    /// </summary>
    public void Gravar(IReadOnlySet<int>? ordensVivas = null)
    {
        lock (_trava)
        {
            var entradas = Carregar();
            if (ordensVivas is not null)
            {
                var agora = _agora();
                foreach (var ordem in entradas
                             .Where(e => !ordensVivas.Contains(e.Key) && agora - e.Value.ValidadoEm >= ValidadeDe(e.Key))
                             .Select(e => e.Key).ToList())
                {
                    entradas.Remove(ordem);
                    _sujo = true;
                }
            }
            if (!_sujo)
                return;
            var temporario = caminho + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(caminho))!);
                File.WriteAllText(temporario, JsonSerializer.Serialize(entradas));
                File.Move(temporario, caminho, overwrite: true);
                _sujo = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sem persistencia so perde o reaproveitamento apos reiniciar o agente.
                log?.LogWarning("Cache de validacao de fotos nao foi gravado em disco ({Tipo}).",
                    ex.GetType().Name);
            }
        }
    }

    private Dictionary<int, EntradaValidacaoFoto> Carregar()
    {
        if (_entradas is not null)
            return _entradas;
        try
        {
            if (File.Exists(caminho) && new FileInfo(caminho).Length > TamanhoMaximoArquivo)
            {
                log?.LogWarning("Cache de validacao de fotos grande demais ({Bytes} bytes); comeca vazio.",
                    new FileInfo(caminho).Length);
                _entradas = [];
                return _entradas;
            }
            _entradas = File.Exists(caminho)
                ? JsonSerializer.Deserialize<Dictionary<int, EntradaValidacaoFoto>>(
                      File.ReadAllText(caminho)) ?? []
                : [];
            // JSON valido mas com entrada nula (null no valor) nao serve.
            if (_entradas.Values.Any(e => e is null))
                throw new JsonException("entrada nula");
            // Data no futuro alem da folga (relogio errado ou arquivo adulterado) nao vale: sera revalidada.
            var agora = _agora();
            foreach (var ordem in _entradas.Where(e => e.Value.ValidadoEm - agora > FolgaRelogio).Select(e => e.Key).ToList())
                _entradas.Remove(ordem);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log?.LogWarning("Cache de validacao de fotos ilegivel; comeca vazio ({Tipo}).",
                ex.GetType().Name);
            _entradas = [];
        }
        return _entradas;
    }
}
