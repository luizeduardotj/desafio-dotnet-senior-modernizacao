using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Nfse.Observabilidade;

var builder = WebApplication.CreateBuilder(args);

// Mesmo registro da fachada (ADR-0012). As fontes próprias (ActivitySource/Meter "Nfse.Host" e a instrumentação do
// ODP.NET) entram quando houver casos de uso.
builder.AdicionarTelemetriaNfse("nfse-novo", "Nfse.Host");

// TODO(ADR-0007): check "ready" do Oracle (NFSE_MODERNO_APP) quando a infraestrutura existir.
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

await app.RunAsync().ConfigureAwait(false);
