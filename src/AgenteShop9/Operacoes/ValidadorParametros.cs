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

        if (op.PeriodoMaxDias is int maxDias)
        {
            var erroFaixa = ValidarFaixaDatas(recebidos, maxDias);
            if (erroFaixa is not null)
                return erroFaixa;
        }

        return null;
    }

    /// <summary>
    /// Quando a operacao declara `periodo_max_dias`, a janela entre `inicio` e
    /// `fim` nao pode estar invertida nem passar do limite (spec 4.4). So roda
    /// se os dois vierem e forem data — a obrigatoriedade ja foi checada acima.
    /// A conta e por DIA de calendario: `inicio`/`fim` podem vir com hora
    /// (relatorios que filtram por data+hora), e a hora nas pontas nao deve
    /// mudar a contagem de dias.
    /// </summary>
    private static ErroValidacao? ValidarFaixaDatas(
        IReadOnlyDictionary<string, JsonElement> recebidos, int maxDias)
    {
        if (!TentarData(recebidos, "inicio", out var inicio)
            || !TentarData(recebidos, "fim", out var fim))
            return null;

        if (fim.Date < inicio.Date)
            return new ErroValidacao(
                "parametro_invalido", "o 'fim' nao pode ser antes do 'inicio'.");

        if ((fim.Date - inicio.Date).TotalDays > maxDias)
            return new ErroValidacao(
                "parametro_invalido",
                $"o periodo entre inicio e fim nao pode passar de {maxDias} dias.");

        return null;
    }

    private static bool TentarData(
        IReadOnlyDictionary<string, JsonElement> recebidos, string nome, out DateTime data)
    {
        data = default;
        return recebidos.TryGetValue(nome, out var v)
               && v.ValueKind == JsonValueKind.String
               && DateTime.TryParse(v.GetString(), out data);
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
