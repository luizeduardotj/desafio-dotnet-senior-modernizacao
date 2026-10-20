using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Flags;

public enum ModoDeMigracao
{
    Legado,
    Sombra,
    Novo,
}

/// <summary>Retrato das flags de migração (seção <c>Migracao</c> do arquivo de flags). Ver README da fachada.</summary>
public sealed class FlagsDeMigracao
{
    public const string Secao = "Migracao";

    /// <summary>Kill switch nível 1, global.</summary>
    public bool KillSwitch { get; set; }

    /// <summary>Por operação; operação ausente fica em <c>legado</c>.</summary>
    public Dictionary<string, FlagsDaOperacao> Operacoes { get; set; } = new(StringComparer.Ordinal);

    public FlagsDaOperacao Da(string operacao) =>
        Operacoes.TryGetValue(operacao, out FlagsDaOperacao? op) ? op : FlagsDaOperacao.Legado;

    /// <summary>Erros de validação; vazio quando o retrato é utilizável.</summary>
    public IEnumerable<string> Validar()
    {
        foreach ((string nome, FlagsDaOperacao op) in Operacoes)
        {
            if (op.Percentual is < 0 or > 100)
            {
                yield return $"{nome}: Percentual fora de 0..100";
            }

            if (op.Sombra.Amostragem is < 0 or > 100)
            {
                yield return $"{nome}: Sombra.Amostragem fora de 0..100";
            }

            // Sombra invertida repetiria no legado o que o novo já fez. Para o cancelamento seria escrita dupla
            // (ADR-0009, seção 5): o retrato inteiro é recusado e tudo vai ao legado (fail-safe), em qualquer modo.
            if (op.Sombra.Invertida && string.Equals(nome, OperacoesSoap.CancelarNfse, StringComparison.Ordinal))
            {
                yield return $"{nome}: Sombra.Invertida proibida (o legado cancelaria de novo)";
            }
        }
    }
}

public sealed class FlagsDaOperacao
{
    internal static readonly FlagsDaOperacao Legado = new();

    public ModoDeMigracao Modo { get; set; } = ModoDeMigracao.Legado;

    /// <summary>Kill switch nível 1 da operação.</summary>
    public bool KillSwitch { get; set; }

    /// <summary>Em <c>novo</c>: fração (0..100) atendida pelo novo. Ignorado nos outros modos.</summary>
    public int Percentual { get; set; }

    /// <summary>Chaves (IM ou documento só com dígitos) que vão ao novo em <c>novo</c> ou são copiadas em <c>sombra</c>.</summary>
#pragma warning disable CA1819 // lido do arquivo de flags pelo binder de configuração
    public string[] Inclusao { get; set; } = [];

    /// <summary>Chaves que ficam sempre no legado.</summary>
    public string[] Exclusao { get; set; } = [];
#pragma warning restore CA1819

    public FlagsDaSombra Sombra { get; set; } = new();
}

public sealed class FlagsDaSombra
{
    /// <summary>
    /// Fração (0..100) das requisições copiadas: em <c>sombra</c>, as atendidas pelo legado e repetidas no novo; com
    /// <see cref="Invertida"/>, as atendidas pelo novo e repetidas no legado.
    /// </summary>
    public int Amostragem { get; set; }

    /// <summary>
    /// Sombra invertida (ADR-0009, fase 2): em <c>novo</c>, o novo responde ao cliente e a leitura é repetida no legado
    /// para comparação, como rede de segurança no M3/M4. Só leituras: em <c>CancelarNfse</c> o retrato é inválido.
    /// Ignorada fora de <c>novo</c>.
    /// </summary>
    public bool Invertida { get; set; }
}
