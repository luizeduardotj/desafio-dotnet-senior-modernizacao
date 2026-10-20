using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Nfse.Fachada.Sombra.Reproducao;

/// <summary>
/// Seção <c>Sombra:Reproducao</c>: pedido original cifrado para reproduzir uma divergência com exatidão (ADR-0009,
/// seção 4; ADR-0012). Desligado por padrão.
/// </summary>
/// <remarks>
/// A fachada só conhece a chave <b>atual</b> (<see cref="ChaveId"/> e <see cref="Chave"/>, de segredo). As chaves
/// anteriores, necessárias para ler arquivos ainda dentro da retenção depois de uma rotação, ficam só com a ferramenta
/// de reprodução (<see cref="ChavesAnteriores"/>), nunca no deployment da fachada.
/// </remarks>
public sealed partial class OpcoesReproducao : IValidatableObject
{
    public const string Secao = "Sombra:Reproducao";

    /// <summary>Tamanho da chave AES-256.</summary>
    public const int TamanhoDaChave = 32;

    public bool Habilitada { get; set; }

    /// <summary>Volume só da fachada (ou do job da ferramenta), fora de imagem e de log. Obrigatório se habilitada.</summary>
    public string? Diretorio { get; set; }

    /// <summary>Identificador da chave atual, gravado em claro em cada arquivo (ex.: <c>k2026-10</c>).</summary>
    public string? ChaveId { get; set; }

    /// <summary>Chave AES-256 atual, em base64 (<c>Sombra__Reproducao__Chave</c>, de segredo).</summary>
    public string? Chave { get; set; }

    /// <summary>
    /// Só na ferramenta: chaves rotacionadas ainda dentro da retenção, por identificador
    /// (<c>Sombra__Reproducao__ChavesAnteriores__k2026-09</c>).
    /// </summary>
    public Dictionary<string, string> ChavesAnteriores { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Idade máxima de um arquivo; a limpeza apaga o que passar disso (ADR-0009: 15 dias).</summary>
    public TimeSpan Retencao { get; set; } = TimeSpan.FromDays(15);

    /// <summary>Intervalo entre duas passadas da limpeza.</summary>
    public TimeSpan IntervaloDaLimpeza { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Arquivos gravados por segundo, por operação: um novo com defeito não enche o disco.</summary>
    [Range(0, 1000)]
    public double PorSegundo { get; set; } = 1;

    /// <summary>Chave atual decodificada; só chamar depois da validação.</summary>
    public byte[] BytesDaChave() => Convert.FromBase64String(Chave!);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Habilitada)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(Diretorio))
        {
            yield return new("Sombra:Reproducao:Diretorio é obrigatório com a reprodução habilitada.");
        }

        if (ChaveId is null || !IdentificadorValido().IsMatch(ChaveId))
        {
            yield return new("Sombra:Reproducao:ChaveId deve ter de 1 a 32 caracteres [A-Za-z0-9_-].");
        }

        // A mensagem nunca cita o valor: é segredo.
        if (!ChaveValida(Chave))
        {
            yield return new($"Sombra:Reproducao:Chave deve ser base64 de {TamanhoDaChave} bytes (AES-256).");
        }

        if (Retencao <= TimeSpan.Zero || IntervaloDaLimpeza <= TimeSpan.Zero)
        {
            yield return new("Sombra:Reproducao:Retencao e IntervaloDaLimpeza devem ser positivos.");
        }
    }

    internal static bool ChaveValida(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return false;
        }

        Span<byte> destino = stackalloc byte[TamanhoDaChave + 3];
        return Convert.TryFromBase64String(base64, destino, out int lidos) && lidos == TamanhoDaChave;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    internal static partial Regex IdentificadorValido();
}
