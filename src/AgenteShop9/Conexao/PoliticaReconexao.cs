namespace AgenteShop9.Conexao;

/// <summary>
/// Quanto esperar antes de cada tentativa de reconexao: dobra a cada falha, com teto.
/// Spec 3.6: 1s, 2s, 4s, 8s ... ate no maximo 60s. Nunca desiste.
/// </summary>
public sealed class PoliticaReconexao
{
    private readonly double _inicialSeg;
    private readonly double _maximaSeg;
    private int _tentativa;

    public PoliticaReconexao(TimeSpan? esperaInicial = null, TimeSpan? esperaMaxima = null)
    {
        _inicialSeg = (esperaInicial ?? TimeSpan.FromSeconds(1)).TotalSeconds;
        _maximaSeg = (esperaMaxima ?? TimeSpan.FromSeconds(60)).TotalSeconds;
    }

    public TimeSpan ProximaEspera()
    {
        var segundos = Math.Min(_maximaSeg, _inicialSeg * Math.Pow(2, _tentativa));
        if (_tentativa < 1000) _tentativa++; // trava o expoente numa queda muito longa
        return TimeSpan.FromSeconds(segundos);
    }

    /// <summary>Chamar apos conectar: a proxima queda recomeca da espera inicial.</summary>
    public void Resetar() => _tentativa = 0;
}
