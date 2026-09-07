using System.Text.Json;
using AgenteShop9.Mensagens;

namespace AgenteShop9.Testes;

public class HeartbeatTestes
{
    [Fact]
    public void Serializa_no_formato_da_spec()
    {
        var json = JsonSerializer.Serialize(new Heartbeat { VersaoAgente = "1.2.3" });

        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;

        Assert.Equal("heartbeat", raiz.GetProperty("tipo").GetString());
        Assert.Equal("1.2.3", raiz.GetProperty("versao_agente").GetString());
        Assert.Equal(0, raiz.GetProperty("versao_catalogo").GetInt32());
    }

    [Fact]
    public void Nao_carrega_tenant_id()
    {
        var json = JsonSerializer.Serialize(new Heartbeat { VersaoAgente = "1.0.0" });

        Assert.DoesNotContain("tenant", json, StringComparison.OrdinalIgnoreCase);
    }
}
