using System.Text.Json;
using AgenteShop9.Catalogos;
using AgenteShop9.Operacoes;

namespace AgenteShop9.Testes;

/// <summary>
/// Candidato v43 da issue #179 (indice de vendas no agente): `catalogo_vendas_indice` e `#VendasIndice` no lugar do
/// trecho Vendas. Nao assina nem publica nada.
/// </summary>
public class CatalogoCandidatoIndiceVendasAgenteTestes
{
    private const string Arquivo = "catalogo.candidato-vendas-indice-agente-v43.json";
    private const string Indice = "catalogo_vendas_indice";
    private static readonly string[] ComIndice = ["catalogo_produtos_ids", "catalogo_inicio"];

    private static Catalogo Ler(string arquivo) => Catalogo.Analisar(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "fixtures", arquivo)));

    private static Catalogo V42 => Ler(Arquivo);
    private static Catalogo V41 => Ler("catalogo-real.json");

    [Fact]
    public void Versao_e_43_sobre_o_vigente_v42()
    {
        Assert.Equal(43, V42.VersaoCatalogo);
        Assert.Equal(42, V41.VersaoCatalogo);
    }

    [Fact]
    public void CA2_consultas_publicas_de_ranking_nao_citam_Movimento_nem_Movimento_Prod_Serv()
    {
        foreach (var nome in ComIndice)
        {
            var op = V42.Operacoes![nome];
            foreach (var sql in new[] { op.Sql, op.SqlContagem ?? "" })
            {
                Assert.DoesNotContain("Movimento", sql, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Movimento_Prod_Serv", sql, StringComparison.OrdinalIgnoreCase);
            }
            Assert.True(op.UsaVendasIndice);
            Assert.Contains("#VendasIndice AS venda", op.Sql);
        }
    }

    [Fact]
    public void Relacionados_segue_como_no_v41_porque_e_coocorrencia_e_nao_soma_por_produto()
    {
        // Decidido na #179 (CA2 da especificacao): relacionados fica FORA do indice, pois calcula
        // coocorrencia de produtos e o indice (Produto_Ordem, Quantidade) nao responde "vendas em comum".
        // Este teste trava o estado para qualquer mudanca ser consciente.
        var op = V42.Operacoes!["catalogo_relacionados"];
        Assert.False(op.UsaVendasIndice);
        Assert.Equal(JsonSerializer.Serialize(V41.Operacoes!["catalogo_relacionados"]), JsonSerializer.Serialize(op));
    }

    [Fact]
    public void Operacao_do_indice_e_interna_com_SQL_fixo_na_janela_de_59_dias_e_dica_de_indice()
    {
        var op = V42.Operacoes![Indice];
        Assert.True(op.UsoInterno);
        Assert.False(op.UsaVendasIndice);
        Assert.Null(op.Ordenacao);
        var p = Assert.Single(op.Parametros);
        Assert.Equal("OrdemFilial", p.Nome);
        Assert.True(p.Obrigatorio);
        Assert.Contains("FROM Movimento AS mov WITH (INDEX (CI_Filial_Estoque_CliFor))", op.Sql);
        Assert.Contains("mov.Tipo_Operacao = 'VND'", op.Sql);
        Assert.Contains("mov.Efetivado_Estoque = 1", op.Sql);
        Assert.Contains("mov.Desefetivado_Estoque = 0 AND item.Linha_Excluida = 0", op.Sql);
        Assert.Contains("mov.Data_Efetivado_Estoque >= DATEADD(DAY, -59, CAST(GETDATE() AS date))", op.Sql);
        Assert.Contains("mov.Data_Efetivado_Estoque <= GETDATE()", op.Sql);
        Assert.DoesNotContain("@Inicio", op.Sql);
        foreach (var proibido in new[] { "INSERT ", "UPDATE ", "DELETE ", "EXEC", "INTO ", "DROP ", "CREATE ", ";" })
            Assert.DoesNotContain(proibido, op.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CA10_indice_tem_timeout_de_30000_e_consulta_publica_continua_com_2000()
    {
        var catalogo = V42.Operacoes!;
        Assert.Equal(30000, catalogo[Indice].TimeoutMs);
        foreach (var nome in new[] { "catalogo_produtos_ids", "catalogo_produtos_ids_sem_vendas", "catalogo_inicio" })
            Assert.Equal(2000, catalogo[nome].TimeoutMs);

        var pedido = new Dictionary<string, JsonElement>
        {
            ["OrdemFilial"] = JsonSerializer.SerializeToElement(2),
        };
        using var comandoIndice = MontadorComando.Montar(catalogo[Indice], pedido);
        Assert.Equal(30, comandoIndice.CommandTimeout);
        using var comandoPublico = MontadorComando.Montar(catalogo["catalogo_inicio"], pedido);
        Assert.Equal(2, comandoPublico.CommandTimeout);
    }

    [Fact]
    public void Ordenacao_por_nome_e_preco_nao_exige_indice_porque_usa_a_operacao_sem_vendas()
    {
        var op = V42.Operacoes!["catalogo_produtos_ids_sem_vendas"];
        Assert.False(op.UsaVendasIndice);
        Assert.DoesNotContain("#VendasIndice", op.Sql);
        Assert.DoesNotContain("Movimento", op.Sql);
    }

    [Fact]
    public void Colunas_ordenacao_e_parametros_das_duas_consultas_seguem_iguais_ao_v41()
    {
        foreach (var nome in ComIndice)
        {
            var antes = V41.Operacoes![nome];
            var depois = V42.Operacoes![nome];
            Assert.Equal(antes.SqlContagem, depois.SqlContagem);
            Assert.Equal(
                JsonSerializer.Serialize(antes with { Sql = "" }),
                JsonSerializer.Serialize(depois with { Sql = "", UsaVendasIndice = false, VendasIndiceOpcional = false }));
            Assert.Contains(depois.Parametros, p => p.Nome == "Inicio");
        }
        Assert.Contains("COALESCE(venda.Quantidade, 0) AS Quantidade_Vendida",
            V42.Operacoes!["catalogo_produtos_ids"].Sql);
    }

    [Fact]
    public void Demais_operacoes_ficam_identicas_ao_v41_exceto_o_cartao()
    {
        // O cartao muda por acumulo do candidato da #178 (liga a venda pela chave); ver CatalogoCandidatoCartaoPorChaveTestes.
        var mudadas = new HashSet<string>(ComIndice) { Indice, "financeiro_recebimentos_cartao_pix_em_aberto" };
        foreach (var (nome, op) in V41.Operacoes!)
            if (!mudadas.Contains(nome))
                Assert.Equal(JsonSerializer.Serialize(op), JsonSerializer.Serialize(V42.Operacoes![nome]));
        Assert.Equal(V41.Operacoes.Count + 1, V42.Operacoes!.Count);
    }

    [Fact]
    public void Acumula_a_correcao_do_cartao_por_chave_da_178()
    {
        var op = V42.Operacoes!["financeiro_recebimentos_cartao_pix_em_aberto"];
        foreach (var sql in new[] { op.Sql, op.SqlContagem })
        {
            Assert.Contains("movef.Ordem_Financeiro = fin.Ordem_Pai", sql);
            Assert.DoesNotContain("movef.Sequencia", sql);
        }
    }

    [Fact]
    public void Inicio_tem_o_indice_opcional_e_produtos_ids_nao()
    {
        Assert.True(V42.Operacoes!["catalogo_inicio"].UsaVendasIndice);
        Assert.True(V42.Operacoes["catalogo_inicio"].VendasIndiceOpcional);
        Assert.True(V42.Operacoes["catalogo_produtos_ids"].UsaVendasIndice);
        Assert.False(V42.Operacoes["catalogo_produtos_ids"].VendasIndiceOpcional);
        Assert.False(V42.Operacoes["catalogo_produtos_ids_sem_vendas"].UsaVendasIndice);
    }

    [Fact]
    public void Opcional_sem_usar_o_indice_e_catalogo_invalido()
    {
        var json = "{\"versao_catalogo\":1,\"operacoes\":{\"x\":{\"sql\":\"SELECT 1\",\"vendas_indice_opcional\":true}}}";
        Assert.Throws<CatalogoInvalidoException>(() => Catalogo.Analisar(System.Text.Encoding.UTF8.GetBytes(json)));
    }
}
