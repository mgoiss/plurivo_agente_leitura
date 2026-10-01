# Catálogo de operações — como escrever e testar

O `catalogo.json` é a lista fixa de operações que o agente sabe executar. O
backend nunca manda SQL — manda **o nome da operação + os parâmetros**, e o SQL
parametrizado vive aqui (spec 3.3 / 3.3.1). Este README é o guia de **como
escrever uma operação nova** e **quais testes rodar** antes de publicar.

> A partir do catálogo **v16**, toda operação é **paginada** (issue #24). Uma
> operação sem o bloco `ordenacao` ainda funciona (cai no modo antigo, resposta
> única), mas o padrão é: toda operação nova nasce paginada.

---

## 1. Anatomia de uma operação

```jsonc
"nome_da_operacao": {
  "sql":            "SELECT ... FROM ... WHERE ...",   // SEM TOP e SEM ORDER BY
  "parametros":     [ { "nome": "...", "tipo": "...", "obrigatorio": true } ],
  "ordenacao":      { "colunas": { "<chave>": "<coluna real>" }, "padrao": { "coluna": "<chave>", "direcao": "asc" } },
  "desempate":      [ "<coluna>", "<coluna>", ... ],
  "sql_contagem":   "SELECT COUNT(*) FROM ...",         // mesmo FROM/WHERE, sem ORDER BY
  "tamanho_pagina_padrao": 50,
  "tamanho_pagina_max":    200,
  "max_linhas":     10000,
  "timeout_ms":     30000,
  "periodo_max_dias": 92                                // só se a operação tem `inicio`/`fim`
}
```

| campo | para que serve |
|---|---|
| `sql` | O `SELECT`. **Sem `TOP`, sem `ORDER BY` final** — o agente cola os dois. Pode ter `GROUP BY`. Parâmetro só entra como valor (`@Nome`), nunca como nome de coluna. |
| `parametros` | O que o front pode mandar. `tipo`: `inteiro`, `texto`, `decimal`, `data`. `obrigatorio`: recusa o pedido sem ele, antes de tocar no banco. |
| `ordenacao.colunas` | O menu de ordenação. **Chave** (o que o front manda em `ordenar_por`) → **coluna/expressão real** do SQL. O front nunca manda `prod.Codigo`; manda `codigo`. |
| `ordenacao.padrao` | Ordenação usada quando o front não manda `ordenar_por`/`direcao`. `coluna` tem que ser uma das chaves de `colunas`; `direcao` é `asc` ou `desc`. |
| `desempate` | Lista de colunas que, juntas, deixam a ordem **única** — sem isso a paginação repete/pula linha quando o critério escolhido empata. Ver seção 2. |
| `sql_contagem` | Conta o total de linhas, para o "página 3 de 47". Mesmo `FROM`/`WHERE` do `sql`, **sem** `ORDER BY`/`OFFSET`, com `COUNT(*)`. Só roda quando o pedido traz `incluir_total`. |
| `tamanho_pagina_padrao` / `_max` | Linhas por página: o padrão quando o front não diz, e o teto que ele não pode passar. O backend ainda tem um teto duro global (`TAMANHO_PAGINA_MAX`, 200). |
| `max_linhas` / `timeout_ms` | Rede de segurança do executor e timeout do banco. Mantenha 10000 / 30000. |
| `periodo_max_dias` | Só em operação com faixa de datas: recusa janela maior que isso. |

---

## 2. O `desempate` — a parte que exige atenção

A paginação usa `OFFSET n ROWS FETCH NEXT m ROWS ONLY`. Isso só é **estável**
(página 2 não repete nem pula nada da página 1) se o `ORDER BY` for uma ordem
**total** — ou seja, nunca houver duas linhas "empatadas". O `desempate` é o que
garante isso. Depende do formato da consulta:

### Consulta simples (sem `GROUP BY`)

O `desempate` é a **chave primária** da tabela principal — uma coluna só, sempre
única.

```jsonc
"desempate": ["prod.Ordem"]
```

As colunas de `ordenacao.colunas` **não** precisam estar no `desempate` (a PK já
resolve). Ex.: `listar_produtos_estoque` ordena por `codigo`/`nome`/`estoque` e
desempata por `prod.Ordem`.

### Consulta com `GROUP BY`

Não há PK no resultado — cada linha é um grupo. O `desempate` é a **tupla inteira
do `GROUP BY`** (menos o `SUM`/agregados). Assim quaisquer duas linhas diferem em
pelo menos uma coluna do desempate.

```jsonc
"desempate": [
  "prod.Codigo", "prod.Nome", "ncm.Codigo", "cfop.Codigo",
  "RTRIM(clas.Nome)", "clasOp.ICMS_CST_CSOSN", "clasOp.PIS_COFINS_CST"
]
```

Aqui **toda coluna de `ordenacao.colunas` também está no `desempate`** — e tudo
bem: o agente **remove a coluna escolhida** do desempate antes de montar o
`ORDER BY`, senão o SQL Server recusa coluna repetida (erro **169**: *"A column
has been specified more than once in the order by list"*).

### Regras do `desempate`

- Um item da lista = uma coluna ou expressão. **Sem vírgula dentro de um item.**
- Escreva **exatamente igual** ao valor em `ordenacao.colunas` (mesma
  capitalização, mesmo espaçamento) — é assim que o agente reconhece e remove a
  duplicata. `RTRIM(clas.Nome)` nos dois lugares, não `RTRIM( clas.Nome )` num e
  `rtrim(clas.nome)` no outro.
- Numa consulta com `GROUP BY`, todo item do `desempate` tem que ser uma coluna
  **do `GROUP BY`** (o SQL Server não deixa ordenar por coluna fora do
  agrupamento).

---

## 3. O `sql_contagem`

### Consulta simples

Copie o `sql`, troque a lista do `SELECT` por `COUNT(*)`, apague o `ORDER BY`.
Mesmos `JOIN`s e mesmo `WHERE` (inclusive os `(@X IS NULL OR ...)`), mesmos
parâmetros declarados.

```sql
SELECT COUNT(*) FROM Prod_Serv AS prod
INNER JOIN ...
WHERE esto.Ordem_Filial = (SELECT Ordem FROM Filiais WHERE Codigo = @CodigoFilial)
  AND (@Codigo IS NULL OR prod.Codigo = @Codigo)
  AND ...
```

### Consulta com `GROUP BY`

Contar **grupos**: embrulhe a consulta agrupada (sem `ORDER BY`) e conte por fora.

```sql
SELECT COUNT(*) FROM (
  SELECT 1 AS n
  FROM Movimento AS mov
  INNER JOIN ...
  WHERE ...
  GROUP BY prod.Codigo, prod.Nome, ncm.Codigo, cfop.Codigo, RTRIM(clas.Nome),
           clasOp.ICMS_CST_CSOSN, clasOp.PIS_COFINS_CST
) AS t
```

O `sql_contagem` **não** referencia `@offset`, `@buscar` nem `@max_linhas`.

---

## 4. Regras que o agente exige (senão a operação não sobe)

Ao carregar o catálogo, o agente recusa a operação se:

- `sql` estiver vazio, ou `max_linhas` / `timeout_ms` / `periodo_max_dias` < 1;
- tiver `ordenacao` mas **faltar** `desempate` ou `sql_contagem` (os três andam juntos);
- `ordenacao.colunas` estiver vazio, ou algum par tiver chave/coluna em branco;
- `ordenacao.padrao.coluna` não for uma das chaves de `ordenacao.colunas`;
- `ordenacao.padrao.direcao` não for `asc` nem `desc`;
- `desempate` estiver vazio ou tiver item em branco;
- `tamanho_pagina_padrao` estiver fora de `1..tamanho_pagina_max`.

E ainda: o `sql` **não pode** conter `ORDER BY` (o agente adiciona o dele — dois
`ORDER BY` = erro de sintaxe). Isso não é checado automaticamente; é você ao
escrever.

---

## 5. O que o agente monta em runtime

Recebido um pedido com `ordenar_por = "cfop"`, `direcao = "desc"`, `pagina = 2`,
`tamanho_pagina = 20`:

```
<sql da operação>
ORDER BY cfop.Codigo DESC,          -- coluna real de ordenacao.colunas["cfop"], direção
         prod.Codigo, prod.Nome, ncm.Codigo, RTRIM(clas.Nome),   -- desempate SEM cfop.Codigo
         clasOp.ICMS_CST_CSOSN, clasOp.PIS_COFINS_CST
OFFSET 40 ROWS FETCH NEXT 21 ROWS ONLY   -- 40 = pagina*tamanho ; 21 = tamanho+1
```

- Busca `tamanho_pagina + 1` linhas. Se a linha extra veio → há próxima página
  (`tem_proxima = true`) e ela **não** volta na resposta.
- Se o pedido traz `incluir_total`, roda o `sql_contagem` (mesmos parâmetros) e
  calcula `total_paginas = teto(total / tamanho_pagina)`.

---

## 6. Contrato do pedido e da resposta (`/api/consulta`)

**Pedido** (campos no topo do corpo, ao lado de `operacao`/`parametros`/`servidor_ordem`):

| campo | |
|---|---|
| `pagina` | inteiro, **começa em 0**. Ausente = 0. |
| `tamanho_pagina` | inteiro. Ausente = `tamanho_pagina_padrao` da operação. Acima do `tamanho_pagina_max` (ou do teto do backend) → HTTP 422. |
| `ordenar_por` | **chave** de `ordenacao.colunas` (`"codigo"`, `"cfop"`...). Ausente = `ordenacao.padrao.coluna`. Chave inexistente → HTTP 422. |
| `direcao` | `"asc"` ou `"desc"`. Ausente = `ordenacao.padrao.direcao`. |
| `incluir_total` | `true`/`false`. `true` só na primeira página; o front guarda o total e reaproveita nas seguintes. |

**Resposta** (sucesso) ganha o bloco `paginacao`:

```jsonc
{
  "status": "ok",
  "linhas": [ ... ],
  "paginacao": {
    "pagina": 0,
    "tamanho_pagina": 50,
    "tem_proxima": true,
    "total_linhas": 128,     // só quando incluir_total = true
    "total_paginas": 3,      // idem
    "ordenado_por": "codigo",
    "direcao": "asc"
  }
}
```

---

## 7. Template para copiar

```jsonc
"minha_operacao_nova": {
  "sql": "SELECT t.Ordem, t.Codigo, t.Nome, t.Valor FROM Tabela AS t INNER JOIN Filiais AS f ON f.Ordem = t.Ordem_Filial WHERE f.Codigo = @CodigoFilial AND (@Nome IS NULL OR t.Nome LIKE '%' + @Nome + '%')",
  "sql_contagem": "SELECT COUNT(*) FROM Tabela AS t INNER JOIN Filiais AS f ON f.Ordem = t.Ordem_Filial WHERE f.Codigo = @CodigoFilial AND (@Nome IS NULL OR t.Nome LIKE '%' + @Nome + '%')",
  "parametros": [
    { "nome": "CodigoFilial", "tipo": "inteiro", "obrigatorio": true },
    { "nome": "Nome", "tipo": "texto", "obrigatorio": false }
  ],
  "ordenacao": {
    "colunas": { "codigo": "t.Codigo", "nome": "t.Nome", "valor": "t.Valor" },
    "padrao": { "coluna": "codigo", "direcao": "asc" }
  },
  "desempate": ["t.Ordem"],
  "tamanho_pagina_padrao": 50,
  "tamanho_pagina_max": 200,
  "max_linhas": 10000,
  "timeout_ms": 30000
}
```

Depois de editar, **suba o `versao_catalogo`** no topo do arquivo (anti-retrocesso:
o agente recusa versão menor ou igual à que já roda).

---

## 8. Checklist de teste de uma operação nova

Roda a lógica do broker (validação de parâmetro, montagem do `ORDER BY`,
contrato de erro) → **testes automáticos** (`cd backend && pytest` +
`cd agente && dotnet test`). O que os testes **não** alcançam e você confere no
**Shop9 real** (SSMS + `/api/consulta`):

### A. SQL no SSMS (base de homologação)

1. **Nível de compatibilidade** ≥ 110 — uma vez por cliente, não por operação:
   `SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME();`
2. **A página roda.** Cole o `sql` da operação + `ORDER BY <padrao> , <desempate sem a coluna do padrao>` + `OFFSET 0 ROWS FETCH NEXT 11 ROWS ONLY`. Tem que rodar sem erro (atenção ao erro 169 — coluna repetida).
3. **Não repete nem pula linha.** Compare "página 0" (`OFFSET 0 FETCH 10`) com "página 1" (`OFFSET 5 FETCH 5`): as 5 linhas da página 1 têm que ser iguais às linhas 6–10 da página 0. (Modelo pronto em `especificacao/Checklist_Teste_Paginacao_v16.md`, teste 1.)
4. **A contagem roda** e devolve um número coerente. Anote o tempo — deve ser rápido (< ~1 s numa base típica).
5. **Página lá no fim** (`OFFSET <milhares>`) responde sem travar.
6. Se a operação tem `GROUP BY`: repita 2–5. Se a base não tiver dado no período (volta 0 linhas), diga isso — a checagem de multi-página fica para uma base com movimento (cliente piloto).

### B. Ponta a ponta pelo `/api/consulta`

Com o agente e o backend da versão nova no ar (ver `documentacao/planos/construcao-plataforma/Checklist_Teste_Paginacao_v16.md` na raiz Plurivo, parte D):

7. `pagina=0, tamanho_pagina=5, incluir_total=true` → 5 linhas + bloco `paginacao` com `total_linhas`/`total_paginas` batendo com o teste 4.
8. `pagina=1, tamanho_pagina=5` (sem `incluir_total`) → linhas batem com "6–10" do teste 3, sem `total_linhas`.
9. `ordenar_por=<outra chave>&direcao=desc` → `paginacao.ordenado_por` reflete, linhas na ordem certa.
10. Entradas inválidas → **HTTP 422** `parametro_invalido`: `pagina=-1`, `tamanho_pagina` acima do teto, `direcao` fora de asc/desc, `ordenar_por` com chave inexistente.
11. Auditoria (`public.auditoria`): as linhas têm `parametros -> paginacao`; nenhum dado do ERP gravado.

---

## 9. Publicar (você — ação irreversível)

A chave privada de assinatura vive só na sua máquina (`~/.shop9-assinatura/`).

```bash
# a partir da raiz Plurivo
./ferramentas/assinatura/assinar-catalogo.py repositorios/agente_leitura/catalogo/catalogo.json   # pede a passphrase
./ferramentas/assinatura/scripts/verificar.sh repositorios/agente_leitura/catalogo/catalogo.json   # confere versao= e sha256
cd repositorios/agente_leitura && git add catalogo/catalogo.json catalogo/catalogo.json.minisig && git commit && git push
```

Depois: subir `catalogo.json` + `.minisig` para o backend, **rollout pela cliente
piloto primeiro** (o auto-update para todos é a Etapa 4.1). O agente verifica a
assinatura, confere o SHA-256, exige `versao=` == `versao_catalogo` e recusa
versão ≤ à que já roda.

Detalhe da assinatura e da chave: `ferramentas/assinatura/README.md`, na raiz Plurivo.
