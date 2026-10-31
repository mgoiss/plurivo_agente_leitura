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
        Assert.Equal(32, candidato.VersaoCatalogo);
        // Candidato superado: o catalogo em uso so acrescenta operacoes, nunca perde as dele.
        Assert.All(candidato.Operacoes!.Keys, nome => Assert.Contains(nome, emUso.Operacoes!.Keys));
        var capacidade = candidato.Operacoes!["catalogo_ocultacoes_capacidade"];
        Assert.Equal("SELECT CAST(32 AS int) AS Versao_Catalogo", capacidade.Sql);
        Assert.Empty(capacidade.Parametros);
        var foto = candidato.Operacoes!["catalogo_foto"];
        Assert.Equal(1, foto.MaxLinhas);
        Assert.Equal(2000, foto.TimeoutMs);
        Assert.Equal(1000000, foto.MaxCaracteresTexto);
        Assert.Contains("prod.Bloqueado_Venda = 0", foto.Sql);
        Assert.Contains("prod.Inativo = 0", foto.Sql);
        Assert.Contains("foto.Ordem = @ImagemOrdem", foto.Sql);
        Assert.Contains("preco.Ordem_Tabela_Preco IN", foto.Sql);
        Assert.Contains("grade.Bloqueado_Venda = 0", foto.Sql);
        Assert.Contains("prod.Ordem_Classe AS Classe_Ordem", foto.Sql);
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
        Assert.Equal(35, foto.Parametros.Count);
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

    [Fact]
    public void Navegacao_usa_operacoes_fixas_e_vendas_distintas_sem_exigir_nota_fiscal()
    {
        var operacoes = Candidato().Operacoes!;
        foreach (var nome in new[] { "catalogo_inicio", "catalogo_classes", "catalogo_subclasses",
            "catalogo_familias", "catalogo_produtos_ids", "catalogo_produtos_ids_sem_vendas",
            "catalogo_cartoes", "catalogo_relacionados" })
        {
            var operacao = operacoes[nome];
            Assert.Equal(2000, operacao.TimeoutMs);
            Assert.DoesNotContain("OPENJSON", operacao.Sql);
            Assert.Contains("prod.Tipo IN ('N', 'G')", operacao.Sql);
            Assert.Contains("prod.Bloqueado_Venda = 0", operacao.Sql);
            Assert.True(operacao.UsaFotosValidadas);
            Assert.Contains("#FotosValidas", operacao.Sql);
            Assert.DoesNotContain("SELECT *", operacao.Sql);
        }
        foreach (var nome in new[] { "catalogo_inicio", "catalogo_classes", "catalogo_subclasses",
            "catalogo_familias", "catalogo_produtos_ids", "catalogo_produtos_ids_sem_vendas",
            "catalogo_cartoes", "catalogo_relacionados", "catalogo_produto",
            "catalogo_variantes", "catalogo_fotos", "catalogo_foto" })
        {
            var operacao = operacoes[nome];
            foreach (var coluna in new[] { "Ordem_Classe", "Ordem_Subclasse", "Ordem_Familia" })
            {
                Assert.Contains($"oculto.Ordem = prod.{coluna}", operacao.Sql);
                if (operacao.SqlContagem is not null)
                    Assert.Contains($"oculto.Ordem = prod.{coluna}", operacao.SqlContagem);
            }
            Assert.Equal(24, operacao.Parametros.Count(p => p.Nome.StartsWith("Ocultar")));
        }
        var inicio = operacoes["catalogo_inicio"];
        Assert.Contains("TOP (7)", inicio.Sql);
        Assert.Contains("SUM(CAST(item.Quantidade AS decimal(19,4)))", inicio.Sql);
        Assert.Contains("mov.Efetivado_Estoque = 1", inicio.Sql);
        Assert.Contains("Data_Efetivado_Estoque >= @Inicio", inicio.Sql);
        Assert.Contains("@Inicio >= DATEADD(DAY, -59", inicio.Sql);
        Assert.Contains("Data_Efetivado_Estoque <= GETDATE()", inicio.Sql);
        Assert.DoesNotContain("Movimento_Documentos_Fiscais", inicio.Sql);
        var familias = operacoes["catalogo_familias"];
        foreach (var nome in new[] { "catalogo_classes", "catalogo_subclasses", "catalogo_familias" })
        {
            Assert.Contains("categoria.Ordem > 0", operacoes[nome].Sql);
            Assert.Contains("categoria.Ordem > 0", operacoes[nome].SqlContagem);
        }
        Assert.Contains("filial.Codigo = @CodigoFilial", familias.Sql);
        Assert.Contains("esto.Ordem_Edicao = 0 AND esto.Invalido = 0", familias.Sql);
        Assert.Contains("esto.Qtde_Estoque_Atual > 0", familias.Sql);
        Assert.Contains("grade.Ordem_Cor = esto.Ordem_Cor", familias.Sql);
        var relacionados = operacoes["catalogo_relacionados"];
        Assert.Contains("COUNT(DISTINCT item.Ordem_Movimento)", relacionados.Sql);
        Assert.Contains("item.Linha_Excluida = 0", relacionados.Sql);
        Assert.Contains("mov.Desefetivado_Estoque = 0", relacionados.Sql);
        Assert.Contains("mov.Efetivado_Estoque = 1", relacionados.Sql);
        Assert.Contains("Origem_Valida", relacionados.Sql);
        var identidades = operacoes["catalogo_produtos_ids"];
        Assert.Contains("mov.Efetivado_Estoque = 1", identidades.Sql);
        Assert.Contains("COUNT(*)", identidades.SqlContagem);
        Assert.Contains("preco_asc", identidades.Ordenacao!.Colunas.Keys);
        Assert.DoesNotContain("Movimento AS mov", operacoes["catalogo_produtos_ids_sem_vendas"].Sql);
        var comando = MontadorComando.Montar(identidades, new Dictionary<string, JsonElement>(),
            new PaginacaoResolvida { Pagina = 0, TamanhoPagina = 24,
                OrdenarPor = "preco_desc", Direcao = "asc", IncluirTotal = false });
        Assert.Contains("ORDER BY Preco_Desc ASC, prod.Ordem OFFSET", comando.CommandText);
        var cartoes = operacoes["catalogo_cartoes"];
        Assert.Equal(48, cartoes.Parametros.Count(p => p.Nome.StartsWith("Produto_")));
        Assert.Equal(8, cartoes.Parametros.Count(p => p.Nome.StartsWith("Tabela_")));
        Assert.Contains("prod.Ordem IN (@Produto_01", cartoes.Sql);
        var estoque = operacoes["catalogo_estoque_produtos"];
        Assert.Equal(2000, estoque.TimeoutMs);
        Assert.DoesNotContain("OPENJSON", estoque.Sql);
        Assert.Equal(48, estoque.Parametros.Count(p => p.Nome.StartsWith("Produto_")));
        Assert.Contains("esto.Ordem_Edicao = 0", estoque.Sql);
        Assert.True(operacoes["catalogo_indice_fotos"].UsoInterno);
        Assert.Equal(50000, operacoes["catalogo_indice_fotos"].MaxLinhas);
        Assert.Equal(5000, operacoes["catalogo_indice_fotos"].TimeoutMs);
    }

    [Fact]
    public void Indice_escolhe_segunda_foto_quando_primeira_e_invalida()
    {
        const string fotoValida = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jUygAAAAASUVORK5CYII=";
        var fotos = new (int ProdutoOrdem, int ImagemOrdem, string? FotoBase64)[]
        {
            (136, 80, "%%%"),
            (136, 81, fotoValida),
            (6998, 26, fotoValida),
        };
        var selecionadas = IndiceFotosPublicas.SelecionarPrimeirasValidas(
            fotos, texto => texto == fotoValida);
        Assert.Equal(81, selecionadas[136]);
        Assert.Equal(26, selecionadas[6998]);
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            Assert.False(IndiceFotosPublicas.FotoValida("%%%"));
            Assert.True(IndiceFotosPublicas.FotoValida(fotoValida));
        }
    }

    [Fact]
    public void Preparo_de_fotos_isola_tabelas_antes_de_aplicar_limite_por_produto()
    {
        var operacao = Candidato().Operacoes!["catalogo_indice_fotos"];
        Assert.Contains("filial.Codigo = @CodigoFilial", operacao.Sql);
        Assert.Contains("preco.Ordem_Tabela_Preco IN (@Tabela_01", operacao.Sql);
        Assert.Contains("preco.Inativo = 0 AND preco.Preco > 0", operacao.Sql);
        using var jsonA = JsonDocument.Parse("""{"CodigoFilial":2,"Tabela_01":4}""");
        using var jsonB = JsonDocument.Parse("""{"CodigoFilial":2,"Tabela_01":9}""");
        var recebidoA = jsonA.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        var recebidoB = jsonB.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        var parametrosA = IndiceFotosPublicas.ParametrosEscopo(recebidoA);
        var parametrosB = IndiceFotosPublicas.ParametrosEscopo(recebidoB);
        Assert.Equal("30:2:4", IndiceFotosPublicas.ChaveEscopo(30, parametrosA));
        Assert.Equal("30:2:9", IndiceFotosPublicas.ChaveEscopo(30, parametrosB));
        var comandoA = MontadorComando.Montar(operacao, parametrosA);
        Assert.Equal(4L, comandoA.Parameters["@Tabela_01"].Value);
        Assert.Equal(DBNull.Value, comandoA.Parameters["@Tabela_02"].Value);

        // Fixture do resultado que o SQL assinado seleciona por tabela:
        // A tem primeira foto ruim e segunda boa; B so tem preco na tabela 9.
        var fonte = new List<(int Produto, int Tabela, int Foto, string Conteudo)>
        {
            (136, 4, 80, "%%%"),
            (136, 4, 81, "PNG valido"),
        };
        fonte.AddRange(Enumerable.Range(1, 9).Select(i =>
            (6998, 9, 100 + i, "PNG valido")));
        var acumuladorA = new AcumuladorIndiceFotosPublicas();
        foreach (var linha in fonte.Where(x => x.Tabela == 4))
            acumuladorA.Incluir(linha.Produto, linha.Foto, linha.Conteudo.Length,
                linha.Conteudo, texto => texto == "PNG valido");
        Assert.Equal(81, acumuladorA.Fotos[136]);
        Assert.DoesNotContain(6998, acumuladorA.Fotos.Keys);
        var acumuladorB = new AcumuladorIndiceFotosPublicas();
        var excesso = Assert.Throws<IndiceFotosCapacidadeException>(() =>
        {
            foreach (var linha in fonte.Where(x => x.Tabela == 9))
                acumuladorB.Incluir(linha.Produto, linha.Foto, linha.Conteudo.Length,
                    linha.Conteudo, texto => texto == "PNG valido");
        });
        Assert.Equal("fotos_por_produto", excesso.Motivo);
        Assert.Equal(9, excesso.LinhasLidas);
    }
}
