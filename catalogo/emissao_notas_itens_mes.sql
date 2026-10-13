-- CANDIDATO NÃO ASSINADO. Confirmar totais/rateio/histórico com documentos reais.
/* CANDIDATO: classificação atual do cadastro. Confirmar campos/rateio no Shop9.
   Não há evidência de snapshot histórico de família/classe/subclasse no schema.
   O rateio do desconto/acréscimo global é proporcional ao Preco_Final dos itens. */
SELECT fiscal.Chave_Acesso AS Nota_Chave, mp.Ordem AS Item_Ordem,
    mov.Sequencia AS Seq_Destino, mov.Referencia_Interna AS Referencia_Interna,
    fiscal.Data_Emissao, '65' AS Modelo, 100 AS Status, 0 AS Cancelada,
    prod.Codigo, prod.Tipo, prod.Ordem_Familia AS Familia_Ordem,
    prod.Ordem_Classe AS Classe_Ordem, prod.Ordem_Subclasse AS Subclasse_Ordem,
    prod.Ordem_Classe_Imposto_Saida AS Classe_Imposto_Ordem,
    cla.Codigo AS Classe_Imposto_Codigo, cla.Nome AS Classe_Imposto_Nome,
    CAST(mp.Preco_Final AS decimal(19,6)) AS Valor_Item,
    CAST(mov.Preco_Final_Somado AS decimal(19,6)) AS Total_Nota,
    totais.Total_Itens
FROM Movimento AS mov
INNER JOIN (
    SELECT Ordem_Movimento, Chave_Acesso, MAX(Data_Emissao) AS Data_Emissao
    FROM Movimento_Documentos_Fiscais
    WHERE SUBSTRING(Chave_Acesso, 21, 2) = '65'
    GROUP BY Ordem_Movimento, Chave_Acesso
    HAVING MAX(CASE WHEN Codigo_Status = 100 THEN 1 ELSE 0 END) = 1
       AND MAX(CAST(ISNULL(Documento_Cancelado, 0) AS int)) = 0
) AS fiscal ON fiscal.Ordem_Movimento = mov.Ordem
INNER JOIN Movimento_Prod_Serv AS mp ON mp.Ordem_Movimento = mov.Ordem
INNER JOIN Prod_Serv AS prod ON prod.Ordem = mp.Ordem_Prod_Serv
LEFT JOIN Classe_Imposto AS cla ON cla.Ordem = prod.Ordem_Classe_Imposto_Saida
INNER JOIN (
    SELECT Ordem_Movimento, SUM(CAST(Preco_Final AS decimal(19,6))) AS Total_Itens
    FROM Movimento_Prod_Serv WHERE Linha_Excluida = 0 GROUP BY Ordem_Movimento
) AS totais ON totais.Ordem_Movimento = mov.Ordem
WHERE mov.Ordem_Filial = (SELECT Ordem FROM Filiais WHERE Codigo = @CodigoFilialDestino)
  AND mp.Linha_Excluida = 0
  AND fiscal.Data_Emissao >= @inicio AND fiscal.Data_Emissao < DATEADD(DAY, 1, @fim);
