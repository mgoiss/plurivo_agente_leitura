using System.Text.Json;
using AgenteShop9.Catalogos;

namespace AgenteShop9.Testes;

/// <summary>
/// Candidato da issue #178 (cartao x venda): o financeiro acha a venda pela chave
/// (Ordem_Pai = Ordem_Financeiro), nao pelo numero da venda, que se repete entre filiais e anos.
/// Nao assina nem publica nada: so confere o arquivo candidato, sem SQL Server.
/// </summary>
public class CatalogoCandidatoCartaoPorChaveTestes
{
    private const string Operacao = "financeiro_recebimentos_cartao_pix_em_aberto";

    private static Catalogo Ler(string arquivo) => Catalogo.Analisar(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "fixtures", arquivo)));

    private static Catalogo Candidato() => Ler("catalogo.candidato-cartao-por-chave.json");

    [Fact]
    public void Versao_ja_foi_absorvida_pelo_catalogo_vigente()
    {
        Assert.Equal(42, Candidato().VersaoCatalogo);
        Assert.True(Candidato().VersaoCatalogo <= Catalogo.Analisar(Fixtures.CatalogoRealBytes).VersaoCatalogo);
    }

    [Fact]
    public void Cartao_liga_venda_pela_chave_na_filial_pedida_e_nao_pelo_numero()
    {
        var op = Candidato().Operacoes![Operacao];
        foreach (var sql in new[] { op.Sql, op.SqlContagem })
        {
            Assert.Contains("movef.Ordem_Financeiro = fin.Ordem_Pai", sql);
            Assert.Contains("movef.Ordem_Filial = @OrdemFilial", sql);
            Assert.DoesNotContain("movef.Sequencia", sql);
        }
    }
}
