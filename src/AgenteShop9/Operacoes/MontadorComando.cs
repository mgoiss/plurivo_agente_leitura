using System.Data;
using System.Text.Json;
using AgenteShop9.Catalogos;
using Microsoft.Data.SqlClient;

namespace AgenteShop9.Operacoes;

/// <summary>
/// Monta o SqlCommand de uma operacao: o SQL fixo do catalogo + `@max_linhas` +
/// cada parametro DECLARADO (DBNull para os opcionais ausentes). Nao executa —
/// o que permite inspecionar o comando nos testes sem banco.
///
/// ISSUE #7 / spec 3.3: valor sempre via SqlParameter, nunca concatenado. Texto
/// vai como VarChar (ANSI): as colunas do Shop9 sao varchar; NVarChar forcaria
/// conversao implicita e o SQL Server ignoraria o indice.
/// </summary>
public static class MontadorComando
{
    public static SqlCommand Montar(
        Operacao op,
        IReadOnlyDictionary<string, JsonElement> recebidos,
        SqlConnection? conexao = null)
    {
        var cmd = new SqlCommand { CommandText = op.Sql, CommandType = CommandType.Text };
        if (conexao is not null) cmd.Connection = conexao;
        cmd.CommandTimeout = Math.Max(1, op.TimeoutMs / 1000);

        cmd.Parameters.Add("@max_linhas", SqlDbType.Int).Value = op.MaxLinhas;

        foreach (var p in op.Parametros)
        {
            var tem = recebidos.TryGetValue(p.Nome, out var v)
                      && v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

            var par = cmd.Parameters.Add("@" + p.Nome, TipoSql(p.Tipo));
            par.Value = tem ? Converter(p.Tipo, v) : DBNull.Value;
        }

        return cmd;
    }

    private static SqlDbType TipoSql(string tipo) => tipo switch
    {
        "inteiro" => SqlDbType.Int,
        // ISSUE #7: VarChar (ANSI), NAO NVarChar. As colunas de texto do Shop9
        // sao varchar; NVarChar forca conversao implicita e mata o uso de indice.
        "texto" => SqlDbType.VarChar,
        "decimal" => SqlDbType.Decimal,
        "data" or "date" => SqlDbType.Date,
        _ => throw new ArgumentOutOfRangeException(nameof(tipo), tipo, "tipo de parametro desconhecido"),
    };

    private static object Converter(string tipo, JsonElement v) => tipo switch
    {
        "inteiro" => v.GetInt64(),
        "decimal" => v.GetDecimal(),
        "texto" => v.GetString() ?? string.Empty,
        "data" or "date" => DateTime.Parse(v.GetString()!),
        _ => throw new ArgumentOutOfRangeException(nameof(tipo)),
    };
}
