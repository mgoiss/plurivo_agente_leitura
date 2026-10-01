# Publicar um catálogo para agentes

Use este guia no MacBook. O catálogo só é aceito pelo agente quando o JSON e a
assinatura `.minisig` correspondem e a versão é maior que a já instalada.

## Arquivos usados

Neste projeto, os arquivos são:

```text
/Users/gois/Documents/Projetos/plurivo/repositorios/agente_leitura/catalogo/catalogo.json
/Users/gois/Documents/Projetos/plurivo/repositorios/agente_leitura/catalogo/catalogo.json.minisig
```

Se você clonou o projeto em outra pasta, troque somente o trecho
`/Users/gois/Documents/Projetos/plurivo` pelo caminho real da raiz Plurivo.
Os comandos abaixo rodam a partir da raiz, porque a ferramenta de assinatura
fica em `ferramentas/assinatura/` e o catálogo em `repositorios/agente_leitura/`.

## 1. Assinar

No Terminal do Mac:

```bash
cd /Users/gois/Documents/Projetos/plurivo
./ferramentas/assinatura/assinar-catalogo.py repositorios/agente_leitura/catalogo/catalogo.json
```

O comando pede a passphrase da chave privada e cria ou substitui:

```text
repositorios/agente_leitura/catalogo/catalogo.json.minisig
```

Nunca copie a chave privada para o Git, VPS ou computador do cliente.

## 2. Verificar a assinatura

```bash
cd /Users/gois/Documents/Projetos/plurivo
./ferramentas/assinatura/scripts/verificar.sh repositorios/agente_leitura/catalogo/catalogo.json
```

O resultado precisa conter `Signature and comment signature verified` e
`versao=23` (ou a versão que estiver no JSON).

## 3. Commit e push

Faça isso se `git status` mostrar mudança em `catalogo.json` ou
`catalogo.json.minisig`:

```bash
cd /Users/gois/Documents/Projetos/plurivo/repositorios/agente_leitura
git status --short
git add catalogo/catalogo.json catalogo/catalogo.json.minisig
git commit -m "chore(catalogo): assina versao 23"
git push
```

Se `git status` não mostrar esses arquivos, eles já foram commitados e não há
nada para repetir.

## 4. Enviar para a VPS

O backend lê os dois arquivos na pasta configurada pela variável `CATALOGO_DIR`.
Quando a variável não existe, a pasta padrão é `./dados_catalogo` dentro do
diretório onde o backend roda na VPS.

No servidor, descubra a pasta configurada:

```bash
printenv CATALOGO_DIR
```

No Mac, envie os dois arquivos. Troque `USUARIO`, `HOST_DA_VPS` e
`/caminho/dados_catalogo` pelos valores reais da sua VPS:

```bash
cd /Users/gois/Documents/Projetos/plurivo/repositorios/agente_leitura
scp catalogo/catalogo.json catalogo/catalogo.json.minisig \
  USUARIO@HOST_DA_VPS:/caminho/dados_catalogo/
```

Se a pasta da VPS for outra, use o valor de `CATALOGO_DIR` no destino do `scp`.
Os dois arquivos precisam ficar juntos e com estes nomes exatos:

```text
catalogo.json
catalogo.json.minisig
```

O agente pode encontrar os arquivos entre uma cópia e outra, rejeitar a dupla
incompleta e tentar de novo no próximo ciclo. Ele só promove o catálogo quando
JSON e assinatura conferem.

## 5. Liberar para um agente de teste

No SQL Editor do Supabase, primeiro localize o servidor:

```sql
select ordem, nome, versao_catalogo_liberada
from public.servidores
order by nome;
```

Depois libere a versão apenas para o servidor de teste:

```sql
update public.servidores
set versao_catalogo_liberada = 23
where ordem = 'UUID_DO_SERVIDOR_DE_TESTE';
```

O agente verifica a atualização em até 15 minutos. Para verificar logo, na
máquina Windows do teste, abra PowerShell como administrador:

```powershell
sc.exe stop AgenteShop9
sc.exe start AgenteShop9
sc.exe query AgenteShop9
```

No Visualizador de Eventos, abra **Logs do Windows > Aplicativo** e filtre pela
origem `AgenteShop9`. Procure a confirmação de que o catálogo versão 23 foi
aceito. No painel de status, o agente deve passar a mostrar versão 23.

## 6. Liberar para todos os agentes

Faça isso somente depois de validar o agente de teste:

```sql
update public.servidores
set versao_catalogo_liberada = 23
where ativo = true;
```

Para excluir um servidor específico, use a lista de UUIDs:

```sql
update public.servidores
set versao_catalogo_liberada = 23
where ativo = true
  and ordem not in ('UUID_PARA_EXCLUIR');
```

Para interromper novas atualizações de um servidor, defina `NULL` antes que ele
baixe a versão:

```sql
update public.servidores
set versao_catalogo_liberada = null
where ordem = 'UUID_DO_SERVIDOR';
```

Isso não reduz a versão de um agente que já aceitou a 23. O agente recusa
retrocesso; uma correção posterior precisa virar uma versão maior, por exemplo
24.

## Depois de publicar a versão 23

1. Envie os dois arquivos assinados à VPS.
2. Libere a versão 23 somente para o agente de teste.
3. Reinicie o serviço ou aguarde até 15 minutos.
4. Confirme a versão 23 no log e no painel de status.
5. Execute `emissao_vendas_por_forma_pagamento` com período curto e confira
   linhas, paginação e total.
6. Só então libere a versão para os demais agentes.

## Executar a consulta de emissão

Com o agente já na versão 23 e o catálogo também publicado na VPS, a operação
é chamada pela API da plataforma. Ela usa o token de um usuário da plataforma,
e não o token do agente.

No Terminal do Mac, informe a URL do backend, o `access_token` do login e o
UUID do servidor que executará a consulta:

```bash
BASE='https://URL_DO_SEU_BACKEND'
JWT='COLE_AQUI_O_ACCESS_TOKEN'
SERVIDOR='UUID_DO_SERVIDOR'
```

Execute uma consulta curta. O exemplo busca a filial de origem `20`, em 1 de
setembro de 2026, com dez linhas na primeira página:

```bash
curl -sS -X POST "$BASE/api/consulta" \
  -H 'Content-Type: application/json' \
  -H "Authorization: Bearer $JWT" \
  -d '{
    "operacao": "emissao_vendas_por_forma_pagamento",
    "servidor_ordem": "'"$SERVIDOR"'",
    "parametros": {
      "CodigoFilial": 20,
      "inicio": "2026-09-01",
      "fim": "2026-09-01"
    },
    "pagina": 0,
    "tamanho_pagina": 10,
    "incluir_total": true,
    "ordenar_por": "sequencia",
    "direcao": "asc"
  }' | python3 -m json.tool
```

Troque `CodigoFilial`, `inicio` e `fim` pelos dados desejados. A operação
aceita no máximo 92 dias por consulta e até 200 linhas por página. O campo
`total_linhas` pode ser maior que 10.000 quando há muitas páginas; reduza o
período antes de percorrer uma fila muito grande. Para a próxima página,
aumente `pagina` para `1`, `2` e assim por diante. Os valores aceitos em `ordenar_por` são `sequencia` e
`data_venda`; use `asc` ou `desc` em `direcao`.

Uma resposta válida traz `linhas` e, quando `incluir_total` é `true`, o campo
`total`. Se receber `503`, confirme que o serviço do agente está ativo e
conectado. Se receber `404` para a operação, confira se os arquivos da versão
23 estão na pasta `CATALOGO_DIR` do backend e se o agente aceitou a versão 23.
