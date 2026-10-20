using System.Globalization;

namespace Nfse.Fachada.Rollback;

/// <summary>Gatilhos do rollback automático (ADR-0014, seção 1); o nome vai para o label <c>gatilho</c>.</summary>
public enum GatilhoDeRollback
{
    Fault,
    P99,
    Fallback,
    RegraDivergente,
}

/// <param name="Detalhe">Os números que dispararam (contagens, taxas, limites); nunca dado do pedido.</param>
public readonly record struct Disparo(GatilhoDeRollback Gatilho, string Detalhe);

/// <summary>Gatilhos estatísticos sobre os totais da janela (ADR-0014, seção 2). Função pura.</summary>
internal static class AvaliadorDeGatilhos
{
    private const double Quantil = 0.99;

    public static string Nome(GatilhoDeRollback gatilho) => gatilho switch
    {
        GatilhoDeRollback.Fault => "fault",
        GatilhoDeRollback.P99 => "p99",
        GatilhoDeRollback.Fallback => "fallback",
        _ => "regra_divergente",
    };

    /// <param name="novo">Requisições atendidas pelo novo e tentativas que viraram fallback.</param>
    /// <param name="legado">Requisições da mesma operação atendidas pelo legado, fora de fallback.</param>
    /// <param name="referencia">Linha de base do M0, para quando o legado não tem amostras suficientes.</param>
    public static Disparo? Avaliar(Totais novo, Totais legado, ReferenciaDoLegado? referencia, OpcoesRollbackAutomatico o) =>
        Fault(novo, legado, referencia, o) ?? Fallback(novo, o) ?? P99(novo, legado, referencia, o);

    private static Disparo? Fault(Totais novo, Totais legado, ReferenciaDoLegado? referencia, OpcoesRollbackAutomatico o)
    {
        if (novo.N < o.MinimoDeAmostras)
        {
            return null;
        }

        double? doLegado = legado.N >= o.MinimoDeAmostras
            ? Estatistica.LimiteSuperiorDeWilson(legado.Faults, legado.N, o.Z)
            : referencia?.TaxaDeFault;
        if (doLegado is not { } ls)
        {
            return null;
        }

        double li = Estatistica.LimiteInferiorDeWilson(novo.Faults, novo.N, o.Z);
        double limite = ls + o.MargemDeFault;
        return li > limite
            ? new(GatilhoDeRollback.Fault, Texto($"novo {novo.Faults}/{novo.N} LI {li:F5} > limite {limite:F5} (legado {legado.Faults}/{legado.N})"))
            : null;
    }

    private static Disparo? Fallback(Totais novo, OpcoesRollbackAutomatico o)
    {
        if (novo.N < o.MinimoDeAmostras)
        {
            return null;
        }

        double li = Estatistica.LimiteInferiorDeWilson(novo.Fallbacks, novo.N, o.Z);
        return li > o.LimiteDeFallback
            ? new(GatilhoDeRollback.Fallback, Texto($"fallback {novo.Fallbacks}/{novo.N} LI {li:F5} > limite {o.LimiteDeFallback:F5}"))
            : null;
    }

    private static Disparo? P99(Totais novo, Totais legado, ReferenciaDoLegado? referencia, OpcoesRollbackAutomatico o)
    {
        if (novo.N < o.MinimoDeAmostrasDoP99)
        {
            return null;
        }

        double? doLegado = legado.N >= o.MinimoDeAmostrasDoP99
            ? BaldesDeLatencia.Teto(BaldesDeLatencia.BaldeDaPosicao(legado.Latencia, Estatistica.PosicaoSuperiorDoQuantil(legado.N, Quantil, o.Z)))
            : referencia?.P99?.TotalSeconds;
        if (doLegado is not { } ls || double.IsPositiveInfinity(ls))
        {
            return null;
        }

        double li = BaldesDeLatencia.Piso(BaldesDeLatencia.BaldeDaPosicao(novo.Latencia, Estatistica.PosicaoInferiorDoQuantil(novo.N, Quantil, o.Z)));
        double limite = o.FatorDoP99 * ls;
        return li > limite
            ? new(GatilhoDeRollback.P99, Texto($"p99 novo LI {li * 1000:F1} ms > {limite * 1000:F1} ms ({o.FatorDoP99:F2} x legado LS {ls * 1000:F1} ms; n {novo.N}/{legado.N})"))
            : null;
    }

    private static string Texto(FormattableString texto) => texto.ToString(CultureInfo.InvariantCulture);
}
