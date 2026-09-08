using System.Text.Json;
using AgenteShop9.Catalogos;

namespace AgenteShop9.Operacoes;

public sealed record ErroValidacao(string Codigo, string Mensagem);

/// <summary>
/// Valida os parametros recebidos contra a declaracao da operacao — ANTES de
/// tocar no banco (spec 3.3). Parametro fora da regra = erro `parametro_invalido`.
/// </summary>
public static class ValidadorParametros
{
    public static ErroValidacao? Validar(
        Operacao op, IReadOnlyDictionary<string, JsonElement> recebidos)
    {
        var declarados = op.Parametros.Select(p => p.Nome).ToHashSet();

        foreach (var nome in recebidos.Keys)
            if (!declarados.Contains(nome))
                return new ErroValidacao("parametro_invalido", $"parametro nao declarado: {nome}");

        foreach (var p in op.Parametros)
        {
            var presente = recebidos.TryGetValue(p.Nome, out var valor)
                           && valor.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

            if (!presente)
            {
                if (p.Obrigatorio)
                    return new ErroValidacao(
                        "parametro_invalido", $"parametro obrigatorio ausente: {p.Nome}");
                continue;
            }

            if (!TipoCompativel(p.Tipo, valor))
                return new ErroValidacao(
                    "parametro_invalido", $"parametro '{p.Nome}' esperava tipo '{p.Tipo}'.");
        }

        return null;
    }

    private static bool TipoCompativel(string tipo, JsonElement v) => tipo switch
    {
        "inteiro" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
        "decimal" => v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out _),
        "texto" => v.ValueKind == JsonValueKind.String,
        "data" or "date" => v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), out _),
        _ => false,
    };
}
