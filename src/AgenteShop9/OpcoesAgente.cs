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
}
