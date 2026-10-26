using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace AgenteShop9.Operacoes;

/// <summary>
/// Seleciona a primeira foto comercial cujo Base64 e imagem PNG/JPEG foram
/// decodificados integralmente. O agente roda em Windows; este validador usa
/// GDI+ somente para a preparacao do indice local da vitrine publica.
/// </summary>
public static class IndiceFotosPublicas
{
    public const int MaxCaracteres = 1_000_000;
    public const int MaxBytes = 750_000;
    public const int MaxPixels = 4_000_000;
    public const int MaxDimensao = 4_096;

    public static Dictionary<string, JsonElement> ParametrosEscopo(
        IReadOnlyDictionary<string, JsonElement> recebidos)
    {
        var parametros = new Dictionary<string, JsonElement>();
        foreach (var nome in new[] { "CodigoFilial" }.Concat(
                     Enumerable.Range(1, 8).Select(i => $"Tabela_{i:00}")))
            if (recebidos.TryGetValue(nome, out var valor))
                parametros[nome] = valor;
        return parametros;
    }

    public static string ChaveEscopo(int versao,
                                    IReadOnlyDictionary<string, JsonElement> parametros)
    {
        if (!parametros.TryGetValue("CodigoFilial", out var filial)
            || filial.ValueKind != JsonValueKind.Number || !filial.TryGetInt32(out var codigo)
            || codigo < 1)
            throw new ArgumentException("Filial do indice de fotos invalida.");
        var tabelas = new List<int>();
        for (var i = 1; i <= 8; i++)
        {
            if (!parametros.TryGetValue($"Tabela_{i:00}", out var tabela)
                || tabela.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;
            if (tabela.ValueKind != JsonValueKind.Number || !tabela.TryGetInt32(out var ordem)
                || ordem < 1)
                throw new ArgumentException("Tabela do indice de fotos invalida.");
            tabelas.Add(ordem);
        }
        if (tabelas.Count == 0 || tabelas.Count != tabelas.Distinct().Count())
            throw new ArgumentException("Conjunto de tabelas do indice de fotos invalido.");
        return $"{versao}:{codigo}:{string.Join(",", tabelas)}";
    }

    public static bool FotoValida(string? texto)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            throw new PlatformNotSupportedException("Validacao de foto do agente requer Windows.");
        if (string.IsNullOrEmpty(texto) || texto.Length > MaxCaracteres
            || texto.Length % 4 != 0 || texto.Any(char.IsWhiteSpace))
            return false;
        try
        {
            var dados = Convert.FromBase64String(texto);
            if (dados.Length is < 1 or > MaxBytes)
                return false;
            using var fluxo = new MemoryStream(dados, writable: false);
            using var imagem = Image.FromStream(fluxo, useEmbeddedColorManagement: false,
                                                validateImageData: false);
            if (imagem.RawFormat.Guid != ImageFormat.Png.Guid
                && imagem.RawFormat.Guid != ImageFormat.Jpeg.Guid)
                return false;
            if (imagem.Width < 1 || imagem.Height < 1
                || imagem.Width > MaxDimensao || imagem.Height > MaxDimensao
                || (long)imagem.Width * imagem.Height > MaxPixels)
                return false;
            // Primeiro limitar dimensoes; depois validar os dados e converter
            // todos os pixels antes de incluir a foto no indice comercial.
            using var fluxoCompleto = new MemoryStream(dados, writable: false);
            using var imagemCompleta = Image.FromStream(
                fluxoCompleto, useEmbeddedColorManagement: false, validateImageData: true);
            using var bitmap = new Bitmap(imagemCompleta);
            var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var pixels = bitmap.LockBits(area, ImageLockMode.ReadOnly,
                                         PixelFormat.Format32bppArgb);
            bitmap.UnlockBits(pixels);
            return true;
        }
        catch (Exception erro) when (erro is FormatException or ArgumentException
                                      or OutOfMemoryException
                                      or System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    public static Dictionary<int, int> SelecionarPrimeirasValidas(
        IEnumerable<(int ProdutoOrdem, int ImagemOrdem, string? FotoBase64)> fotos,
        Func<string?, bool> validar)
    {
        var selecionadas = new Dictionary<int, int>();
        foreach (var (produtoOrdem, imagemOrdem, fotoBase64) in fotos)
            TentarIncluir(selecionadas, produtoOrdem, imagemOrdem, fotoBase64, validar);
        return selecionadas;
    }

    public static void TentarIncluir(Dictionary<int, int> selecionadas, int produtoOrdem,
                                    int imagemOrdem, string? fotoBase64,
                                    Func<string?, bool> validar)
    {
        if (produtoOrdem < 1 || imagemOrdem < 1 || selecionadas.ContainsKey(produtoOrdem))
            return;
        if (validar(fotoBase64))
            selecionadas.Add(produtoOrdem, imagemOrdem);
    }
}

public sealed class IndiceFotosCapacidadeException(
    string motivo, int linhasLidas, long caracteresLidos) : Exception
{
    public string Motivo { get; } = motivo;
    public int LinhasLidas { get; } = linhasLidas;
    public long CaracteresLidos { get; } = caracteresLidos;
}

public sealed class IndiceFotosTempoException(
    int linhasLidas, long caracteresLidos) : TimeoutException
{
    public int LinhasLidas { get; } = linhasLidas;
    public long CaracteresLidos { get; } = caracteresLidos;
}

/// <summary>Consome somente as linhas da consulta ja limitada ao escopo da vitrine.</summary>
public sealed class AcumuladorIndiceFotosPublicas
{
    public const int MaxLinhas = 50_000;
    public const long MaxCaracteres = 128L * 1024 * 1024;
    private int _linhas;
    private int _produtoAnterior;
    private int _fotosDoProduto;
    private long _caracteres;

    public Dictionary<int, int> Fotos { get; } = new();
    public int LinhasLidas => _linhas;
    public long CaracteresLidos => _caracteres;

    public bool PrecisaLerFoto(int produtoOrdem) => !Fotos.ContainsKey(produtoOrdem);

    public void Incluir(int produtoOrdem, int imagemOrdem, long caracteres,
                        string? fotoBase64, Func<string?, bool> validar)
    {
        if (++_linhas > MaxLinhas)
            throw new IndiceFotosCapacidadeException("linhas", _linhas, _caracteres);
        if (produtoOrdem != _produtoAnterior)
        {
            _produtoAnterior = produtoOrdem;
            _fotosDoProduto = 0;
        }
        if (++_fotosDoProduto > 8)
            throw new IndiceFotosCapacidadeException("fotos_por_produto", _linhas, _caracteres);
        if (caracteres < 1 || caracteres > IndiceFotosPublicas.MaxCaracteres)
            throw new IndiceFotosCapacidadeException("tamanho_foto", _linhas, _caracteres);
        _caracteres += caracteres;
        if (_caracteres > MaxCaracteres)
            throw new IndiceFotosCapacidadeException("volume_texto", _linhas, _caracteres);
        IndiceFotosPublicas.TentarIncluir(
            Fotos, produtoOrdem, imagemOrdem, fotoBase64, validar);
    }
}
