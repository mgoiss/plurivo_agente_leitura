namespace AgenteShop9.Configuracao;

/// <summary>
/// Conteudo do config.json da maquina do cliente (spec 3.5).
///
/// NAO tem tenant_id de proposito: o robo nunca informa o proprio tenant. Quem
/// decide de qual loja o robo e o backend, pelo hash do token_agente. Se o
/// config trouxer um tenant_id, ele e ignorado (ver LeitorConfiguracao).
/// </summary>
public sealed record ConfiguracaoAgente
{
    /// <summary>Connection string do SQL Server local. So usada a partir do item 1.4.</summary>
    public required string ConexaoSql { get; init; }

    /// <summary>Endereco WebSocket do backend, ex.: wss://api.suaplataforma.com.br/agente.</summary>
    public required string BackendUrl { get; init; }

    /// <summary>Segredo unico deste cliente. Vai no cabecalho Authorization do handshake.</summary>
    public required string TokenAgente { get; init; }

    /// <summary>
    /// Caminho do catalogo.json candidato local (o .minisig e assumido ao lado,
    /// com sufixo .minisig). Opcional no config; default: catalogo.json na mesma
    /// pasta do proprio config. Baixar do backend e o item 4.1.
    /// </summary>
    public required string CatalogoCaminho { get; init; }
}
