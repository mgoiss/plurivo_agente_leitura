namespace AgenteShop9.Catalogos;

/// <summary>
/// Campos do "trusted comment" do minisign, montados pelo assinar.sh:
/// "arquivo=... sha256=... data=... versao=...". Valores nao contem espaco.
/// </summary>
public sealed record ComentarioConfiavel
{
    public string? Arquivo { get; init; }
    public string? Sha256 { get; init; }
    public string? Data { get; init; }
    public int? Versao { get; init; }

    public static ComentarioConfiavel Analisar(string comentario)
    {
        string? arquivo = null, sha256 = null, data = null;
        int? versao = null;

        foreach (var parte in comentario.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = parte.IndexOf('=');
            if (i <= 0) continue;
            var chave = parte[..i];
            var valor = parte[(i + 1)..];
            switch (chave)
            {
                case "arquivo": arquivo = valor; break;
                case "sha256": sha256 = valor; break;
                case "data": data = valor; break;
                case "versao": if (int.TryParse(valor, out var v)) versao = v; break;
            }
        }

        return new ComentarioConfiavel { Arquivo = arquivo, Sha256 = sha256, Data = data, Versao = versao };
    }
}
