using Nfse.Fachada.Flags;

namespace Nfse.Fachada.Roteamento;

/// <summary>Matriz de decisão da ADR-0008, avaliada a cada requisição em escopo. Função pura.</summary>
public static class DecisorDeDestino
{
    /// <summary>
    /// Se a decisão depende da chave. Quando não depende (flags indisponíveis, kill switch ou modo <c>legado</c>), o
    /// corpo nem é bufferizado: a operação em escopo atravessa a fachada como as de fora do escopo.
    /// </summary>
    public static bool RequerChave(string operacao, FlagsDeMigracao? flags, bool killSwitchAdministrativo)
    {
        if (flags is null || killSwitchAdministrativo || flags.KillSwitch)
        {
            return false;
        }

        FlagsDaOperacao op = flags.Da(operacao);
        return !op.KillSwitch && op.Modo != ModoDeMigracao.Legado;
    }

    /// <param name="operacao">Operação em escopo.</param>
    /// <param name="chave">Inscrição municipal ou documento do consulente; <c>null</c> se não extraída.</param>
    /// <param name="flags">Flags atuais; <c>null</c> se a fonte falhou (fail-safe: legado).</param>
    /// <param name="killSwitchAdministrativo">Kill switch nível 2 desta réplica (global ou da operação).</param>
    public static Decisao Decidir(string operacao, string? chave, FlagsDeMigracao? flags, bool killSwitchAdministrativo)
    {
        if (flags is null)
        {
            return new(Destino.Legado, MotivoDeRoteamento.FlagsIndisponiveis);
        }

        FlagsDaOperacao op = flags.Da(operacao);

        if (killSwitchAdministrativo || flags.KillSwitch || op.KillSwitch)
        {
            return new(Destino.Legado, MotivoDeRoteamento.KillSwitch);
        }

        if (op.Modo == ModoDeMigracao.Legado)
        {
            return new(Destino.Legado, MotivoDeRoteamento.ModoLegado);
        }

        if (chave is not null && Contem(op.Exclusao, chave))
        {
            return new(Destino.Legado, MotivoDeRoteamento.ListaDeExclusao);
        }

        if (op.Modo == ModoDeMigracao.Sombra)
        {
            bool elegivel = op.Inclusao.Length == 0 || (chave is not null && Contem(op.Inclusao, chave));
            return new(Destino.Legado, MotivoDeRoteamento.Sombra, elegivel);
        }

        // modo novo
        if (chave is null)
        {
            // Em novo 100% tudo vai ao novo, que reproduz E001/E010 do legado; senão o desligamento nunca é alcançável.
            return op.Percentual >= 100
                ? new(Destino.Novo, MotivoDeRoteamento.NovoTotalSemChave)
                : new(Destino.Legado, MotivoDeRoteamento.ChaveAusente);
        }

        if (Contem(op.Inclusao, chave))
        {
            return new(Destino.Novo, MotivoDeRoteamento.ListaDeInclusao);
        }

        return HashDeRoteamento.Balde(operacao, chave) < op.Percentual
            ? new(Destino.Novo, MotivoDeRoteamento.Percentual)
            : new(Destino.Legado, MotivoDeRoteamento.ForaDoPercentual);
    }

    private static bool Contem(string[] lista, string chave)
    {
        foreach (string item in lista)
        {
            if (string.Equals(item, chave, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
