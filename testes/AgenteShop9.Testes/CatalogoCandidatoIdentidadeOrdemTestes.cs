using System.Text.Json;
using System.Text.RegularExpressions;
using AgenteShop9.Catalogos;

namespace AgenteShop9.Testes;

/// <summary>
/// Candidato v41 da identidade por Ordem (issue #178, etapa 1): toda operacao que recebia o codigo da filial passa a
/// receber a Ordem, a conciliacao filtra pela filial do Movimento e existe a operacao filiais_cadastro.
/// Nao assina nem publica nada.
/// </summary>
public class CatalogoCandidatoIdentidadeOrdemTestes
{
    private const string Cartao = "financeiro_recebimentos_cartao_pix_em_aberto";
    private const string PorCaixaDia = "conciliacao_dinheiro_transferencia_por_caixa_dia";
    private const string Vendas = "conciliacao_dinheiro_transferencia_vendas";
    private const string Caixas = "conciliacao_caixas_funcionarios";

    private static readonly string[] Proibidos = ["INSERT ", "UPDATE ", "DELETE ", "EXEC", "INTO ", "DROP ", "ALTER ", "TRUNCATE ", "MERGE ", ";"];

    private static Catalogo Ler(string arquivo) => Catalogo.Analisar(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "fixtures", arquivo)));

    private static Catalogo Candidato => Ler("catalogo.candidato-identidade-ordem.json");

    private static string SemComentarios(string sql) => Regex.Replace(sql, @"/\*.*?\*/|--[^\n]*", "", RegexOptions.Singleline);

    [Fact]
    public void Versao_e_41_e_so_acrescenta_filiais_cadastro_ao_catalogo_vigente()
    {
        var novo = Candidato.Operacoes!;
        var atual = Ler("catalogo-real.json");
        Assert.Equal(41, Candidato.VersaoCatalogo);
        Assert.True(atual.VersaoCatalogo < 41);
        foreach (var nome in atual.Operacoes!.Keys)
            Assert.Contains(nome, novo.Keys);
        Assert.Equal(atual.Operacoes.Count + 1, novo.Count);
        Assert.Contains("filiais_cadastro", novo.Keys);
    }

    [Fact]
    public void Nenhuma_operacao_tem_parametro_nem_sql_com_CodigoFilial()
    {
        foreach (var (nome, op) in Candidato.Operacoes!)
        {
            Assert.DoesNotContain(op.Parametros, p => p.Nome.StartsWith("CodigoFilial", StringComparison.Ordinal));
            Assert.DoesNotContain("CodigoFilial", op.Sql);
            if (op.SqlContagem is not null)
                Assert.DoesNotContain("CodigoFilial", op.SqlContagem);
        }
    }

    [Fact]
    public void Todo_parametro_CodigoFilial_do_vigente_virou_OrdemFilial_com_o_mesmo_tipo_e_obrigatoriedade()
    {
        var atual = Ler("catalogo-real.json").Operacoes!;
        var novo = Candidato.Operacoes!;
        var contadas = 0;
        foreach (var (nome, op) in atual)
            foreach (var antigo in op.Parametros.Where(p => p.Nome.StartsWith("CodigoFilial", StringComparison.Ordinal)))
            {
                var nomeNovo = antigo.Nome.Replace("CodigoFilial", "OrdemFilial");
                var depois = novo[nome].Parametros.Single(p => p.Nome == nomeNovo);
                Assert.Equal("inteiro", depois.Tipo);
                Assert.Equal(antigo.Obrigatorio, depois.Obrigatorio);
                contadas++;
            }
        Assert.True(contadas >= 40);
    }

    [Fact]
    public void Operacoes_sem_filial_ficam_identicas_ao_vigente()
    {
        var atual = Ler("catalogo-real.json").Operacoes!;
        foreach (var (nome, op) in atual.Where(o => !o.Value.Parametros.Any(p => p.Nome.StartsWith("CodigoFilial", StringComparison.Ordinal))))
            Assert.Equal(JsonSerializer.Serialize(op), JsonSerializer.Serialize(Candidato.Operacoes![nome]));
    }

    [Fact]
    public void Filiais_cadastro_devolve_ordem_codigo_nome_sem_a_ordem_zero_e_sem_parametro()
    {
        var op = Candidato.Operacoes!["filiais_cadastro"];
        Assert.Empty(op.Parametros);
        var sql = SemComentarios(op.Sql);
        Assert.StartsWith("SELECT", sql.TrimStart());
        Assert.Contains("fil.Ordem", sql);
        Assert.Contains("fil.Codigo", sql);
        Assert.Contains("fil.Nome", sql);
        Assert.Contains("fil.Ordem <> 0", sql);
        Assert.Contains("fil.Ordem <> 0", op.SqlContagem!);
        foreach (var proibido in Proibidos)
            Assert.DoesNotContain(proibido, sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(op.UsoInterno);
        Assert.NotEmpty(op.Desempate!);
    }

    [Theory]
    [InlineData(Cartao)]
    [InlineData(PorCaixaDia)]
    [InlineData(Vendas)]
    [InlineData(Caixas)]
    public void Operacoes_da_conciliacao_recebem_OrdemFilial_e_declaram_so_o_que_usam(string nome)
    {
        var op = Candidato.Operacoes![nome];
        var filial = op.Parametros.Single(p => p.Nome == "OrdemFilial");
        Assert.Equal("inteiro", filial.Tipo);
        Assert.True(filial.Obrigatorio);
        var usados = Regex.Matches(SemComentarios(op.Sql), @"@[A-Za-z_]+").Select(m => m.Value[1..]).ToHashSet();
        Assert.Equal(op.Parametros.Select(p => p.Nome).ToHashSet(), usados);
        var usadosContagem = Regex.Matches(SemComentarios(op.SqlContagem!), @"@[A-Za-z_]+").Select(m => m.Value[1..]).ToHashSet();
        Assert.Equal(usados, usadosContagem);
        Assert.Contains("Ordem_Filial", op.Sql);
        Assert.Contains("AS Cod_Filial", op.Sql);
        Assert.Contains("AS Ordem_Filial", op.Sql);
    }

    [Theory]
    [InlineData(PorCaixaDia)]
    [InlineData(Vendas)]
    public void Dinheiro_e_transferencia_filtram_e_agrupam_pela_filial_do_movimento(string nome)
    {
        var op = Candidato.Operacoes![nome];
        foreach (var sql in new[] { SemComentarios(op.Sql), SemComentarios(op.SqlContagem!) })
        {
            Assert.Contains("mov.Ordem_Filial = @OrdemFilial", sql);
            Assert.DoesNotContain("fil.Codigo = @", sql);
            Assert.Contains("GROUP BY ", sql);
            Assert.Contains("mov.Ordem_Filial, fil.Codigo", sql);
        }
        Assert.Contains("mov.Ordem_Filial AS Ordem_Filial", op.Sql);
    }

    [Fact]
    public void Caixas_funcionarios_filtra_pela_filial_do_caixa()
    {
        var op = Candidato.Operacoes![Caixas];
        Assert.Contains("cx.Ordem_Filial = @OrdemFilial", op.Sql);
        Assert.Contains("cx.Ordem_Filial = @OrdemFilial", op.SqlContagem!);
        Assert.Contains("Filiais.Ordem AS Ordem_Filial", op.Sql);
    }

    [Fact]
    public void Cartao_filtra_pelo_movimento_da_venda_na_filial_e_nao_pelo_financeiro()
    {
        var op = Candidato.Operacoes![Cartao];
        foreach (var sql in new[] { SemComentarios(op.Sql), SemComentarios(op.SqlContagem!) })
        {
            // A venda precisa existir como movimento VND daquela filial (a Sequencia se repete entre filiais).
            Assert.Contains("CROSS APPLY (SELECT TOP", sql);
            Assert.Contains("movef.Ordem_Filial = @OrdemFilial", sql);
            Assert.Contains("movef.Tipo_Operacao = 'VND'", sql);
            Assert.Contains("movef.Sequencia = fin.Codigo_Sequencia", sql);
            // O Financeiro_Contas.Ordem_Filial nao filtra mais nem junta com Filiais.
            Assert.DoesNotContain("fin.Ordem_Filial", sql);
            Assert.DoesNotContain("OUTER APPLY", sql);
        }
        Assert.Contains("INNER JOIN Filiais ON Filiais.Ordem = efet.Ordem_Filial_Venda", op.Sql);
        Assert.Contains("Filiais.Ordem AS Ordem_Filial", op.Sql);
        // Colunas que o motor ja le continuam presentes.
        foreach (var coluna in new[] { "Cod_Filial", "Nome_Filial", "Ordem_Financeiro", "Cartao_Lote", "Cod_Efetivador", "Nome_Efetivador", "Cod_Administradora", "Cod_Vendedor" })
            Assert.Contains(coluna, op.Sql);
        Assert.Equal(new[] { "fin.Ordem" }, op.Desempate);
    }

    [Theory]
    [InlineData("emissao_vendas_por_forma_pagamento")]
    [InlineData("emissao_vendas_apos_marca")]
    [InlineData("emissao_venda_por_seq")]
    public void Vendas_da_emissao_devolvem_Ordem_Filial_e_mantem_Cod_Filial(string nome)
    {
        var op = Candidato.Operacoes![nome];
        Assert.Contains("M.Ordem_Filial", op.Sql);
        Assert.Contains("AS Ordem_Filial", op.Sql);
        Assert.Contains("B.Ordem_Filial,", op.Sql);
        Assert.Contains("B.Cod_Filial,", op.Sql);
        Assert.Contains("FL.Ordem = @OrdemFilial", op.Sql);
    }

    [Fact]
    public void Conversoes_de_codigo_para_ordem_dentro_do_SQL_sumiram_nas_operacoes_da_emissao()
    {
        foreach (var nome in new[] { "emissao_produtos_disponiveis", "emissao_produtos_disponiveis_v2", "emissao_produtos_sem_estoque",
                                      "emissao_produtos_sem_estoque_v2", "emissao_notas_itens_mes", "emissao_origem_das_emitidas" })
        {
            var op = Candidato.Operacoes![nome];
            Assert.DoesNotContain("SELECT Ordem FROM Filiais", op.Sql);
            Assert.Matches(@"Ordem_Filial = @OrdemFilial(Origem|Destino|Estoque)", op.Sql);
        }
    }

    [Fact]
    public void Operacoes_novas_ou_alteradas_continuam_select_puro()
    {
        foreach (var (nome, op) in Candidato.Operacoes!)
        {
            foreach (var proibido in Proibidos.Where(p => p != ";" && p != "INTO " && p != "EXEC"))
            {
                Assert.DoesNotContain(proibido, SemComentarios(op.Sql), StringComparison.OrdinalIgnoreCase);
                if (op.SqlContagem is not null)
                    Assert.DoesNotContain(proibido, SemComentarios(op.SqlContagem), StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
