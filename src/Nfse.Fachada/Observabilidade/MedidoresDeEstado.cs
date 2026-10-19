using System.Diagnostics.Metrics;
using System.Globalization;
using Nfse.Fachada.Admin;
using Nfse.Fachada.Flags;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Observabilidade;

/// <summary>
/// Gauges de estado da ADR-0012, lidos a cada coleta: <c>migracao_modo_info</c> (modo e percentual configurados por
/// operação) e <c>kill_switch_ativo</c> (qualquer kill switch ligado, do arquivo ou administrativo).
/// </summary>
/// <remarks>
/// Ao contrário de <see cref="Telemetria"/>, depende de serviços da réplica; por isso usa um <c>Meter</c> do
/// <see cref="IMeterFactory"/> com o mesmo nome, descartado junto com o container.
/// </remarks>
public sealed class MedidoresDeEstado : IDisposable
{
    private readonly Meter _medidor;

    public MedidoresDeEstado(IMeterFactory fabrica, IFonteDeFlags flags, KillSwitchAdministrativo killSwitch)
    {
        ArgumentNullException.ThrowIfNull(fabrica);
        _medidor = fabrica.Create(Telemetria.Nome);

        _medidor.CreateObservableGauge(
            "migracao_modo_info", () => Modos(flags.Atual()),
            description: "1 por operação em escopo, com o modo e o percentual configurados nas flags.");
        _medidor.CreateObservableGauge(
            "kill_switch_ativo", () => KillSwitchAtivo(flags.Atual(), killSwitch) ? 1L : 0L,
            description: "1 se algum kill switch (global ou de operação, do arquivo ou administrativo) está ligado.");
    }

    public void Dispose() => _medidor.Dispose();

    private static IEnumerable<Measurement<long>> Modos(FlagsDeMigracao? flags)
    {
        foreach (string operacao in OperacoesSoap.EmEscopo)
        {
            FlagsDaOperacao? op = flags?.Da(operacao);
            yield return new Measurement<long>(1,
                new KeyValuePair<string, object?>("operacao", operacao),
                new KeyValuePair<string, object?>("modo", op is null ? "indisponivel" : Telemetria.NomeDoModo(op.Modo)),
                new KeyValuePair<string, object?>("percentual", (op?.Percentual ?? 0).ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static bool KillSwitchAtivo(FlagsDeMigracao? flags, KillSwitchAdministrativo killSwitch)
    {
        if (flags?.KillSwitch == true)
        {
            return true;
        }

        foreach (string operacao in OperacoesSoap.EmEscopo)
        {
            if (killSwitch.Ativo(operacao) || flags?.Da(operacao).KillSwitch == true)
            {
                return true;
            }
        }

        return false;
    }
}
