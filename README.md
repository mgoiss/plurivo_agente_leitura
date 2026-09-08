# Agente local (robô) — shop_plus

O "robô": serviço que roda no servidor de cada loja, ao lado do Shop9. Conecta no
backend por WebSocket, manda sinal de vida e reconecta sozinho. **.NET 10 / C#.**

Fonte da verdade: `../especificacao/`. Este README cobre só como rodar e publicar.

## O que já existe (itens 1.2 e 1.3 do roteiro)

- lê o `config.json` (`conexao_sql`, `backend_url`, `token_agente`, e o opcional
  `catalogo_caminho` — **sem** tenant);
- conecta no `backend_url` **validando o certificado TLS** (nunca desligado);
- autentica com `Authorization: Bearer <token_agente>` no handshake;
- manda heartbeat a cada 30s, com o `versao_catalogo` aceito;
- reconecta sozinho com espera 1s → 2s → 4s → … → 60s, sem desistir;
- na subida, se houver um `catalogo.json` + `.minisig` no `catalogo_caminho`
  (default: ao lado do `config.json`), **verifica a assinatura minisign** com a
  chave pública embutida, confere o SHA-256, exige `versao=` == `versao_catalogo`,
  e só aceita se a versão for **maior** que a do catálogo já aceito
  (anti-retrocesso). O catálogo aceito fica em `<pasta>/dados/`.

**Ainda não:** executar operação no SQL Server (1.4), baixar o catálogo do
backend e auto-update do binário (4.1).

## Rodar em desenvolvimento (Mac / Linux / Windows)

```bash
cd agente
dotnet run --project src/AgenteShop9 -- --config /caminho/para/config.json
```

Fora do Windows ele roda como app de console. No Windows, roda como serviço quando
instalado como tal (abaixo).

## Testes

Não precisam de rede externa — sobem um servidor WebSocket local de teste.

```bash
cd agente
dotnet test
```

## Publicar o .exe para o servidor Windows do cliente

Um arquivo só, sem instalar .NET no cliente:

```bash
cd agente
dotnet publish src/AgenteShop9 -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Sai em `src/AgenteShop9/bin/Release/net10.0/win-x64/publish/AgenteShop9.exe` (~72 MB —
carrega o motor do .NET dentro).

## Instalar como serviço no Windows (uma vez por cliente)

Deixe o `.exe` e o `config.json` **juntos, numa pasta sem espaço no caminho**
(ex.: `C:\AgenteShop9\`). Espaço em "C:\Program Files\..." complica o `sc.exe`.

Crie o `config.json` pelo PowerShell — o Bloco de Notas salva como
`config.json.txt` quando as extensões estão ocultas:

```powershell
New-Item -ItemType Directory -Force -Path C:\AgenteShop9 | Out-Null
@'
{
  "conexao_sql": "Server=localhost;Database=Shop9;Encrypt=True;TrustServerCertificate=True;Connection Timeout=15",
  "backend_url": "wss://api.suaplataforma.com.br/agente",
  "token_agente": "o-token-do-cliente"
}
'@ | Set-Content -Path C:\AgenteShop9\config.json -Encoding UTF8
```

Instale apontando o config **explicitamente** (PowerShell como Administrador):

```powershell
sc.exe create AgenteShop9 binPath= "C:\AgenteShop9\AgenteShop9.exe --config C:\AgenteShop9\config.json" start= auto DisplayName= "Agente Shop9"
sc.exe start AgenteShop9
sc.exe query AgenteShop9
```

`STATE : 4  RUNNING` = ok. Logs em: Visualizador de Eventos → Logs do Windows →
Aplicativo → origem **AgenteShop9** (a 1ª linha registra de onde o config foi lido).

Remover: `sc.exe stop AgenteShop9` e depois `sc.exe delete AgenteShop9`.

O caminho padrão, se `--config` não for passado, é
`C:\ProgramData\AgenteShop9\config.json`; dá também para apontar pela variável de
ambiente `AGENTE_SHOP9_CONFIG`.
