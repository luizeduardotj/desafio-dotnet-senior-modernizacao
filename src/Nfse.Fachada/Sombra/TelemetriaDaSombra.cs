using System.Diagnostics;
using System.Diagnostics.Metrics;
using Nfse.Fachada.Observabilidade;

namespace Nfse.Fachada.Sombra;

/// <summary>
/// Métricas e spans da sombra com os nomes da ADR-0012, no mesmo <c>Meter</c>/<c>ActivitySource</c> da fachada
/// (<see cref="Telemetria.Nome"/>). Labels só com operação, resultado, classe e motivo: nada de IM ou documento.
/// </summary>
internal static class TelemetriaDaSombra
{
    public static readonly ActivitySource Fonte = new(Telemetria.Nome);

    private static readonly Meter Medidor = new(Telemetria.Nome);

    private static readonly Counter<long> Comparacoes = Medidor.CreateCounter<long>(
        "sombra_comparacoes_total", description: "Respostas do legado e do novo comparadas em bytes (corpo e status).");

    private static readonly Counter<long> Divergencias = Medidor.CreateCounter<long>(
        "sombra_divergencias_total", description: "Divergências e falhas do novo na sombra, por classe automática (ADR-0009).");

    private static readonly Counter<long> Descartadas = Medidor.CreateCounter<long>(
        "sombra_descartadas_total", description: "Cópias amostradas que não chegaram a ser comparadas.");

    private static readonly Counter<long> Reexecucoes = Medidor.CreateCounter<long>(
        "sombra_reexecucoes_total", description: "Reexecuções de legado e novo depois de uma divergência de leitura.");

    private static readonly Histogram<double> Atraso = Medidor.CreateHistogram<double>(
        "sombra_atraso", unit: "s", description: "Do recebimento da requisição original ao fim da comparação.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60] });

    /// <param name="igual">Corpo e status idênticos.</param>
    public static void ContarComparacao(string operacao, bool igual) =>
        Comparacoes.Add(1, Operacao(operacao), new("resultado", igual ? "igual" : "divergente"));

    public static void ContarDivergencia(string operacao, ClasseDeDivergencia classe) =>
        Divergencias.Add(1, Operacao(operacao), new("classe", ClassificadorDeDivergencia.Nome(classe)));

    /// <param name="motivo"><c>canal_cheio</c>, <c>resposta_grande</c>, <c>requisicao_grande</c> ou <c>falha_legado</c>.</param>
    public static void ContarDescarte(string operacao, string motivo) =>
        Descartadas.Add(1, Operacao(operacao), new("motivo", motivo));

    /// <param name="resultado"><c>coincidiu</c>, <c>divergiu</c>, <c>erro</c> ou <c>limitada</c>.</param>
    public static void ContarReexecucao(string operacao, string resultado) =>
        Reexecucoes.Add(1, Operacao(operacao), new("resultado", resultado));

    public static void MedirAtraso(string operacao, long t0) =>
        Atraso.Record(Stopwatch.GetElapsedTime(t0).TotalSeconds, Operacao(operacao));

    private static KeyValuePair<string, object?> Operacao(string operacao) => new("operacao", operacao);
}
