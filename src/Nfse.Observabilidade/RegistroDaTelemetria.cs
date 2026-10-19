using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Nfse.Observabilidade;

/// <summary>
/// OpenTelemetry e logs da fachada e do serviço novo (ADR-0012): traces de ASP.NET Core e HttpClient com propagação
/// W3C, métricas de runtime e dos <c>Meter</c> do serviço, logs JSON com <c>TraceId</c>/<c>SpanId</c>.
/// </summary>
/// <remarks>
/// O exportador OTLP só é ligado com <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> configurado (compose, Kubernetes); sem ele,
/// testes e <c>dotnet run</c> não tentam falar com um coletor. As demais variáveis padrão do SDK
/// (<c>OTEL_EXPORTER_OTLP_PROTOCOL</c>, <c>OTEL_RESOURCE_ATTRIBUTES</c>, ...) continuam valendo.
/// </remarks>
public static class RegistroDaTelemetria
{
    /// <summary>Caminhos de probe: chamados a cada poucos segundos, só gerariam ruído nos traces.</summary>
    private static readonly PathString[] Probes = ["/health/live", "/health/ready"];

    /// <param name="servico"><c>service.name</c> (ex.: <c>nfse-fachada</c>).</param>
    /// <param name="fontes">Nomes de <c>ActivitySource</c> e <c>Meter</c> próprios do serviço.</param>
    public static TBuilder AdicionarTelemetriaNfse<TBuilder>(this TBuilder builder, string servico, params string[] fontes)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(servico);

        // Logs: JSON no console, com TraceId e SpanId do Activity corrente em todo evento (escopo). Pelo OTLP, o
        // trace_id e o span_id vão como campos do registro. As mensagens não carregam IM, documento nem corpo.
        builder.Logging.Configure(opcoes =>
            opcoes.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        builder.Logging.AddJsonConsole(opcoes =>
        {
            opcoes.IncludeScopes = true;
            opcoes.UseUtcTimestamp = true;
            opcoes.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });

        string? versao = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();

        OpenTelemetryBuilder otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(recurso => recurso.AddService(servico, serviceVersion: versao))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(opcoes => opcoes.Filter = ctx => !EhProbe(ctx.Request.Path))
                .AddHttpClientInstrumentation()
                .AddSource(fontes))
            .WithMetrics(metricas => metricas
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(fontes))
            .WithLogging(configureBuilder: null, configureOptions: opcoes =>
            {
                opcoes.IncludeFormattedMessage = true;
                opcoes.IncludeScopes = false;
            });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }

    private static bool EhProbe(PathString caminho)
    {
        foreach (PathString probe in Probes)
        {
            if (caminho.Equals(probe, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
