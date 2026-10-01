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
