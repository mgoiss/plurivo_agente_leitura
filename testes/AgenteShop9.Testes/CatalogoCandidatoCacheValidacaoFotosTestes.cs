using System.Text.Json;
using AgenteShop9.Catalogos;
using AgenteShop9.Operacoes;

namespace AgenteShop9.Testes;

/// <summary>
/// Candidato v44 da issue #182 (cache de validacao de fotos): `catalogo_indice_fotos_metadados` (sem o
/// conteudo) e `catalogo_foto_base64` (uma foto por Ordem). Nao assina nem publica nada.
/// </summary>
public class CatalogoCandidatoCacheValidacaoFotosTestes
{
    private const string Metadados = "catalogo_indice_fotos_metadados";
    private const string FotoBase64 = "catalogo_foto_base64";
    private const string IndiceAntigo = "catalogo_indice_fotos";

    private static Catalogo Ler(string arquivo) => Catalogo.Analisar(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "fixtures", arquivo)));

    private static Catalogo V44 => Ler("catalogo.candidato-cache-validacao-fotos-v44.json");
    // Base congelada: o v43 vigente antes do v44 (o vigente de hoje ainda e ele).
    private static Catalogo V43 => Ler("catalogo.base-v43.json");

    [Fact]
    public void Versao_e_44_sobre_a_base_v43()
    {
        Assert.Equal(44, V44.VersaoCatalogo);
        Assert.Equal(43, V43.VersaoCatalogo);
    }

    [Fact]
    public void So_as_duas_operacoes_novas_a_mais_e_nenhuma_outra_muda()
    {
        var novas = new HashSet<string> { Metadados, FotoBase64 };
        Assert.Equal(V43.Operacoes!.Count + 2, V44.Operacoes!.Count);
        Assert.Equal(novas, V44.Operacoes.Keys.Except(V43.Operacoes.Keys).ToHashSet());
        foreach (var (nome, op) in V43.Operacoes)
            Assert.Equal(JsonSerializer.Serialize(op), JsonSerializer.Serialize(V44.Operacoes[nome]));
    }

    [Fact]
    public void Operacoes_novas_sao_de_uso_interno_e_nao_usam_indices_nem_paginacao()
    {
        foreach (var nome in new[] { Metadados, FotoBase64 })
        {
            var op = V44.Operacoes![nome];
            Assert.True(op.UsoInterno);
            Assert.False(op.UsaVendasIndice);
            Assert.False(op.UsaFotosValidadas);
            Assert.Null(op.Ordenacao);
        }
    }

    [Fact]
    public void Metadados_tem_o_mesmo_escopo_do_indice_antigo_mas_sem_ler_o_conteudo_da_foto()
    {
        var antigo = V44.Operacoes![IndiceAntigo];
        var metadados = V44.Operacoes[Metadados];

        Assert.DoesNotContain("Foto_Base64", metadados.Sql);
        Assert.DoesNotContain("foto.Foto AS", metadados.Sql);
        Assert.Contains("DATALENGTH(foto.Foto) AS Foto_Caracteres", metadados.Sql);
        Assert.Contains("foto.Ordem AS Imagem_Ordem", metadados.Sql);
        Assert.Contains("prod.Ordem AS Produto_Ordem", metadados.Sql);
        // Mesmo FROM/WHERE/ORDER BY: so a lista de colunas muda.
        Assert.Equal(DepoisDoSelect(antigo.Sql), DepoisDoSelect(metadados.Sql));
        Assert.Equal(JsonSerializer.Serialize(antigo.Parametros), JsonSerializer.Serialize(metadados.Parametros));
        Assert.Equal(antigo.MaxLinhas, metadados.MaxLinhas);
        Assert.Equal(antigo.TimeoutMs, metadados.TimeoutMs);
        Assert.Contains("ORDER BY prod.Ordem, foto.Principal DESC, foto.Posicao ASC, foto.Ordem ASC", metadados.Sql);
    }

    private static string DepoisDoSelect(string sql) => sql[sql.IndexOf("\nFROM ", StringComparison.Ordinal)..];

    [Fact]
    public void Foto_por_ordem_e_parametrizada_sem_concatenacao_e_limita_o_tamanho()
    {
        var op = V44.Operacoes![FotoBase64];
        var p = Assert.Single(op.Parametros);
        Assert.Equal("OrdemFoto", p.Nome);
        Assert.Equal("inteiro", p.Tipo);
        Assert.True(p.Obrigatorio);
        Assert.Equal(
            "SELECT foto.Foto AS Foto_Base64\nFROM Prod_Serv_Fotos AS foto\n" +
            "WHERE foto.Ordem = @OrdemFoto AND DATALENGTH(foto.Foto) BETWEEN 1 AND 1000000", op.Sql);
        Assert.Equal(1, op.MaxLinhas);
        Assert.True(op.TimeoutMs <= V44.Operacoes[IndiceAntigo].TimeoutMs);
        foreach (var op2 in new[] { op, V44.Operacoes[Metadados] })
            foreach (var proibido in new[] { "INSERT ", "UPDATE ", "DELETE ", "EXEC", "INTO ", "DROP ", "CREATE ", "INDEX", ";", "+", "'' +" })
                Assert.DoesNotContain(proibido, op2.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CA15_comando_da_foto_leva_a_ordem_como_valor_de_parametro()
    {
        var op = V44.Operacoes![FotoBase64];
        var pedido = new Dictionary<string, JsonElement> { ["OrdemFoto"] = JsonSerializer.SerializeToElement(1234) };
        using var comando = MontadorComando.Montar(op, pedido);
        Assert.DoesNotContain("1234", comando.CommandText);
        Assert.Equal(1234L, Convert.ToInt64(comando.Parameters["@OrdemFoto"].Value));
        Assert.Equal(5, comando.CommandTimeout);
    }

    [Fact]
    public void Parametros_validam_como_o_executor_exige()
    {
        var foto = V44.Operacoes![FotoBase64];
        Assert.Null(ValidadorParametros.Validar(foto,
            new Dictionary<string, JsonElement> { ["OrdemFoto"] = JsonSerializer.SerializeToElement(1) }));
        Assert.NotNull(ValidadorParametros.Validar(foto, new Dictionary<string, JsonElement>()));

        var escopo = new Dictionary<string, JsonElement>
        {
            ["OrdemFilial"] = JsonSerializer.SerializeToElement(2),
            ["Tabela_01"] = JsonSerializer.SerializeToElement(7),
        };
        Assert.Null(ValidadorParametros.Validar(V44.Operacoes[Metadados], escopo));
    }

    [Fact]
    public void Catalogo_v43_nao_tem_as_operacoes_novas_e_segue_no_caminho_antigo()
    {
        Assert.False(V43.Operacoes!.ContainsKey(Metadados));
        Assert.False(V43.Operacoes.ContainsKey(FotoBase64));
        Assert.True(V43.Operacoes[IndiceAntigo].UsoInterno);
        Assert.Contains("foto.Foto AS Foto_Base64", V43.Operacoes[IndiceAntigo].Sql);
    }

    private static Dictionary<string, JsonElement> Escopo => new()
    {
        ["OrdemFilial"] = JsonSerializer.SerializeToElement(2),
        ["Tabela_01"] = JsonSerializer.SerializeToElement(7),
    };

    [Fact]
    public void Executor_usa_o_caminho_novo_so_no_v44_e_o_antigo_no_v43()
    {
        Assert.True(ExecutorOperacao.TentarObterOperacoesPorMetadados(V44, Escopo, out var meta, out var foto));
        Assert.Equal(V44.Operacoes![Metadados].Sql, meta.Sql);
        Assert.Equal(V44.Operacoes[FotoBase64].Sql, foto.Sql);
        Assert.False(ExecutorOperacao.TentarObterOperacoesPorMetadados(V43, Escopo, out _, out _));
    }

    [Fact]
    public void Executor_recusa_o_caminho_novo_se_uma_operacao_nao_for_interna_ou_faltar()
    {
        var ops = new Dictionary<string, Operacao>(V44.Operacoes!);
        ops[FotoBase64] = ops[FotoBase64] with { UsoInterno = false };
        Assert.False(ExecutorOperacao.TentarObterOperacoesPorMetadados(
            V44 with { Operacoes = ops }, Escopo, out _, out _));

        ops = new Dictionary<string, Operacao>(V44.Operacoes!);
        ops.Remove(Metadados);
        Assert.False(ExecutorOperacao.TentarObterOperacoesPorMetadados(
            V44 with { Operacoes = ops }, Escopo, out _, out _));
    }
}
