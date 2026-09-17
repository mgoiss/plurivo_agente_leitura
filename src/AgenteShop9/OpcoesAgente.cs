namespace AgenteShop9;

/// <summary>
/// Tempos do robo. Os padroes sao os da spec (3.4 e 3.6); os testes passam
/// valores curtos para nao esperar de verdade.
/// </summary>
public sealed record OpcoesAgente
{
    public TimeSpan IntervaloHeartbeat { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReconexaoEsperaInicial { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconexaoEsperaMaxima { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Item 4.1: intervalo entre verificacoes de catalogo novo no backend.</summary>
    public TimeSpan IntervaloVerificacaoCatalogo { get; init; } = TimeSpan.FromMinutes(15);
}
