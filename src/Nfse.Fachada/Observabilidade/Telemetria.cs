using System.Diagnostics;
using System.Diagnostics.Metrics;
using Nfse.Fachada.Flags;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Observabilidade;

/// <summary>
/// Traces e métricas da fachada com os nomes da ADR-0012. O OpenTelemetry assina <see cref="Nome"/>
/// (<c>AddSource</c>/<c>AddMeter</c>, em <c>Program.cs</c>) e exporta por OTLP.
/// </summary>
/// <remarks>
/// Cardinalidade: labels só com operação conhecida, modo, destino, resultado e causa. O motivo de roteamento fica no
/// span (<c>migracao.motivo_roteamento</c>). IM, documento, número de nota e protocolo nunca entram.
/// </remarks>
public static class Telemetria
{
    public const string Nome = "Nfse.Fachada";

    private static readonly Meter Medidor = new(Nome);

    // Os buckets padrão do SDK (0, 5, 10, ... 10000) pressupõem milissegundos; aqui tudo é em segundos.
    private static readonly double[] BucketsDeDuracao = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10, 30, 100];

    private static readonly Counter<long> Requisicoes = Medidor.CreateCounter<long>(
        "nfse_requisicoes_total", description: "Requisições SOAP por operação, modo, destino que respondeu e resultado.");

    private static readonly Histogram<double> Duracao = Medidor.CreateHistogram<double>(
        "nfse_duracao", unit: "s", description: "Duração da chamada a cada destino, vista pela fachada.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = BucketsDeDuracao });

    private static readonly Histogram<double> Overhead = Medidor.CreateHistogram<double>(
        "fachada_overhead", unit: "s",
        description: "Tempo da requisição SOAP gasto na própria fachada (total menos as chamadas aos destinos).",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.0005, 0.001, 0.0025, 0.005, 0.0075, 0.01, 0.025, 0.05, 0.1, 0.25] });

    private static readonly Counter<long> Fallbacks = Medidor.CreateCounter<long>(
        "nfse_fallback_total", description: "Leituras (ou cancelamentos não enviados) que o novo não atendeu e foram ao legado.");

    /// <summary>Atributos do span da requisição.</summary>
    /// <param name="fonteDasFlags">Fonte que avaliou as flags (<c>arquivo</c>, <c>openfeature</c>); <c>null</c> fora do escopo, sem avaliação.</param>
    public static void MarcarSpan(string operacao, string modo, Decisao decisao, string? fonteDasFlags)
    {
        Activity? atividade = Activity.Current;
        if (atividade is null)
        {
            return;
        }

        atividade.SetTag("soap.operacao", operacao);
        atividade.SetTag("migracao.modo", modo);
        atividade.SetTag("migracao.destino", NomeDoDestino(decisao.Destino));
        atividade.SetTag("migracao.motivo_roteamento", decisao.Motivo.ToString());
        if (fonteDasFlags is not null)
        {
            atividade.SetTag("migracao.fonte_flags", fonteDasFlags);
        }
    }

    /// <param name="destino">Quem respondeu ao cliente (depois de um eventual fallback).</param>
    /// <param name="status">Status HTTP devolvido ao cliente.</param>
    public static void ContarRequisicao(string operacao, string modo, Destino destino, int status) =>
        Requisicoes.Add(1,
            new KeyValuePair<string, object?>("operacao", operacao),
            new KeyValuePair<string, object?>("modo", modo),
            new KeyValuePair<string, object?>("destino", NomeDoDestino(destino)),
            new KeyValuePair<string, object?>("resultado", Resultado(status)));

    public static void MedirDuracao(string operacao, Destino destino, TimeSpan duracao) =>
        Duracao.Record(duracao.TotalSeconds,
            new KeyValuePair<string, object?>("operacao", operacao),
            new KeyValuePair<string, object?>("destino", NomeDoDestino(destino)));

    /// <summary>
    /// Orçamento da ADR-0008 (≤ 5 ms no p99 fora do escopo, ≤ 10 ms em escopo): bufferização, parsing da chave,
    /// avaliação das flags e o que mais a fachada fizer fora das chamadas aos destinos.
    /// </summary>
    public static void MedirOverhead(string operacao, TimeSpan overhead) =>
        Overhead.Record(Math.Max(0, overhead.TotalSeconds), new KeyValuePair<string, object?>("operacao", operacao));

    /// <param name="causa"><c>conexao</c>, <c>transporte</c>, <c>timeout</c>, <c>http_502</c>, <c>http_503</c> ou <c>http_504</c>.</param>
    public static void ContarFallback(string operacao, string causa) =>
        Fallbacks.Add(1,
            new KeyValuePair<string, object?>("operacao", operacao),
            new KeyValuePair<string, object?>("causa", causa));

    /// <summary>
    /// <c>ok</c> para 2xx; <c>fault</c> para o resto (no SOAP, o legado e o novo só devolvem 200 ou Fault 500; 502/504
    /// são do próprio proxy).
    /// </summary>
    /// <remarks>
    /// TODO(ADR-0012): <c>erro_negocio</c> (resposta 200 com <c>ListaMensagemRetorno</c>, ex.: E050) exige olhar o corpo
    /// da resposta. Fica para quando houver leitura da resposta, junto da sombra.
    /// </remarks>
    public static string Resultado(int status) => status is >= 200 and < 300 ? "ok" : "fault";

    public static string NomeDoModo(ModoDeMigracao modo) => modo switch
    {
        ModoDeMigracao.Sombra => "sombra",
        ModoDeMigracao.Novo => "novo",
        _ => "legado",
    };

    private static string NomeDoDestino(Destino destino) => destino == Destino.Novo ? "novo" : "legado";
}
