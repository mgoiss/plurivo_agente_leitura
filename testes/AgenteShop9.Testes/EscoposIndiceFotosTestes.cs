using System.Text.Json;
using AgenteShop9.Operacoes;

namespace AgenteShop9.Testes;

public class EscoposIndiceFotosTestes
{
    private static Dictionary<string, JsonElement> Escopo(int filial, params int[] tabelas)
    {
        var d = new Dictionary<string, JsonElement>
        {
            ["CodigoFilial"] = JsonSerializer.SerializeToElement(filial),
        };
        for (var i = 0; i < tabelas.Length; i++)
            d[$"Tabela_{i + 1:00}"] = JsonSerializer.SerializeToElement(tabelas[i]);
        return d;
    }

    [Fact]
    public void Guarda_escopos_em_disco_sem_duplicar_e_relê_na_proxima_subida()
    {
        var pasta = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var caminho = Path.Combine(pasta, "dados", "escopos.json");
        try
        {
            var a = new EscoposIndiceFotos(caminho);
            Assert.True(a.Registrar(Escopo(2, 7)));
            Assert.False(a.Registrar(Escopo(2, 7)));
            Assert.True(a.Registrar(Escopo(3, 7, 8)));

            var b = new EscoposIndiceFotos(caminho).Listar();
            Assert.Equal(2, b.Count);
            Assert.Equal("1:2:7", Chave(b[0]));
            Assert.Equal("1:3:7,8", Chave(b[1]));
        }
        finally
        {
            if (Directory.Exists(pasta)) Directory.Delete(pasta, true);
        }
    }

    [Fact]
    public void Arquivo_corrompido_vira_lista_vazia()
    {
        var caminho = Path.GetTempFileName();
        try
        {
            File.WriteAllText(caminho, "{ nao e json");
            Assert.Empty(new EscoposIndiceFotos(caminho).Listar());
        }
        finally { File.Delete(caminho); }
    }

    [Fact]
    public void Mantem_so_os_escopos_mais_recentes()
    {
        var caminho = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var e = new EscoposIndiceFotos(caminho, maximo: 2);
            e.Registrar(Escopo(1, 7));
            e.Registrar(Escopo(2, 7));
            e.Registrar(Escopo(3, 7));
            Assert.Equal(2, e.Listar().Count);
            Assert.Equal("1:2:7", Chave(e.Listar()[0]));
        }
        finally { File.Delete(caminho); }
    }

    private static string Chave(Dictionary<string, JsonElement> p) =>
        IndiceFotosPublicas.ChaveEscopo(1, p);
}
