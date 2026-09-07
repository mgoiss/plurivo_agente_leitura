using System.Reflection;

namespace AgenteShop9;

/// <summary>Versao do robo, para ir no heartbeat.</summary>
public static class VersaoAgente
{
    public static string Atual { get; } = Resolver();

    private static string Resolver()
    {
        var asm = Assembly.GetExecutingAssembly();
        var informacional = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var bruta = informacional ?? asm.GetName().Version?.ToString() ?? "0.0.0";
        // remove sufixo de metadados de build (ex.: "1.0.0+abc1234")
        var mais = bruta.IndexOf('+');
        return mais >= 0 ? bruta[..mais] : bruta;
    }
}
