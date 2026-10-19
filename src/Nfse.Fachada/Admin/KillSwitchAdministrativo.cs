using System.Collections.Concurrent;

namespace Nfse.Fachada.Admin;

/// <summary>
/// Kill switch nível 2 (plano de migração, seção 8): estado em memória desta réplica, ligado pelo endpoint
/// administrativo. Vale na próxima requisição e independe do arquivo de flags.
/// </summary>
public sealed class KillSwitchAdministrativo
{
    private readonly ConcurrentDictionary<string, bool> _operacoes = new(StringComparer.Ordinal);
    private volatile bool _global;

    public bool Ativo(string operacao) => _global || _operacoes.ContainsKey(operacao);

    /// <param name="operacao"><c>null</c> para o global.</param>
    public void Ativar(string? operacao)
    {
        if (operacao is null)
        {
            _global = true;
        }
        else
        {
            _operacoes[operacao] = true;
        }
    }

    /// <param name="operacao"><c>null</c> desliga só o global; os das operações continuam.</param>
    public void Desativar(string? operacao)
    {
        if (operacao is null)
        {
            _global = false;
        }
        else
        {
            _operacoes.TryRemove(operacao, out _);
        }
    }

    public EstadoDoKillSwitch Estado() => new(_global, [.. _operacoes.Keys.Order(StringComparer.Ordinal)]);
}

public sealed record EstadoDoKillSwitch(bool Global, IReadOnlyList<string> Operacoes);
