using System.Data;
using System.Text.Json;
using AgenteShop9.Catalogos;
using Microsoft.Data.SqlClient;

namespace AgenteShop9.Operacoes;

/// <summary>
/// Monta o SqlCommand de uma operacao: o SQL fixo do catalogo + `@max_linhas` +
/// cada parametro DECLARADO (DBNull para os opcionais ausentes) + , quando a
/// operacao e paginada (issue #24), o `ORDER BY ... OFFSET ... FETCH` e os
/// parametros `@offset` / `@buscar`. Nao executa — o que permite inspecionar o
/// comando nos testes sem banco.
///
/// ISSUE #7 / spec 3.3: valor sempre via SqlParameter, nunca concatenado. Texto
/// vai como VarChar (ANSI): as colunas do Shop9 sao varchar; NVarChar forcaria
/// conversao implicita e o SQL Server ignoraria o indice.
///
/// ISSUE #24 / spec 3.3: o fragmento de `ORDER BY` (coluna real + desempate) sai
/// do CATALOGO ASSINADO — `ordenar_por` do front so escolhe uma chave. `@offset`
/// e `@buscar` entram como VALOR, nunca no texto.
/// </summary>
public static class MontadorComando
{
    /// <summary>Comando da pagina: SQL da operacao (+ ORDER BY/OFFSET/FETCH se paginada).</summary>
    public static SqlCommand Montar(
        Operacao op,
        IReadOnlyDictionary<string, JsonElement> recebidos,
        PaginacaoResolvida? paginacao = null,
        SqlConnection? conexao = null)
    {
        var sql = op.Sql;
        if (paginacao is not null)
            sql += ClausulaPaginacao(op, paginacao);

        var cmd = ComandoBase(op, sql, recebidos, conexao);

        if (paginacao is not null)
        {
            cmd.Parameters.Add("@offset", SqlDbType.BigInt).Value = paginacao.Offset;
            cmd.Parameters.Add("@buscar", SqlDbType.Int).Value = paginacao.Buscar;
        }

        return cmd;
    }

    /// <summary>
    /// Comando de contagem: o `sql_contagem` da operacao com os mesmos parametros
    /// declarados (sem `@offset` / `@buscar`). So use quando o pedido traz
    /// `incluir_total` — a operacao tem que declarar `sql_contagem`.
    /// </summary>
    public static SqlCommand MontarContagem(
        Operacao op,
        IReadOnlyDictionary<string, JsonElement> recebidos,
        SqlConnection? conexao = null)
    {
        if (string.IsNullOrWhiteSpace(op.SqlContagem))
            throw new InvalidOperationException("operacao sem 'sql_contagem'.");
        return ComandoBase(op, op.SqlContagem!, recebidos, conexao);
    }

    private static SqlCommand ComandoBase(
        Operacao op,
        string sql,
        IReadOnlyDictionary<string, JsonElement> recebidos,
        SqlConnection? conexao)
    {
        var cmd = new SqlCommand { CommandText = sql, CommandType = CommandType.Text };
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

    /// <summary>
    /// ` ORDER BY <coluna escolhida> <ASC|DESC>, <desempate...> OFFSET @offset
    /// ROWS FETCH NEXT @buscar ROWS ONLY`. Coluna e desempate vem do catalogo
    /// (nao do front); a chave ja foi validada por <see cref="ValidadorPaginacao"/>.
    /// Se a coluna escolhida ja estiver no desempate (operacao com GROUP BY cujo
    /// desempate e a tupla inteira), ela e removida de la — SQL Server recusa
    /// coluna repetida no ORDER BY (erro 169).
    /// </summary>
    private static string ClausulaPaginacao(Operacao op, PaginacaoResolvida pag)
    {
        var coluna = op.Ordenacao!.Colunas[pag.OrdenarPor];
        var direcao = pag.Direcao == "desc" ? "DESC" : "ASC";

        var termos = new List<string> { $"{coluna} {direcao}" };
        foreach (var bruto in op.Desempate)
        {
            var termo = bruto.Trim();
            // BUG #24: pular a coluna ja nomeada (e qualquer repetida). Sem isso, a
            // operacao com GROUP BY cujo desempate e a tupla inteira gera
            // `ORDER BY prod.Codigo ASC, prod.Codigo, ...` -> SQL Server erro 169
            // ("A column has been specified more than once"). Validado na piloto.
            if (termo.Length == 0
                || string.Equals(termo, coluna, StringComparison.OrdinalIgnoreCase)
                || termos.Any(t => string.Equals(t, termo, StringComparison.OrdinalIgnoreCase)))
                continue;
            termos.Add(termo);
        }

        return $" ORDER BY {string.Join(", ", termos)} "
               + "OFFSET @offset ROWS FETCH NEXT @buscar ROWS ONLY";
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
