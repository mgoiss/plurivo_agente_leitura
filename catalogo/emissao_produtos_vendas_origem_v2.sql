/* Produtos vendidos na filial de origem nos ultimos @DiasVendas dias, SEM exigir compra,
   transferencia nem estoque (criterio "vendas da origem" da lista sem estoque, issue #203).
   Mesmas colunas do contrato v2 da lista sem estoque. A variacao (cor/tamanho) e a da venda na origem
   e o estoque lido e o da filial de destino (pode ser zero). A media de quantidade e das vendas da
   janela. Filtro de classe de imposto, familia e grade entram como parametro. */
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
    cor.Codigo AS Cor,
    tam.Codigo AS Tamanho,
    cla.Nome AS Classe_Imposto,
    cla.Codigo AS Classe_Imposto_Codigo,
    preco.Preco,
    ISNULL(med.Media, 0) AS Media_Preco_Compra,
    prod.Qtde_Fracionada_Casas_Decimais,
    ISNULL(esto.Qtde_Estoque_Atual, 0) AS Qtde_Estoque_Atual
FROM Prod_Serv AS prod
INNER JOIN Fabricantes AS fab ON fab.Ordem = prod.Ordem_Fabricante
INNER JOIN Unidades_Venda AS un ON un.Ordem = prod.Ordem_Unidade_Venda
INNER JOIN Prod_Serv_Precos AS preco ON preco.Ordem_Prod_Serv = prod.Ordem
INNER JOIN Classe_Imposto AS cla ON cla.Ordem = prod.Ordem_Classe_Imposto_Saida
INNER JOIN Tabelas_Preco AS tab ON tab.Ordem = preco.Ordem_Tabela_Preco
INNER JOIN (
    SELECT mp.Ordem_Prod_Serv, mp.Ordem_Cor, mp.Ordem_Tamanho, AVG(mp.Quantidade) AS Media
    FROM Movimento_Prod_Serv AS mp
    INNER JOIN Movimento AS m ON m.Ordem = mp.Ordem_Movimento
    WHERE mp.Linha_Excluida = 0
      AND m.Tipo_Operacao = 'VND'
      AND m.Ordem_Filial = @OrdemFilialOrigem
      AND m.Ordem_Operacao = (SELECT Ordem FROM Operacoes WHERE Codigo = @CodigoOperacaoVenda)
      AND m.Data_Efetivado_Estoque >= DATEADD(DAY, -ISNULL(@DiasVendas, 90), GETDATE())
    GROUP BY mp.Ordem_Prod_Serv, mp.Ordem_Cor, mp.Ordem_Tamanho
) AS med ON med.Ordem_Prod_Serv = prod.Ordem
LEFT JOIN Cores AS cor ON cor.Ordem = med.Ordem_Cor
LEFT JOIN Tamanhos AS tam ON tam.Ordem = med.Ordem_Tamanho
LEFT JOIN Estoque_Atual AS esto ON esto.Ordem_Prod_Serv = prod.Ordem
    AND esto.Ordem_Filial = @OrdemFilialDestino
    AND esto.Ordem_Cor = med.Ordem_Cor AND esto.Ordem_Tamanho = med.Ordem_Tamanho
WHERE tab.Ordem = @OrdemTabelaPreco
  AND (@ClasseImposto IS NULL OR cla.Nome LIKE @ClasseImposto)
  AND (@CodigoFamiliaExcluir IS NULL
       OR prod.Ordem_Familia <> (SELECT Ordem FROM Familias WHERE Codigo = @CodigoFamiliaExcluir))
  AND (ISNULL(@ComGrade, 0) = 0 OR (cor.Codigo IS NOT NULL AND tam.Codigo IS NOT NULL))
GROUP BY prod.Ordem, prod.Ordem_Familia, prod.Ordem_Classe, prod.Ordem_Subclasse, prod.Ordem_Classe_Imposto_Saida, prod.Codigo, prod.Nome, prod.Tipo, cor.Codigo, tam.Codigo, esto.Qtde_Estoque_Atual, un.Nome, preco.Preco, cla.Codigo, cla.Nome,
         prod.Qtde_Fracionada_Casas_Decimais, med.Media;
