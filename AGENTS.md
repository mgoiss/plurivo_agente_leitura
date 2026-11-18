# Agente de leitura — Plurivo

Serviço Windows (.NET 10 / C#) que roda no servidor de cada cliente, ao lado do
Shop9. Conecta no back_end por WebSocket, aceita só catálogo assinado e executa
o SQL parametrizado do catálogo no SQL Server local. O catálogo em uso fica em
`catalogo/`.

Parte do Plurivo. Produto, contrato geral e fluxo: `../../AGENTS.md` (raiz).
Issues ficam na raiz; PR daqui fecha com `Fixes <dono>/plurivo#N`. Fonte da
verdade de arquitetura e segurança, na raiz:
`documentacao/especificacoes/plataforma/Especificacao_Plataforma_Shop9_v2.md`.
Assinatura e chave pública: `../../ferramentas/assinatura/`.

## NUNCA alterar sem me perguntar

- **Agente nunca recebe SQL.** O backend manda nome de operação + parâmetros;
  o SQL parametrizado vive no `catalogo.json`. Parâmetro só entra como valor,
  nunca como nome de tabela/coluna/`ORDER BY`/`TOP`. Spec 3.3, 6.2.
- **Chave privada de assinatura nunca no backend, na VPS ou no repo.**
  `catalogo.json` e o binário do agente são assinados localmente; o backend só
  distribui o já assinado, e o agente verifica com a chave pública embutida.
  Anti-retrocesso: recusar `versao_catalogo` menor ou igual à atual. Spec
  3.3.1, 3.7.
- **Vendas do catálogo leem `Movimento` pelo índice nomeado (issue #125), agora só no índice de vendas
  (issue #179).** No catálogo v43 o trecho de vendas saiu de `catalogo_produtos_ids` e `catalogo_inicio`
  (juntam com `#VendasIndice`) e vive em `catalogo_vendas_indice` (uso_interno, timeout 30000 ms), com
  `FROM Movimento AS mov WITH (INDEX (CI_Filial_Estoque_CliFor))` e janela fixa de 59 dias calculada no SQL.
  Sem a dica o SQL Server escolhe `IX_Movimento_1` com key lookup (567 mil leituras) e a consulta passa de
  1,7 s. Não remover a dica. O índice é nativo do Shop9: conferir que existe em todo cliente novo antes de
  ligar a vitrine (`SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID('Movimento') AND name =
  'CI_Filial_Estoque_CliFor'`); sem ele a consulta falha com o erro 308. Nunca criar índice no banco do
  cliente por conta própria. `catalogo_relacionados` continua lendo `Movimento` direto, FORA do índice
  (decidido na #179, CA2 da especificação: calcula coocorrência de produtos, não soma por produto).
- **Índice de vendas: cache por filial, chave SEM versão do catálogo (issue #179, ADR 0010).**
  `CacheIndiceVendas` guarda `(Produto_Ordem, Quantidade)` por `OrdemFilial`, renova a cada ~10 min em
  segundo plano (stale-while-revalidate), uma construção por vez, recuo de 60 s após falha mantendo o
  índice antigo. A chave é só a filial: diferente do índice de fotos (`CacheIndiceFotos`, chave com
  `VersaoCatalogo`), um catálogo novo NÃO pode zerar o índice de vendas. Sem índice pronto a requisição
  NÃO espera: dispara a construção e responde `ranking_indisponivel` (código estável usado pelo broker
  para cair na ordem alfabética; não mudar). Exceção: operação com `vendas_indice_opcional` (hoje
  `catalogo_inicio`) NÃO falha, cria `#VendasIndice` vazia, dispara a construção e segue (ordem pelo
  desempate da consulta); só `catalogo_produtos_ids` devolve `ranking_indisponivel`. Só operações com
  `usa_vendas_indice` dependem dele;
  `catalogo_produtos_ids_sem_vendas` (nome/preço) não. Escopos vistos ficam em
  `dados/escopos_indice_vendas.json` para o aquecimento no boot.
- **Índice de fotos: cache de validação por foto (issue #182).** No catálogo v44, `catalogo_indice_fotos_metadados`
  (sem o Base64) + `catalogo_foto_base64` (uma foto por `OrdemFoto`) fazem a reconstrução ler só as fotos novas;
  o resultado fica em `dados/validacao_fotos.json` (`CacheValidacaoFotos`: Ordem da foto -> tamanho, válida,
  validadoEm; vale 24 h, global, não por escopo; foto removida sai do cache só depois de vencer, nunca podar por escopo). Catálogo v43 cai no `catalogo_indice_fotos` antigo (lê tudo).
  Cada reconstrução loga "Indice de fotos construido: N fotos no escopo, L lidas do banco, C do cache...".
  Nunca criar índice no banco do cliente para isso.
- **Validação de certificado TLS no agente é obrigatória.** Proibido desativar,
  mesmo em teste — usar certificado de teste válido. Spec 3.6.

## Validacao (contrato de validacao) — LEIA ANTES DE DIZER QUE ESTA PRONTO
- **Quem valida, e onde:** parse e verificação de catálogo/assinatura →
  `dotnet test` aqui (fixtures em `testes/fixtures/`). Conexão com SQL Server,
  serviço Windows e caminho ponta a ponta → eu, no Windows (homologação, depois
  cliente piloto).
- **O que o agente não alcança:** Windows, SQL Server e Shop9.
- **Ação irreversível:** publicar `catalogo.json` ou binário assinado (todos os
  agentes puxam sozinhos). Assinar e publicar: só eu.
- **O que os testes NÃO cobrem:** SQL Server real, schema real do Shop9,
  ciclo de vida do serviço Windows (auto-start, reconexão, DPAPI), TLS contra o
  backend real, auto-update num cliente real.
- Nunca afirme que funciona. Diga o que mudou e o que preciso verificar.

## Fluxo e conhecimento compartilhado
- Ver `../../AGENTS.md`. Descoberta durável → `../../vault/_inbox.md`, campo
  <projeto> = plurivo.

- **Publicar o agente com `-p:IncludeNativeLibrariesForSelfExtract=true`.** Sem a flag o `.exe`
  sai sem a biblioteca nativa do SQL Server (`DllNotFoundException` em `SNIInitialize`) e todas
  as consultas dão `agente_indisponivel`. Hash batendo não prova que funciona: abrir o agente e
  rodar uma consulta antes de publicar.
