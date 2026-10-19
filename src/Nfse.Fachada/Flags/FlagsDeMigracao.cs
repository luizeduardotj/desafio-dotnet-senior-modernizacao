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
    /// <summary>Fração (0..100) das requisições elegíveis copiadas para o novo. A sombra ainda não está implementada.</summary>
    public int Amostragem { get; set; }
}
