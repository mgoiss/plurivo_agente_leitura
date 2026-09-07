using AgenteShop9.Conexao;

namespace AgenteShop9.Testes;

public class PoliticaReconexaoTestes
{
    [Fact]
    public void Espera_dobra_e_para_em_60s()
    {
        var politica = new PoliticaReconexao();

        var sequencia = Enumerable.Range(0, 9)
            .Select(_ => politica.ProximaEspera().TotalSeconds)
            .ToArray();

        Assert.Equal(new double[] { 1, 2, 4, 8, 16, 32, 60, 60, 60 }, sequencia);
    }

    [Fact]
    public void Resetar_volta_para_a_espera_inicial()
    {
        var politica = new PoliticaReconexao();
        for (var i = 0; i < 5; i++) politica.ProximaEspera();

        politica.Resetar();

        Assert.Equal(1, politica.ProximaEspera().TotalSeconds);
    }

    [Fact]
    public void Aceita_tempos_customizados()
    {
        var politica = new PoliticaReconexao(
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40));

        var sequencia = Enumerable.Range(0, 4)
            .Select(_ => politica.ProximaEspera().TotalMilliseconds)
            .ToArray();

        Assert.Equal(new double[] { 10, 20, 40, 40 }, sequencia);
    }
}
