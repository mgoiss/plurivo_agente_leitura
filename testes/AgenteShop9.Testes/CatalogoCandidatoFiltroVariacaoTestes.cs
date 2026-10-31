using System.Text.Json;
using System.Text.RegularExpressions;
using AgenteShop9.Catalogos;
using AgenteShop9.Operacoes;

namespace AgenteShop9.Testes;

/// <summary>
/// Candidato da issue #117 (filtros da vitrine): cor/tamanho nas listagens de
/// produtos, na disponibilidade dos cartoes e opcoes do recorte. Nao assina nem
/// publica nada: so confere o arquivo candidato, sem SQL Server.
/// </summary>
public class CatalogoCandidatoFiltroVariacaoTestes
{
    private const string Opcoes = "catalogo_opcoes_variacao";
    private static readonly string[] Listagens = ["catalogo_produtos_ids", "catalogo_produtos_ids_sem_vendas"];
    private static readonly string[] Alteradas = [.. Listagens, "catalogo_estoque_produtos"];

    private static Catalogo Ler(string arquivo) => Catalogo.Analisar(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "fixtures", arquivo)));

    private static Catalogo Candidato() => Ler("catalogo.candidato-filtro-variacao.json");
    private static Catalogo Anterior() => Ler("catalogo.candidato-aceleracao-imagens.json");

    private static JsonElement Inteiro(long valor) => JsonSerializer.SerializeToElement(valor);

    [Fact]
    public void Versao_e_a_maior_dos_catalogos_do_repositorio()
    {
        var versao = Candidato().VersaoCatalogo;
        Assert.Equal(35, versao);
        // A v34 deste candidato ja foi assinada e publicada (catalogo.json); a v35 a sucede.
        Assert.Equal(Catalogo.Analisar(Fixtures.CatalogoRealBytes).VersaoCatalogo + 1, versao);
        Assert.True(versao > Anterior().VersaoCatalogo);
        Assert.True(versao > Ler("catalogo.candidato-publico.json").VersaoCatalogo);
        Assert.True(versao > Ler("catalogo.candidato-configuracao.json").VersaoCatalogo);
    }

    [Fact]
    public void Acumula_o_candidato_anterior_e_so_altera_as_tres_operacoes_previstas()
    {
        var antigas = Anterior().Operacoes!;
        var novas = Candidato().Operacoes!;
        foreach (var (nome, op) in antigas)
        {
            Assert.Contains(nome, novas.Keys);
            if (Alteradas.Contains(nome))
                continue;
            Assert.Equal(JsonSerializer.Serialize(op), JsonSerializer.Serialize(novas[nome]));
        }
        Assert.Equal(antigas.Count + 1, novas.Count);
        Assert.Contains(Opcoes, novas.Keys);
    }

    [Fact]
    public void Nenhuma_operacao_do_candidato_usa_openjson()
    {
        foreach (var (nome, op) in Candidato().Operacoes!)
        {
            Assert.DoesNotContain("OPENJSON", op.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("OPENJSON", op.SqlContagem ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Parametros_de_variacao_sao_oito_inteiros_opcionais_nas_operacoes_alteradas()
    {
        var esperados = Enumerable.Range(1, 4).Select(i => $"CorOrdem_{i:00}")
            .Concat(Enumerable.Range(1, 4).Select(i => $"TamanhoOrdem_{i:00}")).ToList();
        var antigas = Anterior().Operacoes!;
        foreach (var nome in Alteradas)
        {
            var parametros = Candidato().Operacoes![nome].Parametros;
            Assert.Equal(parametros.Count, parametros.Select(p => p.Nome).Distinct().Count());
            Assert.Equal(antigas[nome].Parametros.Count + 8, parametros.Count);
            foreach (var esperado in esperados)
            {
                var parametro = Assert.Single(parametros, p => p.Nome == esperado);
                Assert.Equal("inteiro", parametro.Tipo);
                Assert.False(parametro.Obrigatorio);
            }
        }
    }

    [Fact]
    public void Todo_parametro_declarado_e_usado_nas_operacoes_novas_e_alteradas()
    {
        foreach (var nome in Alteradas.Append(Opcoes))
        {
            var op = Candidato().Operacoes![nome];
            var sql = op.Sql + "\n" + op.SqlContagem;
            var usados = Regex.Matches(sql, @"@([A-Za-z_][A-Za-z0-9_]*)")
                .Select(m => m.Groups[1].Value).ToHashSet();
            usados.Remove("offset");
            usados.Remove("buscar");
            usados.Remove("max_linhas");
            Assert.Equal(op.Parametros.Select(p => p.Nome).ToHashSet(), usados);
            // Parametro so entra como valor: nunca TOP, ORDER BY, tabela ou coluna.
            Assert.DoesNotMatch(@"\bTOP\s*\(?\s*@", sql);
            Assert.DoesNotMatch(@"ORDER BY[^\n]*@", sql);
            Assert.DoesNotMatch(@"(FROM|JOIN|\.)\s*@", sql);
        }
    }

    [Fact]
    public void Listagens_filtram_por_variante_na_mesma_linha_de_grade_antes_da_paginacao()
    {
        foreach (var nome in Listagens)
        {
            var op = Candidato().Operacoes![nome];
            foreach (var sql in new[] { op.Sql, op.SqlContagem! })
            {
                // Um unico EXISTS sobre a grade, com cor e tamanho no mesmo alias.
                Assert.Equal(1, Regex.Matches(sql, @"Prod_Serv_Grade AS variante").Count);
                Assert.Contains("variante.Inativo = 0 AND variante.Bloqueado_Venda = 0", sql);
                Assert.Contains("variante.Ordem_Cor IN (@CorOrdem_01, @CorOrdem_02, @CorOrdem_03, @CorOrdem_04)", sql);
                Assert.Contains("variante.Ordem_Tamanho IN (@TamanhoOrdem_01, @TamanhoOrdem_02, @TamanhoOrdem_03, @TamanhoOrdem_04)", sql);
                Assert.Contains("prod.Tipo = 'G' AND EXISTS", sql);
                // Elegibilidade e ocultacoes continuam na CTE unica e antes do filtro.
                var iElegiveis = sql.IndexOf("Elegiveis AS (", StringComparison.Ordinal);
                var iFiltro = sql.IndexOf("Prod_Serv_Grade AS variante", StringComparison.Ordinal);
                Assert.InRange(sql.IndexOf("oculto.Ordem = prod.Ordem_Familia", StringComparison.Ordinal), iElegiveis, iFiltro);
                Assert.InRange(sql.IndexOf("preco.Preco > 0", StringComparison.Ordinal), iElegiveis, iFiltro);
            }
            // Paginacao e anexada pelo montador depois de todo o SQL (sem ORDER BY proprio).
            Assert.DoesNotContain("OFFSET", op.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(op.Ordenacao);
        }
    }

    [Fact]
    public void Disponibilidade_dos_cartoes_considera_so_as_combinacoes_casadas()
    {
        var sql = Candidato().Operacoes!["catalogo_estoque_produtos"].Sql;
        Assert.Equal(2, Regex.Matches(sql, @"esto\.Ordem_Cor IN \(@CorOrdem_01").Count);
        Assert.Equal(2, Regex.Matches(sql, @"esto\.Ordem_Tamanho IN \(@TamanhoOrdem_01").Count);
        var iDisponivel = sql.IndexOf("AS Disponivel", StringComparison.Ordinal);
        var iInvalido = sql.IndexOf("AS Estoque_Invalido", StringComparison.Ordinal);
        Assert.InRange(sql.IndexOf("esto.Ordem_Cor IN", StringComparison.Ordinal), 0, iDisponivel);
        Assert.InRange(sql.LastIndexOf("esto.Ordem_Cor IN", StringComparison.Ordinal), iDisponivel, iInvalido);
    }

    [Fact]
    public void Opcoes_sao_publicas_limitadas_e_nunca_trazem_foto_estoque_ou_contagem()
    {
        var op = Candidato().Operacoes![Opcoes];
        Assert.False(op.UsoInterno);
        Assert.True(op.UsaFotosValidadas);
        Assert.Equal(2000, op.MaxLinhas);
        Assert.Equal(3000, op.TimeoutMs);
        Assert.Equal(500, op.MaxCaracteresTexto);
        Assert.Null(op.Ordenacao);
        Assert.Null(op.SqlContagem);
        foreach (var proibido in new[] { "INSERT ", "UPDATE ", "DELETE ", "EXEC", "INTO ", "DROP ", ";", "Estoque_Atual", "COUNT(", "Foto_Base64", "Qtde" })
            Assert.DoesNotContain(proibido, op.Sql, StringComparison.OrdinalIgnoreCase);
        // So combinacoes publicaveis de produtos elegiveis; sem zero nem nome vazio.
        Assert.Contains("variante.Inativo = 0 AND variante.Bloqueado_Venda = 0", op.Sql);
        Assert.Contains("cor.Ordem > 0", op.Sql);
        Assert.Contains("tam.Ordem > 0", op.Sql);
        // Sentinela "sem dimensao" do Shop9 (Ordem 999) sai das opcoes; so com o nome conhecido.
        Assert.Contains("AND NOT (cor.Ordem = 999 AND UPPER(LTRIM(RTRIM(cor.Nome))) = 'SEM COR')", op.Sql);
        Assert.Contains("AND NOT (tam.Ordem = 999 AND UPPER(LTRIM(RTRIM(tam.Nome))) = 'SEM TAMANHO')", op.Sql);
        Assert.Contains("prod.Tipo = 'G'", op.Sql);
        Assert.Contains("ORDER BY Dimensao, Nome, Ordem", op.Sql);
        Assert.Contains("preco.Preco > 0", op.Sql);
        Assert.Contains("#FotosValidas", op.Sql);
        foreach (var coluna in new[] { "Ordem_Classe", "Ordem_Subclasse", "Ordem_Familia" })
            Assert.Contains($"oculto.Ordem = prod.{coluna}", op.Sql);
        var nomes = op.Parametros.Select(p => p.Nome).ToList();
        Assert.DoesNotContain(nomes, n => n.StartsWith("CorOrdem_") || n.StartsWith("TamanhoOrdem_")
            || n is "Inicio" or "TabelaEscolhida");
        foreach (var obrigatorio in new[] { "ClasseOrdem", "SubclasseOrdem", "FamiliaOrdem", "Busca" })
            Assert.Contains(obrigatorio, nomes);
        Assert.Equal(["CodigoFilial"], op.Parametros.Where(p => p.Obrigatorio).Select(p => p.Nome));
    }

    [Fact]
    public void Validador_e_montador_enviam_filtros_como_valor_e_deixam_ausentes_nulos()
    {
        var op = Candidato().Operacoes!["catalogo_produtos_ids_sem_vendas"];
        var recebidos = new Dictionary<string, JsonElement>
        {
            ["CodigoFilial"] = Inteiro(2), ["Tabela_01"] = Inteiro(4), ["TabelaEscolhida"] = Inteiro(4),
            ["CorOrdem_01"] = Inteiro(10), ["CorOrdem_02"] = Inteiro(11), ["TamanhoOrdem_01"] = Inteiro(1002),
        };
        Assert.Null(ValidadorParametros.Validar(op, recebidos));
        var comando = MontadorComando.Montar(op, recebidos);
        Assert.Equal(10L, Convert.ToInt64(comando.Parameters["@CorOrdem_01"].Value));
        Assert.Equal(1002L, Convert.ToInt64(comando.Parameters["@TamanhoOrdem_01"].Value));
        Assert.Equal(DBNull.Value, comando.Parameters["@CorOrdem_03"].Value);
        Assert.Equal(DBNull.Value, comando.Parameters["@TamanhoOrdem_04"].Value);
        Assert.Equal(op.Parametros.Count + 1, comando.Parameters.Count);

        foreach (var invalido in new[] { "CorOrdem_05", "TamanhoOrdem_05", "CorOrdem" })
            Assert.NotNull(ValidadorParametros.Validar(op, new Dictionary<string, JsonElement>(recebidos)
                { [invalido] = Inteiro(1) }));
        Assert.NotNull(ValidadorParametros.Validar(op, new Dictionary<string, JsonElement>(recebidos)
            { ["CorOrdem_01"] = JsonSerializer.SerializeToElement("10 OR 1=1") }));
    }

    [Fact]
    public void Catalogo_anterior_recusa_os_parametros_novos_e_nao_tem_a_operacao_de_opcoes()
    {
        // Base do CA14 no agente: a vitrine so tem de tratar a recusa; os demais filtros nao usam os parametros.
        var antigas = Anterior().Operacoes!;
        Assert.DoesNotContain(Opcoes, antigas.Keys);
        var recebidos = new Dictionary<string, JsonElement>
        {
            ["CodigoFilial"] = Inteiro(2), ["Tabela_01"] = Inteiro(4), ["TabelaEscolhida"] = Inteiro(4),
            ["CorOrdem_01"] = Inteiro(10),
        };
        var erro = ValidadorParametros.Validar(antigas["catalogo_produtos_ids_sem_vendas"], recebidos);
        Assert.Equal("parametro_invalido", erro?.Codigo);
        recebidos.Remove("CorOrdem_01");
        Assert.Null(ValidadorParametros.Validar(antigas["catalogo_produtos_ids_sem_vendas"], recebidos));
    }
}
