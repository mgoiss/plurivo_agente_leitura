namespace AgenteShop9.Testes;

/// <summary>Acesso ao catálogo de exemplo já assinado (assinatura/exemplos/).</summary>
internal static class Fixtures
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static byte[] CatalogoBytes => File.ReadAllBytes(Path.Combine(Dir, "catalogo.json"));
    public static string CatalogoMinisig => File.ReadAllText(Path.Combine(Dir, "catalogo.json.minisig"));
}
