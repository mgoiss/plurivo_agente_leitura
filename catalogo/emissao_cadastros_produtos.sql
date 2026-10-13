-- CANDIDATO NÃO ASSINADO. Confirmar schema e paginação em homologação.
SELECT dados.Grupo, dados.Identidade, dados.Codigo, dados.Descricao, dados.Sigla FROM (
SELECT 'familias' AS Grupo, CONVERT(varchar(100), c.Ordem) AS Identidade, CONVERT(varchar(100), c.Codigo) AS Codigo, c.Nome AS Descricao, CAST(NULL AS varchar(20)) AS Sigla FROM Familias AS c WHERE EXISTS (SELECT 1 FROM Filiais WHERE Codigo = @CodigoFilial)
UNION ALL
SELECT 'classes' AS Grupo, CONVERT(varchar(100), c.Ordem) AS Identidade, CONVERT(varchar(100), c.Codigo) AS Codigo, c.Nome AS Descricao, CAST(NULL AS varchar(20)) AS Sigla FROM Classes AS c WHERE EXISTS (SELECT 1 FROM Filiais WHERE Codigo = @CodigoFilial)
UNION ALL
SELECT 'subclasses' AS Grupo, CONVERT(varchar(100), c.Ordem) AS Identidade, CONVERT(varchar(100), c.Codigo) AS Codigo, c.Nome AS Descricao, CAST(NULL AS varchar(20)) AS Sigla FROM Subclasses AS c WHERE EXISTS (SELECT 1 FROM Filiais WHERE Codigo = @CodigoFilial)
UNION ALL
SELECT 'classes_imposto' AS Grupo, CONVERT(varchar(100), c.Ordem) AS Identidade, CONVERT(varchar(100), c.Codigo) AS Codigo, c.Nome AS Descricao, CAST(NULL AS varchar(20)) AS Sigla FROM Classe_Imposto AS c WHERE c.Tipo_Operacao = 'S' AND c.Inativo = 0 AND EXISTS (SELECT 1 FROM Filiais WHERE Codigo = @CodigoFilial)
UNION ALL
SELECT 'unidades' AS Grupo, CONVERT(varchar(100), c.Ordem) AS Identidade, CONVERT(varchar(100), c.Codigo) AS Codigo, c.Nome AS Descricao, RTRIM(c.Nome) AS Sigla FROM Unidades_Venda AS c WHERE EXISTS (SELECT 1 FROM Filiais WHERE Codigo = @CodigoFilial)
UNION ALL
SELECT 'tipos' AS Grupo, RTRIM(prod.Tipo) AS Identidade, RTRIM(prod.Tipo) AS Codigo, RTRIM(prod.Tipo) AS Descricao, CAST(NULL AS varchar(20)) AS Sigla FROM Prod_Serv AS prod WHERE prod.Tipo IS NOT NULL AND RTRIM(prod.Tipo) <> '' AND EXISTS (SELECT 1 FROM Filiais WHERE Codigo = @CodigoFilial) GROUP BY RTRIM(prod.Tipo)
) AS dados;
