-- CANDIDATO NÃO ASSINADO. Confirmar schema e paginação em homologação.
/* Produtos que podem entrar nas notas de uma filial, com estoque positivo.
   Reproduz os SQLs de produtos por empresa do robo (Hortifruti Santos, PagBank e
   Ponto das Artes). O que variava por cliente virou parametro (nunca texto):
   filtro de classe de imposto, tabela de preco, familia excluida e grade. */
SELECT
    prod.Ordem AS Produto_Ordem,
    prod.Ordem_Familia AS Familia_Ordem,
    prod.Ordem_Classe AS Classe_Ordem,
    prod.Ordem_Subclasse AS Subclasse_Ordem,
    prod.Ordem_Classe_Imposto_Saida AS Classe_Imposto_Ordem,
    prod.Codigo,
    prod.Nome,
    un.Nome AS Unidade,
    prod.Tipo,
    cla.Nome AS Classe_Imposto,
    cla.Codigo AS Classe_Imposto_Codigo,
    cor.Codigo AS Cor,
    tam.Codigo AS Tamanho,
    preco.Preco,
    AVG(movProd.Quantidade) AS Media_Preco_Compra,
    prod.Qtde_Fracionada_Casas_Decimais,
    esto.Qtde_Estoque_Atual
FROM Prod_Serv AS prod
INNER JOIN Fabricantes AS fab ON fab.Ordem = prod.Ordem_Fabricante
INNER JOIN Unidades_Venda AS un ON un.Ordem = prod.Ordem_Unidade_Venda
INNER JOIN Prod_Serv_Precos AS preco ON preco.Ordem_Prod_Serv = prod.Ordem
INNER JOIN Classe_Imposto AS cla ON cla.Ordem = prod.Ordem_Classe_Imposto_Saida
INNER JOIN Tabelas_Preco AS tab ON tab.Ordem = preco.Ordem_Tabela_Preco
INNER JOIN Estoque_Atual AS esto ON esto.Ordem_Prod_Serv = prod.Ordem
       AND esto.Ordem_Filial = (SELECT Ordem FROM Filiais WHERE Codigo = @CodigoFilialEstoque)
-- Grade: sem cor/tamanho o LEFT JOIN devolve NULL e a linha segue igual.
LEFT JOIN Cores AS cor ON cor.Ordem = esto.Ordem_Cor
LEFT JOIN Tamanhos AS tam ON tam.Ordem = esto.Ordem_Tamanho
INNER JOIN Movimento_Prod_Serv AS movProd ON movProd.Ordem_Prod_Serv = prod.Ordem
INNER JOIN Movimento AS mov ON mov.Ordem = movProd.Ordem_Movimento
WHERE movProd.Linha_Excluida = 0
  AND mov.Data_Efetivado_Estoque >= DATEADD(DAY, -ISNULL(@DiasVendas, 90), GETDATE())
  AND mov.Ordem_Filial = (SELECT Ordem FROM Filiais WHERE Codigo = @CodigoFilialOrigem)
  AND mov.Tipo_Operacao = 'VND'
  AND mov.Ordem_Operacao = (SELECT Ordem FROM Operacoes WHERE Codigo = @CodigoOperacaoVenda)
  AND tab.Ordem = @OrdemTabelaPreco
  AND esto.Qtde_Estoque_Atual > 0
  AND (@ClasseImposto IS NULL OR cla.Nome LIKE @ClasseImposto)
  AND (@CodigoFamiliaExcluir IS NULL
       OR prod.Ordem_Familia <> (SELECT Ordem FROM Familias WHERE Codigo = @CodigoFamiliaExcluir))
  AND (ISNULL(@ComGrade, 0) = 0 OR (cor.Codigo IS NOT NULL AND tam.Codigo IS NOT NULL))
GROUP BY prod.Ordem, prod.Ordem_Familia, prod.Ordem_Classe, prod.Ordem_Subclasse, prod.Ordem_Classe_Imposto_Saida, prod.Codigo, prod.Nome, prod.Tipo, un.Nome, cla.Codigo, cla.Nome, cor.Codigo, tam.Codigo,
         preco.Preco, prod.Qtde_Fracionada_Casas_Decimais, esto.Qtde_Estoque_Atual;
