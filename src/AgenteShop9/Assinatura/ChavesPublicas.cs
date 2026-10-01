namespace AgenteShop9.Assinatura;

/// <summary>
/// Chave(s) publica(s) minisign aceitas pelo agente, embutidas no binario
/// (2a linha do arquivo .pub — ver ferramentas/assinatura/chaves/shop9-agente.pub na raiz Plurivo). Nunca
/// lidas de um arquivo ao lado do executavel.
///
/// E uma lista para permitir "chave atual + proxima" numa rotacao futura
/// (ferramentas/assinatura/docs/ASSINATURA.md secao 5, raiz Plurivo) sem reescrever a verificacao.
/// </summary>
public static class ChavesPublicas
{
    // key ID 160AA9FCF7537783 — gerada em 2026-09-07
    public static readonly string[] Aceitas =
    [
        "RWSDd1P3/KkKFkw+Xwc4BLR/GZbr+7jaBZjsOyojiGkE1oSwUKyFhm4S",
    ];
}
