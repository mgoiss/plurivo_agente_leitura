using System.Text.Json;

namespace AgenteShop9.Operacoes;

/// <summary>
/// Guarda em disco os escopos (filial + tabelas de preco) do indice de fotos ja
/// vistos em requisicoes, para o aquecimento da proxima subida do agente
/// (issue #119). O agente so descobre o escopo quando o backend manda o pedido;
/// sem isso a primeira vitrine apos cada reinicio pagaria a carga completa.
/// O arquivo tem so numeros (CodigoFilial, Tabela_NN); nada sensivel.
/// </summary>
public sealed class EscoposIndiceFotos(string caminho, int maximo = 16)
{
    private readonly object _trava = new();
    private List<Dictionary<string, int>>? _escopos;

    /// <summary>Escopos conhecidos, do mais antigo para o mais recente.</summary>
    public IReadOnlyList<Dictionary<string, JsonElement>> Listar()
    {
        lock (_trava)
            return Carregar().Select(Converter).ToList();
    }

    /// <summary>Registra o escopo; devolve true se era novo. Falha de disco nao propaga.</summary>
    public bool Registrar(IReadOnlyDictionary<string, JsonElement> parametros)
    {
        var novo = new Dictionary<string, int>();
        foreach (var (nome, valor) in parametros)
            if (valor.ValueKind == JsonValueKind.Number && valor.TryGetInt32(out var n))
                novo[nome] = n;
        lock (_trava)
        {
            var lista = Carregar();
            if (lista.Any(e => e.Count == novo.Count && !e.Except(novo).Any()))
                return false;
            lista.Add(novo);
            while (lista.Count > maximo)
                lista.RemoveAt(0);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(caminho))!);
                File.WriteAllText(caminho, JsonSerializer.Serialize(lista));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sem persistencia so perde o aquecimento da proxima subida.
            }
            return true;
        }
    }

    private List<Dictionary<string, int>> Carregar()
    {
        if (_escopos is not null)
            return _escopos;
        try
        {
            _escopos = File.Exists(caminho)
                ? JsonSerializer.Deserialize<List<Dictionary<string, int>>>(File.ReadAllText(caminho)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _escopos = [];
        }
        return _escopos;
    }

    private static Dictionary<string, JsonElement> Converter(Dictionary<string, int> escopo) =>
        escopo.ToDictionary(par => par.Key, par => JsonSerializer.SerializeToElement(par.Value));
}
