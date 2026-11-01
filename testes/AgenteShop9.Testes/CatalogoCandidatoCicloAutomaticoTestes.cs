using System.Text.Json;
using AgenteShop9.Catalogos;

namespace AgenteShop9.Testes;

/// <summary>
/// Candidato v38 das issues #129 e #130: tres operacoes de leitura para o ciclo automatico da emissao
/// (vendas depois da marca d'agua, venda pelo SEQ e origem das NFC-e emitidas). Nao assina nem publica nada.
/// </summary>
public class CatalogoCandidatoCicloAutomaticoTestes
{
    private const string Base = "emissao_vendas_por_forma_pagamento";
    private const string AposMarca = "emissao_vendas_apos_marca";
    private const string PorSeq = "emissao_venda_por_seq";
    private const string OrigemDasEmitidas = "emissao_origem_das_emitidas";
    private const string JanelaInicio = "      AND M.Data_Efetivado_Estoque >= @inicio\n";
    private const string JanelaFim = "      AND M.Data_Efetivado_Estoque < DATEADD(DAY, 1, @fim)\n";
    private const string FiltroMarca = "      AND M.Data_Efetivado_Estoque > CONVERT(DATETIME, @marca, 126)\n";
    private const string FiltroSeq = "      AND M.Sequencia = @Seq\n";

    private static readonly string[] Proibidos = ["INSERT ", "UPDATE ", "DELETE ", "EXEC", "INTO ", "DROP ", "ALTER ", "TRUNCATE ", "MERGE ", ";"];

