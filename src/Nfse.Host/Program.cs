using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Nfse.Adaptadores.Rest;
using Nfse.Adaptadores.Soap;
using Nfse.Aplicacao.Consultas;
using Nfse.Aplicacao.Portas;
using Nfse.Infra.Oracle;
using Nfse.Observabilidade;

var builder = WebApplication.CreateBuilder(args);

builder.AdicionarTelemetriaNfse("nfse-novo", "Nfse.Host");

// Oracle: usuário NFSE_MODERNO_APP, só views e wrapper (ADR-0002).
string connectionString = builder.Configuration.GetConnectionString("Oracle")
    ?? throw new InvalidOperationException("ConnectionStrings:Oracle não configurada.");
builder.Services.AddSingleton(new FabricaDeConexoes(connectionString));
builder.Services.AddSingleton<IContribuintes, Contribuintes>();
builder.Services.AddSingleton<ILeituraDeNotas, LeituraDeNotas>();
builder.Services.AddSingleton<ConsultaDeNotasEmitidas>();
builder.Services.AddSingleton<ConsultaDeNotasTomadas>();

builder.Services.AdicionarAdaptadorSoap();

// REST: JWT (OIDC) configurado em Authentication:Schemes:Bearer; claim "inscricoes" autoriza por prestador e "documento" identifica o tomador (ADR-0010).
builder.Services.AddAuthentication().AddJwtBearer();
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// TODO(ADR-0007): check "ready" do Oracle.
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapOpenApi();
app.MapNfseSoap();
app.MapNfseRest();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposto para os testes com <c>WebApplicationFactory</c>.</summary>
public partial class Program;
