namespace AgenteShop9.Configuracao;

/// <summary>Decide de onde ler o config.json.</summary>
public static class CaminhoConfiguracao
{
    /// <summary>Local padrao no servidor do cliente (spec 3.5).</summary>
    public const string PadraoWindows = @"C:\ProgramData\AgenteShop9\config.json";

    /// <summary>
    /// Ordem: argumento --config &lt;caminho&gt;, depois a variavel de ambiente
    /// AGENTE_SHOP9_CONFIG, depois o padrao do Windows. Em dev (fora do Windows),
    /// sempre passar --config.
    /// </summary>
    public static string Resolver(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] is "--config" or "-c")
                return args[i + 1];

        var doAmbiente = Environment.GetEnvironmentVariable("AGENTE_SHOP9_CONFIG");
        return string.IsNullOrWhiteSpace(doAmbiente) ? PadraoWindows : doAmbiente;
    }
}