    private static Catalogo Ler(string arquivo) => Catalogo.Analisar(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "fixtures", arquivo)));

    private static Catalogo Candidato => Ler("catalogo.candidato-ciclo-automatico.json");

    // Tira o comentario de abertura (que muda de proposito) para comparar so a consulta.
    private static string SemCabecalho(string sql) => sql[(sql.IndexOf("*/", StringComparison.Ordinal) + 2)..];

    // Comentario de SQL pode ter ";" e palavras soltas; so o codigo executavel importa.
    private static string SemComentarios(string sql) =>
        System.Text.RegularExpressions.Regex.Replace(sql, @"/\*.*?\*/|--[^\n]*", "", System.Text.RegularExpressions.RegexOptions.Singleline);

    [Fact]
    public void Versao_e_38_e_o_resto_fica_identico_ao_v37()
    {
        var novas = Candidato.Operacoes!;
        var anterior = Ler("catalogo.candidato-vendas-indice.json");
        Assert.Equal(38, Candidato.VersaoCatalogo);
        Assert.Equal(37, anterior.VersaoCatalogo);
        foreach (var (nome, op) in anterior.Operacoes!)
        {
            Assert.Contains(nome, novas.Keys);
            Assert.Equal(JsonSerializer.Serialize(op), JsonSerializer.Serialize(novas[nome]));
        }
        Assert.Equal(anterior.Operacoes!.Count + 3, novas.Count);
        Assert.Contains(AposMarca, novas.Keys);
        Assert.Contains(PorSeq, novas.Keys);
        Assert.Contains(OrigemDasEmitidas, novas.Keys);
    }

    [Theory]
    [InlineData(AposMarca)]
    [InlineData(PorSeq)]
    public void As_duas_operacoes_sao_select_puro_e_paginadas_como_a_base(string nome)
    {
        var op = Candidato.Operacoes![nome];
        var baseOp = Candidato.Operacoes![Base];
        Assert.StartsWith("/*", op.Sql);
        Assert.Contains("WITH Base AS", op.Sql);
        foreach (var proibido in Proibidos)
        {
            Assert.DoesNotContain(proibido, SemComentarios(op.Sql), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(proibido, SemComentarios(op.SqlContagem!), StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(100, op.TamanhoPaginaPadrao);
        Assert.Equal(200, op.TamanhoPaginaMax);
        Assert.Equal(10_000, op.MaxLinhas);
        Assert.Equal(30_000, op.TimeoutMs);
        Assert.False(op.UsoInterno);
        Assert.Equal(JsonSerializer.Serialize(baseOp.Ordenacao), JsonSerializer.Serialize(op.Ordenacao));
        Assert.Equal(baseOp.Desempate, op.Desempate);
        Assert.StartsWith("/*", op.SqlContagem);
        Assert.EndsWith("SELECT COUNT(*) FROM Base B INNER JOIN Formas FM ON FM.Ordem_Forma = B.Ordem_Forma", op.SqlContagem);
    }

    [Fact]
    public void Vendas_apos_a_marca_so_acrescentam_o_filtro_de_hora_a_consulta_base()
    {
        var baseOp = Candidato.Operacoes![Base];
        var op = Candidato.Operacoes![AposMarca];
        Assert.Contains(FiltroMarca, op.Sql);
        Assert.Contains(FiltroMarca, op.SqlContagem!);
        // O filtro e sobre a mesma coluna que sai como Data_Venda.
        Assert.Contains("B.Data_Efetivado_Estoque     AS Data_Venda", op.Sql);
        Assert.Equal(SemCabecalho(baseOp.Sql).Replace(JanelaFim, JanelaFim + FiltroMarca), SemCabecalho(op.Sql));
        Assert.Equal(SemCabecalho(baseOp.SqlContagem!).Replace(JanelaFim, JanelaFim + FiltroMarca), SemCabecalho(op.SqlContagem!));
        Assert.Equal(baseOp.PeriodoMaxDias, op.PeriodoMaxDias);

        Assert.Collection(op.Parametros,
            p => { Assert.Equal("CodigoFilial", p.Nome); Assert.Equal("inteiro", p.Tipo); Assert.True(p.Obrigatorio); },
            p => { Assert.Equal("inicio", p.Nome); Assert.Equal("data", p.Tipo); Assert.True(p.Obrigatorio); },
            p => { Assert.Equal("fim", p.Nome); Assert.Equal("data", p.Tipo); Assert.True(p.Obrigatorio); },
            p => { Assert.Equal("marca", p.Nome); Assert.Equal("texto", p.Tipo); Assert.True(p.Obrigatorio); });
    }

    [Fact]
    public void Venda_pelo_seq_troca_a_janela_de_datas_pelo_seq_e_nao_tem_periodo_maximo()
    {
        var baseOp = Candidato.Operacoes![Base];
        var op = Candidato.Operacoes![PorSeq];
        Assert.Equal(SemCabecalho(baseOp.Sql).Replace(JanelaInicio + JanelaFim, FiltroSeq), SemCabecalho(op.Sql));
        Assert.Equal(SemCabecalho(baseOp.SqlContagem!).Replace(JanelaInicio + JanelaFim, FiltroSeq), SemCabecalho(op.SqlContagem!));
        Assert.DoesNotContain("@inicio", SemCabecalho(op.Sql));
        Assert.DoesNotContain("@fim", SemCabecalho(op.Sql));
        Assert.Null(op.PeriodoMaxDias);

        Assert.Collection(op.Parametros,
            p => { Assert.Equal("CodigoFilial", p.Nome); Assert.Equal("inteiro", p.Tipo); Assert.True(p.Obrigatorio); },
            p => { Assert.Equal("Seq", p.Nome); Assert.Equal("inteiro", p.Tipo); Assert.True(p.Obrigatorio); });
    }

    [Fact]
    public void Origem_das_emitidas_e_select_puro_paginado_e_limitado_a_31_dias()
    {
        var op = Candidato.Operacoes![OrigemDasEmitidas];
        Assert.StartsWith("/*", op.Sql);
        foreach (var proibido in Proibidos)
        {
            Assert.DoesNotContain(proibido, SemComentarios(op.Sql), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(proibido, SemComentarios(op.SqlContagem!), StringComparison.OrdinalIgnoreCase);
        }
        Assert.False(op.UsoInterno);
        Assert.Equal(31, op.PeriodoMaxDias);
        Assert.Equal(200, op.TamanhoPaginaMax);
        Assert.Equal(10_000, op.MaxLinhas);
        Assert.Equal(30_000, op.TimeoutMs);
        Assert.NotNull(op.Ordenacao);
        Assert.Contains("fiscal.Chave_Acesso", op.Desempate);
        // A contagem e a mesma consulta embrulhada, sem divergir da pagina.
        Assert.Equal("SELECT COUNT(*) FROM (\n" + op.Sql + "\n) AS consulta_contagem", op.SqlContagem);

        Assert.Collection(op.Parametros,
            p => { Assert.Equal("CodigoFilialDestino", p.Nome); Assert.Equal("inteiro", p.Tipo); Assert.True(p.Obrigatorio); },
            p => { Assert.Equal("CodigoFilialOrigem", p.Nome); Assert.Equal("inteiro", p.Tipo); Assert.True(p.Obrigatorio); },
            p => { Assert.Equal("inicio", p.Nome); Assert.Equal("data", p.Tipo); Assert.True(p.Obrigatorio); },
            p => { Assert.Equal("fim", p.Nome); Assert.Equal("data", p.Tipo); Assert.True(p.Obrigatorio); });
    }

    [Fact]
    public void Origem_das_emitidas_so_le_nota_modelo_65_autorizada_com_origem_e_devolve_o_que_o_backend_usa()
    {
        var sql = SemComentarios(Candidato.Operacoes![OrigemDasEmitidas].Sql);
        Assert.Contains("SUBSTRING(Chave_Acesso, 21, 2) = '65'", sql);
        Assert.Contains("Codigo_Status = 100", sql);
        Assert.Contains("ISNULL(mov.Referencia_Interna, '') <> ''", sql);
        Assert.Contains("fiscal.Data_Emissao >= @inicio AND fiscal.Data_Emissao < DATEADD(DAY, 1, @fim)", sql);
        // A nota cancelada TEM de voltar (a reconciliacao lenta estorna a cota por ela).
        var having = sql[sql.IndexOf("HAVING", StringComparison.Ordinal)..sql.IndexOf(") AS fiscal", StringComparison.Ordinal)];
        Assert.DoesNotContain("Documento_Cancelado", having);
        foreach (var coluna in new[] { "Nota_Chave", "Seq_Destino", "Referencia_Interna", "Data_Emissao", "Cancelada", "Valor_Nota", "Seq_Origem", "Origem_Efetivada" })
            Assert.Contains(coluna, sql);
        // A origem e a venda da filial de ORIGEM, nao a da filial de destino.
        Assert.Contains("Codigo = @CodigoFilialOrigem", sql);
        Assert.Contains("Codigo = @CodigoFilialDestino", sql);
    }

    [Theory]
    [InlineData(AposMarca)]
    [InlineData(PorSeq)]
    [InlineData(OrigemDasEmitidas)]
    public void Todo_parametro_usado_no_sql_esta_declarado_e_vice_versa(string nome)
    {
        var op = Candidato.Operacoes![nome];
        var usados = System.Text.RegularExpressions.Regex.Matches(SemCabecalho(op.Sql), @"@[A-Za-z_]+")
            .Select(m => m.Value[1..]).ToHashSet();
        Assert.Equal(op.Parametros.Select(p => p.Nome).ToHashSet(), usados);
    }
}
