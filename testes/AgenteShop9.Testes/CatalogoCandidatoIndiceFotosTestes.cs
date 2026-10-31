using System.Text.RegularExpressions;
using AgenteShop9.Catalogos;
using AgenteShop9.Operacoes;

namespace AgenteShop9.Testes;

/// <summary>
/// Candidato v36 da issue #119: impressao digital barata do indice de fotos.
/// Nao assina nem publica nada — so confere o arquivo candidato.
/// </summary>
public class CatalogoCandidatoIndiceFotosTestes
{
    private const string Nome = "catalogo_indice_fotos_impressao";

    private static Catalogo Candidato() => Catalogo.Analisar(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "catalogo.candidato-indice-fotos.json")));

    [Fact]
    public void Versao_e_36()
    {
        Assert.Equal(36, Candidato().VersaoCatalogo);
    }

    [Fact]
    public void Operacao_e_interna_select_puro_e_nunca_le_o_blob()
    {
        var op = Candidato().Operacoes![Nome];
        Assert.True(op.UsoInterno);
        Assert.False(op.UsaFotosValidadas);
        Assert.Null(op.Ordenacao);
        Assert.StartsWith("SELECT COUNT(*)", op.Sql);
        foreach (var proibido in new[] { "INSERT ", "UPDATE ", "DELETE ", "EXEC", "INTO ", "DROP ", ";", "ORDER BY" })
            Assert.DoesNotContain(proibido, op.Sql, StringComparison.OrdinalIgnoreCase);
        // Foto so aparece dentro de DATALENGTH.
        var referencias = Regex.Matches(op.Sql, @"\bfoto\.Foto\b").Count;
        var seguras = Regex.Matches(op.Sql, @"DATALENGTH\(foto\.Foto\b").Count;
        Assert.Equal(referencias, seguras);
    }

    [Fact]
    public void Usa_o_mesmo_escopo_e_os_mesmos_parametros_do_indice_completo()
    {
        var ops = Candidato().Operacoes!;
        var indice = ops["catalogo_indice_fotos"];
        var impressao = ops[Nome];
        Assert.Equal(indice.Parametros, impressao.Parametros);
        // Da primeira linha do FROM em diante o texto e identico, exceto o ORDER BY.
        string Escopo(string sql)
        {
            var de = sql.IndexOf("FROM Prod_Serv AS prod", StringComparison.Ordinal);
            var ate = sql.IndexOf("\nORDER BY", StringComparison.Ordinal);
            return (ate < 0 ? sql[de..] : sql[de..ate]).Trim();
        }
        Assert.Equal(Escopo(indice.Sql), Escopo(impressao.Sql));
    }

    [Fact]
    public void Mantem_todas_as_operacoes_do_candidato_33_e_acrescenta_a_impressao()
    {
        var novas = Candidato().Operacoes!;
        var antigas = Catalogo.Analisar(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "catalogo.candidato-aceleracao-imagens.json"))).Operacoes!;
        foreach (var (nome, op) in antigas)
        {
            // As operacoes da v34/v35 (filtro de variacao) mudaram o SQL de algumas;
            // aqui so se garante que nenhuma operacao sumiu.
            Assert.Contains(nome, novas.Keys);
        }
        Assert.Contains(Nome, novas.Keys);
    }
}
