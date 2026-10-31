using System.Text.Json;
using System.Text.RegularExpressions;
using AgenteShop9.Catalogos;
using AgenteShop9.Operacoes;

namespace AgenteShop9.Testes;

/// <summary>
/// Candidato da issue #111 (Fase 3): metadados de fotos em lote, sem o blob.
/// Nao assina nem publica nada — so confere o arquivo candidato.
/// </summary>
public class CatalogoCandidatoAceleracaoImagensTestes
{
    private const string Nome = "catalogo_fotos_meta_lote";

    private static Catalogo Candidato() => Catalogo.Analisar(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "catalogo.candidato-aceleracao-imagens.json")));

    private static Catalogo Publico() => Catalogo.Analisar(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "catalogo.candidato-publico.json")));

    private static Operacao Meta() => Candidato().Operacoes![Nome];

    [Fact]
    public void Versao_e_maior_que_a_do_candidato_publico()
    {
        var versao = Candidato().VersaoCatalogo;
        Assert.Equal(33, versao);
        Assert.True(versao > Publico().VersaoCatalogo);
    }

    [Fact]
    public void Preserva_todas_as_operacoes_do_candidato_publico_e_acrescenta_so_a_nova()
    {
        var antigas = Publico().Operacoes!;
        var novas = Candidato().Operacoes!;
        foreach (var (nome, op) in antigas)
        {
            Assert.Contains(nome, novas.Keys);
            Assert.Equal(op.Sql, novas[nome].Sql);
            Assert.Equal(op.Parametros.Count, novas[nome].Parametros.Count);
        }
        Assert.Equal(antigas.Count + 1, novas.Count);
        Assert.Contains(Nome, novas.Keys);
    }

    [Fact]
    public void Operacao_e_publica_select_puro_e_nunca_devolve_a_foto()
    {
        var op = Meta();
        Assert.False(op.UsoInterno);
        Assert.False(op.UsaFotosValidadas);
        Assert.Equal(432, op.MaxLinhas);
        Assert.Equal(3000, op.TimeoutMs);
        Assert.Null(op.SqlContagem);
        Assert.StartsWith("WITH Lote AS", op.Sql);
        Assert.DoesNotContain("OPENJSON", op.Sql);
        Assert.DoesNotContain("#", op.Sql);
        foreach (var proibido in new[] { "INSERT ", "UPDATE ", "DELETE ", "EXEC", "INTO ", "DROP ", ";" })
            Assert.DoesNotContain(proibido, op.Sql, StringComparison.OrdinalIgnoreCase);
        // Unica referencia a coluna Foto: dentro de DATALENGTH ou LEFT, nunca selecionada crua.
        var referencias = Regex.Matches(op.Sql, @"\bfoto\.Foto\b").Count;
        var seguras = Regex.Matches(op.Sql, @"(DATALENGTH|LEFT)\(foto\.Foto\b").Count;
        Assert.True(referencias > 0);
        Assert.Equal(referencias, seguras);
        Assert.DoesNotContain("Foto_Base64", op.Sql);
        // O SELECT final so projeta os quatro metadados.
        Assert.Contains("SELECT Produto_Ordem, Imagem_Ordem, Foto_Caracteres, Formato_Provavel\nFROM Ranque", op.Sql);
        Assert.Contains("Posicao_Ranque <= 9", op.Sql);
        Assert.Contains("foto.Principal DESC, foto.Posicao ASC, foto.Ordem ASC", op.Sql);
        Assert.Contains("LEFT(foto.Foto, 11) = 'iVBORw0KGgo'", op.Sql);
        Assert.Contains("'/9j/'", op.Sql);
    }

    [Fact]
    public void Nenhum_select_do_candidato_novo_projeta_a_coluna_foto_inteira()
    {
        // Garantia so da operacao nova; as operacoes legadas (indice de fotos) seguem como estao.
        Assert.DoesNotMatch(@"SELECT[^()]*\bfoto\.Foto\b\s*(AS|,|\n)", Meta().Sql);
    }

    [Fact]
    public void Parametros_sao_inteiros_opcionais_e_contam_como_esperado()
    {
        var parametros = Meta().Parametros;
        Assert.Equal(1 + 8 + 24 + 48, parametros.Count);
        Assert.Equal(parametros.Count, parametros.Select(p => p.Nome).Distinct().Count());
        Assert.All(parametros, p => Assert.Equal("inteiro", p.Tipo));
        Assert.Equal(["CodigoFilial"], parametros.Where(p => p.Obrigatorio).Select(p => p.Nome));
        Assert.Equal(48, parametros.Count(p => Regex.IsMatch(p.Nome, @"^Produto_(0[1-9]|[1-3]\d|4[0-8])$")));
        Assert.Equal(8, parametros.Count(p => p.Nome.StartsWith("Tabela_")));
        foreach (var prefixo in new[] { "OcultarClasse_", "OcultarSubclasse_", "OcultarFamilia_" })
            Assert.Equal(8, parametros.Count(p => p.Nome.StartsWith(prefixo)));
        // Mesmos nomes de catalogo_fotos, exceto ProdutoOrdem (trocado pelos 48 Produto_NN).
        var legados = Candidato().Operacoes!["catalogo_fotos"].Parametros.Select(p => p.Nome)
            .Where(n => n != "ProdutoOrdem").ToHashSet();
        Assert.True(legados.IsSubsetOf(parametros.Select(p => p.Nome).ToHashSet()));
    }

    [Fact]
    public void Todo_parametro_declarado_e_usado_e_so_aparece_como_valor()
    {
        var op = Meta();
        var usados = Regex.Matches(op.Sql, @"@([A-Za-z_][A-Za-z0-9_]*)")
            .Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal(op.Parametros.Select(p => p.Nome).ToHashSet(), usados);
        // Nenhum parametro vira nome de tabela/coluna, TOP ou ORDER BY.
        Assert.DoesNotMatch(@"(FROM|JOIN|TOP\s*\(?|ORDER BY[^\n]*?|PARTITION BY|\.)\s*@", op.Sql);
        Assert.DoesNotMatch(@"\bTOP\b", op.Sql);
        Assert.DoesNotMatch(@"ORDER BY[^\n]*@", op.Sql);
    }

    [Fact]
    public void Elegibilidade_e_igual_a_de_catalogo_fotos_e_avaliada_uma_vez_por_produto()
    {
        var sql = Meta().Sql;
        Assert.Contains("prod.Tipo IN ('N', 'G')", sql);
        Assert.Contains("prod.Inativo = 0 AND prod.Bloqueado_Venda = 0", sql);
        foreach (var coluna in new[] { "Ordem_Classe", "Ordem_Subclasse", "Ordem_Familia" })
            Assert.Contains($"oculto.Ordem = prod.{coluna}", sql);
        Assert.Contains("preco.Inativo = 0 AND preco.Preco > 0", sql);
        Assert.Contains("preco.Ordem_Tabela_Preco IN (@Tabela_01", sql);
        Assert.Contains("grade.Inativo = 0 AND grade.Bloqueado_Venda = 0", sql);
        Assert.Contains("filial.Codigo = @CodigoFilial", sql);
        Assert.Contains("DATALENGTH(foto.Foto) BETWEEN 1 AND 1000000", sql);
        // Elegibilidade na CTE Elegiveis (uma vez por produto), nao por foto.
        var iElegiveis = sql.IndexOf("Elegiveis AS (", StringComparison.Ordinal);
        var iRanque = sql.IndexOf("Ranque AS (", StringComparison.Ordinal);
        Assert.True(iElegiveis > 0 && iRanque > iElegiveis);
        Assert.InRange(sql.IndexOf("preco.Preco > 0", StringComparison.Ordinal), iElegiveis, iRanque);
        Assert.InRange(sql.IndexOf("grade.Ordem_Prod_Serv", StringComparison.Ordinal), iElegiveis, iRanque);
    }

    [Fact]
    public void Validador_e_montador_aceitam_pedido_de_lote_completo_e_recusam_parametro_estranho()
    {
        var op = Meta();
        var recebidos = new Dictionary<string, JsonElement>
        {
            ["CodigoFilial"] = JsonSerializer.SerializeToElement(1),
            ["Tabela_01"] = JsonSerializer.SerializeToElement(3),
        };
        for (var i = 1; i <= 48; i++)
            recebidos[$"Produto_{i:00}"] = JsonSerializer.SerializeToElement(1000 + i);
        Assert.Null(ValidadorParametros.Validar(op, recebidos));

        var comando = MontadorComando.Montar(op, recebidos);
        Assert.Equal(op.Sql, comando.CommandText);
        Assert.Equal(3, comando.CommandTimeout);
        Assert.Equal(op.Parametros.Count + 1, comando.Parameters.Count); // + @max_linhas
        Assert.Equal(1001L, Convert.ToInt64(comando.Parameters["@Produto_01"].Value));
        Assert.Equal(DBNull.Value, comando.Parameters["@OcultarClasse_01"].Value);

        Assert.NotNull(ValidadorParametros.Validar(op,
            new Dictionary<string, JsonElement> { ["CodigoFilial"] = JsonSerializer.SerializeToElement(1),
                ["Produto_49"] = JsonSerializer.SerializeToElement(1) }));
        Assert.NotNull(ValidadorParametros.Validar(op,
            new Dictionary<string, JsonElement> { ["Produto_01"] = JsonSerializer.SerializeToElement(1) }));
        Assert.NotNull(ValidadorParametros.Validar(op,
            new Dictionary<string, JsonElement> { ["CodigoFilial"] = JsonSerializer.SerializeToElement(1),
                ["Produto_01"] = JsonSerializer.SerializeToElement("1; DROP TABLE x") }));
    }
}
