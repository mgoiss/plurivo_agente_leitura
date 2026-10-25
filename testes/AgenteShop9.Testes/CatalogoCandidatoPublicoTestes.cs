using System.Text.Json;
using AgenteShop9.Catalogos;
using AgenteShop9.Operacoes;

namespace AgenteShop9.Testes;

public class CatalogoCandidatoPublicoTestes
{
    private static Catalogo Candidato() => Catalogo.Analisar(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "catalogo.candidato-publico.json")));

    [Fact]
    public void Candidato_preserva_operacoes_em_uso_e_isola_foto_por_escopo()
    {
        var candidato = Candidato();
        var emUso = Catalogo.Analisar(Fixtures.CatalogoRealBytes);
        Assert.Equal(29, candidato.VersaoCatalogo);
        Assert.All(emUso.Operacoes!.Keys, nome => Assert.Contains(nome, candidato.Operacoes!.Keys));
        var foto = candidato.Operacoes!["catalogo_foto"];
        Assert.Equal(1, foto.MaxLinhas);
        Assert.Equal(2000, foto.TimeoutMs);
        Assert.Equal(1000000, foto.MaxCaracteresTexto);
        Assert.Contains("prod.Bloqueado_Venda = 0", foto.Sql);
        Assert.Contains("prod.Inativo = 0", foto.Sql);
        Assert.Contains("foto.Ordem = @ImagemOrdem", foto.Sql);
        Assert.Contains("preco.Ordem_Tabela_Preco IN", foto.Sql);
        Assert.Contains("grade.Bloqueado_Venda = 0", foto.Sql);
        Assert.DoesNotContain("OPENJSON", foto.Sql);
        var fotos = candidato.Operacoes["catalogo_fotos"];
        Assert.StartsWith("SELECT TOP (9) foto.Ordem AS Imagem_Ordem\nFROM Prod_Serv AS prod\nINNER JOIN Prod_Serv_Fotos AS foto", fotos.Sql);
        Assert.Contains("\nWHERE prod.Ordem = @ProdutoOrdem", fotos.Sql);
        Assert.Contains("foto.Principal DESC, foto.Posicao ASC, foto.Ordem ASC", fotos.Sql);
        Assert.Equal(9, fotos.MaxLinhas);
        Assert.DoesNotContain("Foto_Base64", fotos.Sql);
        Assert.DoesNotContain("WHERE v.Tabela_Ordem", fotos.Sql);
        Assert.DoesNotContain("SELECT prod.Ordem AS Produto_Ordem", fotos.Sql);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(fotos.Sql,
            @"\bSELECT TOP \(9\)").Count);
        var produto = candidato.Operacoes["catalogo_produto"];
        Assert.StartsWith("WITH Permitidas AS (", produto.Sql);
        Assert.Contains("\nSELECT prod.Ordem AS Produto_Ordem", produto.Sql);
        Assert.Contains("\nFROM Prod_Serv AS prod", produto.Sql);
        Assert.Contains("\nWHERE prod.Ordem = @ProdutoOrdem", produto.Sql);
        Assert.DoesNotContain("CROSS APPLY", produto.Sql);
    }

    [Fact]
    public void Parametros_da_foto_sao_inteiros_fixos_e_paginacao_de_tabelas_nao_repete_coluna()
    {
        var operacoes = Candidato().Operacoes!;
        var foto = operacoes["catalogo_foto"];
        Assert.Equal(11, foto.Parametros.Count);
        Assert.All(foto.Parametros, parametro => Assert.Equal("inteiro", parametro.Tipo));
        var tabelas = operacoes["catalogo_tabelas_preco"];
        var comando = MontadorComando.Montar(tabelas, new Dictionary<string, JsonElement>(),
            new PaginacaoResolvida { Pagina = 0, TamanhoPagina = 100,
                OrdenarPor = "ordem", Direcao = "asc", IncluirTotal = false });
        Assert.Contains("ORDER BY tab.Ordem ASC OFFSET", comando.CommandText);
    }

    [Fact]
    public void Produto_variantes_e_estoque_preservam_recorte_confirmado()
    {
        var operacoes = Candidato().Operacoes!;
        var produto = operacoes["catalogo_produto"];
        var variantes = operacoes["catalogo_variantes"];
        var estoque = operacoes["catalogo_estoque_variantes"];
        foreach (var op in new[] { produto, variantes })
        {
            Assert.Contains("prod.Tipo IN ('N', 'G')", op.Sql);
            Assert.Contains("prod.Inativo = 0", op.Sql);
            Assert.Contains("prod.Bloqueado_Venda = 0", op.Sql);
            Assert.Contains("preco", op.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("OPENJSON", op.Sql);
            Assert.Equal(2000, op.TimeoutMs);
        }
        Assert.Contains("grade.Inativo = 0 AND grade.Bloqueado_Venda = 0", variantes.Sql);
        Assert.Contains("LEFT JOIN Estoque_Atual", estoque.Sql);
        Assert.Contains("esto.Ordem_Edicao = 0", estoque.Sql);
        Assert.Contains("esto.Invalido = 1", estoque.Sql);
        Assert.DoesNotContain("Qtde_Estoque_Atual > 0\nWHERE", estoque.Sql);
        Assert.Equal(50, estoque.Parametros.Count);
        Assert.Equal(48, estoque.MaxLinhas);
        Assert.Contains("TotaisGrade AS", estoque.Sql);
        Assert.Contains("MAX(Disponivel) AS Disponivel_Produto", estoque.Sql);
        Assert.Contains("MAX(Estoque_Invalido) AS Estoque_Invalido_Produto", estoque.Sql);
        Assert.DoesNotContain("catalogo_disponibilidade_produto", operacoes.Keys);
        Assert.Contains("prod.Web_Obs1", produto.Sql);
        Assert.Contains("prod.Web_Obs2", produto.Sql);
        Assert.Equal(8000, produto.MaxCaracteresTexto);
    }
}
