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
/// - Entrada vale <see cref="Validade"/>: passado esse prazo a foto e relida. E a
///   releitura completa periodica que pega foto trocada por outra de mesmo tamanho.
/// - Persistencia atomica (grava em .tmp e move). Arquivo ausente ou corrompido
///   comeca vazio. Falha de disco nao propaga: so perde o reaproveitamento.
/// - O arquivo guarda so numeros e booleanos; nada sensivel.
/// </summary>
public sealed class CacheValidacaoFotos(
    string caminho, Func<DateTimeOffset>? agora = null, ILogger? log = null)
{
    /// <summary>Prazo da entrada: acima disso a foto e revalidada.</summary>
    public static readonly TimeSpan Validade = TimeSpan.FromHours(24);

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
                && _agora() - entrada.ValidadoEm < Validade
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
    /// antes poda as entradas de fotos que nao aparecem mais (foto removida ou fora do escopo).
    /// Falha de disco nao propaga.
    /// </summary>
    public void Gravar(IReadOnlySet<int>? ordensVivas = null)
    {
        lock (_trava)
        {
            var entradas = Carregar();
            if (ordensVivas is not null)
                foreach (var ordem in entradas.Keys.Where(o => !ordensVivas.Contains(o)).ToList())
                {
                    entradas.Remove(ordem);
                    _sujo = true;
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
            _entradas = File.Exists(caminho)
                ? JsonSerializer.Deserialize<Dictionary<int, EntradaValidacaoFoto>>(
                      File.ReadAllText(caminho)) ?? []
                : [];
            // JSON valido mas com entrada nula (null no valor) nao serve.
            if (_entradas.Values.Any(e => e is null))
                throw new JsonException("entrada nula");
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
