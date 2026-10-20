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

    private static readonly Counter<long> CnpjConferido = Medidor.CreateCounter<long>(
        "soap_cnpj_conferido_total", description: "Pedidos copiados pela sombra cujo CNPJ do prestador foi conferido com o cadastro da IM (C18/K04).");

    private static readonly Counter<long> CnpjInconsistente = Medidor.CreateCounter<long>(
        "soap_cnpj_inconsistente_total", description: "Pedidos copiados pela sombra com CNPJ do prestador diferente do cadastrado para a IM (C18/K04).");

    private static readonly Counter<long> Reproducoes = Medidor.CreateCounter<long>(
        "sombra_reproducoes_total", description: "Pedidos de divergência gravados cifrados para reprodução (ADR-0009, fase 2).");

    private static readonly Histogram<double> Atraso = Medidor.CreateHistogram<double>(
        "sombra_atraso", unit: "s", description: "Do recebimento da requisição original ao fim da comparação.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60] });

    /// <summary>
    /// Trace próprio, ligado ao da requisição original por span link: a sombra não infla a duração do trace do cliente
    /// (ADR-0012).
    /// </summary>
    public static Activity? IniciarSpan(CopiaDaSombra copia)
    {
        Activity.Current = null;
        return Fonte.StartActivity(
            ActivityKind.Internal, parentContext: default,
            tags: [new("soap.operacao", copia.Operacao), new("migracao.modo", Modo(copia))],
            links: copia.Origem == default ? null : [new ActivityLink(copia.Origem)],
            name: "sombra " + copia.Operacao);
    }

    /// <summary><c>sombra</c> (o legado respondeu ao cliente) ou <c>sombra_invertida</c> (o novo respondeu).</summary>
    public static string Modo(CopiaDaSombra copia) => copia.Invertida ? "sombra_invertida" : "sombra";

    /// <summary><c>traceId</c> da requisição original, para o log; <c>-</c> sem trace.</summary>
    public static string TraceIdDaOrigem(CopiaDaSombra copia) =>
        copia.Origem == default ? "-" : copia.Origem.TraceId.ToHexString();

    /// <param name="igual">Corpo e status idênticos.</param>
    public static void ContarComparacao(string operacao, bool igual) =>
        Comparacoes.Add(1, Operacao(operacao), new("resultado", igual ? "igual" : "divergente"));

    public static void ContarDivergencia(string operacao, ClasseDeDivergencia classe) =>
        Divergencias.Add(1, Operacao(operacao), new("classe", ClassificadorDeDivergencia.Nome(classe)));

    /// <param name="motivo">
    /// <c>canal_cheio</c>, <c>resposta_grande</c>, <c>requisicao_grande</c>, <c>falha_legado</c>,
    /// <c>novo_nao_configurado</c>, <c>sem_url_interna</c> ou, na sombra invertida, <c>falha_novo</c>, <c>erro_legado</c> e
    /// <c>invertida_proibida</c>.
    /// </param>
    public static void ContarDescarte(string operacao, string motivo) =>
        Descartadas.Add(1, Operacao(operacao), new("motivo", motivo));

    /// <param name="resultado"><c>coincidiu</c>, <c>divergiu</c>, <c>erro</c> ou <c>limitada</c>.</param>
    public static void ContarReexecucao(string operacao, string resultado) =>
        Reexecucoes.Add(1, Operacao(operacao), new("resultado", resultado));

    /// <summary>
    /// Denominador e numerador da taxa de CNPJ inconsistente (ADR-0004, C18/K04). Só conta pedidos amostrados: a taxa vale
    /// para a amostra, e as chaves raras (sempre copiadas) a puxam um pouco para os casos de erro.
    /// </summary>
    /// <param name="confere">Resultado da conferência; <c>null</c> não conta (não havia o que conferir).</param>
    public static void ContarConferenciaDeCnpj(string operacao, bool? confere)
    {
        if (confere is null)
        {
            return;
        }

        CnpjConferido.Add(1, Operacao(operacao));
        if (confere == false)
        {
            CnpjInconsistente.Add(1, Operacao(operacao));
        }
    }

    /// <param name="resultado"><c>guardada</c>, <c>limitada</c> ou <c>erro</c>.</param>
    public static void ContarReproducao(string operacao, string resultado) =>
        Reproducoes.Add(1, Operacao(operacao), new("resultado", resultado));

    public static void MedirAtraso(string operacao, long t0) =>
        Atraso.Record(Stopwatch.GetElapsedTime(t0).TotalSeconds, Operacao(operacao));

    private static KeyValuePair<string, object?> Operacao(string operacao) => new("operacao", operacao);
}
