using System.Text.Json;
using System.Text.RegularExpressions;
using AgenteShop9.Catalogos;

namespace AgenteShop9.Testes;

/// <summary>
/// Candidato v39 da conciliacao com diagnostico (Etapa 2): dinheiro e transferencia por caixa e dia, a relacao
/// caixa x funcionario e o Cartao_Lote com quem efetivou na operacao de recebimentos de cartao. Nao assina nem publica nada.
/// </summary>
public class CatalogoCandidatoConciliacaoDiagnosticoTestes
{
    private const string Cartao = "financeiro_recebimentos_cartao_pix_em_aberto";
    private const string PorCaixaDia = "conciliacao_dinheiro_transferencia_por_caixa_dia";
    private const string Vendas = "conciliacao_dinheiro_transferencia_vendas";
    private const string Caixas = "conciliacao_caixas_funcionarios";

    private static readonly string[] Proibidos = ["INSERT ", "UPDATE ", "DELETE ", "EXEC", "INTO ", "DROP ", "ALTER ", "TRUNCATE ", "MERGE ", ";"];

    private static Catalogo Ler(string arquivo) => Catalogo.Analisar(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "fixtures", arquivo)));

    private static Catalogo Candidato => Ler("catalogo.candidato-conciliacao-diagnostico.json");

    private static string SemComentarios(string sql) => Regex.Replace(sql, @"/\*.*?\*/|--[^\n]*", "", RegexOptions.Singleline);

    [Theory]
    [InlineData(PorCaixaDia)]
    [InlineData(Vendas)]
    [InlineData(Caixas)]
    public void As_novas_sao_select_puro_paginadas_e_com_filial_marcada(string nome)
    {
        var op = Candidato.Operacoes![nome];
        foreach (var proibido in Proibidos)
        {
            Assert.DoesNotContain(proibido, SemComentarios(op.Sql), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(proibido, SemComentarios(op.SqlContagem!), StringComparison.OrdinalIgnoreCase);
        }
        Assert.StartsWith("SELECT", SemComentarios(op.Sql).TrimStart());
        Assert.False(op.UsoInterno);
        Assert.Equal(200, op.TamanhoPaginaMax);
        Assert.Equal(10_000, op.MaxLinhas);
        Assert.Equal(30_000, op.TimeoutMs);
        Assert.NotNull(op.Ordenacao);
        Assert.NotEmpty(op.Desempate!);
        // O parametro de filial (hoje OrdemFilial) e garantido em CatalogoCandidatoIdentidadeOrdemTestes.
        Assert.Contains(op.Parametros, p => p.Nome.EndsWith("Filial", StringComparison.Ordinal) && p.Tipo == "inteiro" && p.Obrigatorio);
        // Todo parametro usado no SQL esta declarado e vice-versa (a contagem usa os mesmos).
        var usados = Regex.Matches(SemComentarios(op.Sql), @"@[A-Za-z_]+").Select(m => m.Value[1..]).ToHashSet();
        Assert.Equal(op.Parametros.Select(p => p.Nome).ToHashSet(), usados);
        var usadosContagem = Regex.Matches(SemComentarios(op.SqlContagem!), @"@[A-Za-z_]+").Select(m => m.Value[1..]).ToHashSet();
        Assert.Equal(usados, usadosContagem);
    }

    [Theory]
    [InlineData(PorCaixaDia)]
    [InlineData(Vendas)]
    public void Dinheiro_e_liquido_de_troco_e_transferencia_vem_da_tabela_de_formas(string nome)
    {
        var op = Candidato.Operacoes![nome];
        var sql = SemComentarios(op.Sql);
        Assert.Contains("forma.Dinheiro_Valor, 0) - ISNULL(forma.Valor_Troco, 0)", sql);
        Assert.Contains("forma.Conta_Bancaria_Valor", sql);
        Assert.Contains("mov.Ordem_Funcionario_Efetivacao_Financeiro", sql);
        Assert.Contains("FROM Financeiro_Formas_Recebimento_Pagamento_V2".Replace("FROM ", ""), sql);
        Assert.Contains("mov.Tipo_Operacao = 'VND'", sql);
        Assert.Contains("mov.Efetivado_Estoque = 1", sql);
        Assert.Contains("mov.Desefetivado_Estoque = 0", sql);
        Assert.Contains("ISNULL(forma.Invalido, 0) = 0", sql);
        Assert.Equal(92, op.PeriodoMaxDias);
        Assert.Contains(op.Parametros, p => p.Nome == "inicio" && p.Tipo == "data" && p.Obrigatorio);
        Assert.Contains(op.Parametros, p => p.Nome == "fim" && p.Tipo == "data" && p.Obrigatorio);
        Assert.StartsWith("SELECT COUNT(*) FROM (", op.SqlContagem);
    }

    [Fact]
    public void Por_caixa_e_dia_agrupa_e_o_desempate_e_a_tupla_do_group_by()
    {
        var op = Candidato.Operacoes![PorCaixaDia];
        var grupo = Regex.Match(SemComentarios(op.Sql), @"GROUP BY (.+)$", RegexOptions.Singleline).Groups[1].Value.Trim();
        Assert.Equal(string.Join(", ", op.Desempate!), grupo);
        foreach (var coluna in new[] { "Cod_Filial", "Dia", "Cod_Efetivador", "Nome_Efetivador", "Dinheiro_Liquido", "Transferencia_Valor", "Qtd_Vendas" })
            Assert.Contains(coluna, op.Sql);
    }

    [Fact]
    public void Vendas_traz_uma_linha_por_venda_com_o_efetivador()
    {
        var op = Candidato.Operacoes![Vendas];
        foreach (var coluna in new[] { "Ordem_Movimento", "Seq", "Cod_Filial", "Data_Venda", "Cod_Efetivador", "Dinheiro_Liquido", "Transferencia_Valor" })
            Assert.Contains(coluna, op.Sql);
        Assert.Contains("mov.Ordem", op.Desempate!);
    }

    [Fact]
    public void Caixas_funcionarios_usa_o_sql_do_dono_com_aliases_distintos()
    {
        var op = Candidato.Operacoes![Caixas];
        var sql = SemComentarios(op.Sql);
        Assert.Contains("FROM Funcionarios_Caixas_Padrao AS funCx", sql);
        Assert.Contains("INNER JOIN Funcionarios AS fun ON fun.Ordem = funCx.Ordem_Funcionario", sql);
        Assert.Contains("INNER JOIN Caixas AS cx ON cx.Ordem = funCx.Ordem_Caixa", sql);
        Assert.Contains("INNER JOIN Filiais ON Filiais.Ordem = cx.Ordem_Filial", sql);
        foreach (var alias in new[] { "Cod_Caixa", "Nome_Caixa", "Cod_Filial", "Nome_Filial", "Cod_Funcionario", "Nome_Funcionario" })
            Assert.Contains("AS " + alias, sql);
        Assert.Null(op.PeriodoMaxDias);
    }
}
