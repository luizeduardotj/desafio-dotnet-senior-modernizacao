using System.ComponentModel.DataAnnotations;

namespace Nfse.Fachada.Sombra;

/// <summary>Seção <c>Sombra</c>: limites do módulo de sombra (ADR-0009). A amostragem vem das flags, por operação.</summary>
public sealed class OpcoesSombra
{
    public const string Secao = "Sombra";

    /// <summary>Limite da resposta do legado capturada para comparação. Acima dele a cópia é descartada.</summary>
    public const int LimiteDaResposta = 1024 * 1024;

    /// <summary>Cópias aguardando um worker. Lido na partida; canal cheio descarta.</summary>
    [Range(1, 1_000_000)]
    public int CapacidadeDoCanal { get; set; } = 1000;

    /// <summary>Workers em paralelo, cada um com no máximo uma comparação em curso. Lido na partida.</summary>
    [Range(1, 1024)]
    public int Concorrencia { get; set; } = 4;

    /// <summary>Duração máxima de cada chamada da sombra (ao novo e, na reexecução, ao legado).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Reexecuções de divergência por segundo, por operação. Acima disso fica <c>nao_classificada</c>.</summary>
    [Range(0, 1000)]
    public double ReexecucoesPorSegundo { get; set; } = 1;

    /// <summary>
    /// Códigos de retorno do legado que vão sempre para a sombra, fora da amostragem (plano de migração, §5.4).
    /// Ausente: <see cref="ChavesRarasPadrao"/>. Sem valor inicial porque o binder acrescentaria a configuração a ele.
    /// </summary>
#pragma warning disable CA1819 // lido da configuração pelo binder
    public string[]? ChavesRaras { get; set; }
#pragma warning restore CA1819

    public static IReadOnlyList<string> ChavesRarasPadrao { get; } = ["E001", "E030", "E050", "E051", "E053"];

    public IReadOnlyList<string> ChavesRarasEfetivas => ChavesRaras ?? ChavesRarasPadrao;

    /// <summary>
    /// Chave do HMAC-SHA256 que pseudonimiza a requisição no registro da divergência (<c>Sombra__ChaveHmac</c>, de
    /// segredo; os bytes UTF-8 do texto). Vazia: o registro leva só os hashes das respostas, sem a requisição.
    /// </summary>
    public string? ChaveHmac { get; set; }
}
