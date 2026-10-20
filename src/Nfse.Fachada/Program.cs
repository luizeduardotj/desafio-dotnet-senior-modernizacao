using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nfse.Fachada.Admin;
using Nfse.Fachada.Configuracao;
using Nfse.Fachada.Encaminhamento;
using Nfse.Fachada.Flags;
using Nfse.Fachada.Observabilidade;
using Nfse.Fachada.Sombra;
using Nfse.Fachada.Sombra.Reproducao;
using Nfse.Observabilidade;

// Ferramenta de reprodução de divergência (ADR-0009, fase 2), na mesma imagem: não sobe o servidor.
if (args is [FerramentaDeReproducao.Comando, ..])
{
    Environment.ExitCode = await FerramentaDeReproducao.ExecutarAsync(args[1..], Console.Out, CancellationToken.None).ConfigureAwait(false);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Flags de migração: arquivo observado, recarregado sem restart. Arquivo ausente ou inválido não derruba o processo:
// a seção some, a fonte devolve null e tudo vai ao legado (fail-safe).
string arquivoDeFlags = builder.Configuration[$"{OpcoesFachada.Secao}:ArquivoDeFlags"] ?? new OpcoesFachada().ArquivoDeFlags;
builder.Configuration.AddJsonFile(fonte =>
{
    fonte.Path = arquivoDeFlags;
    fonte.Optional = true;
    fonte.ReloadOnChange = true;
    fonte.OnLoadException = contexto => contexto.Ignore = true;
    fonte.ResolveFileProvider();
});

// Traces, métricas e logs (ADR-0012). O span do ASP.NET Core recebe os atributos de migração (Telemetria.MarcarSpan) e
// o HttpClient do YARP cria o span filho da chamada ao destino, propagando traceparent.
builder.AdicionarTelemetriaNfse("nfse-fachada", Telemetria.Nome);

builder.WebHost.ConfigureKestrel((contexto, kestrel) =>
{
    // O legado não limita o corpo (Program.cs:20); a fachada não introduz limite.
    kestrel.Limits.MaxRequestBodySize = null;
    var fachada = contexto.Configuration.GetSection(OpcoesFachada.Secao).Get<OpcoesFachada>() ?? new();
    var admin = contexto.Configuration.GetSection(OpcoesAdmin.Secao).Get<OpcoesAdmin>() ?? new();
    kestrel.ListenAnyIP(fachada.Porta);
    kestrel.ListenAnyIP(admin.Porta);
});

builder.Services.AddOptions<OpcoesFachada>().BindConfiguration(OpcoesFachada.Secao).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<OpcoesLegado>().BindConfiguration(OpcoesLegado.Secao).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<OpcoesNovo>().BindConfiguration(OpcoesNovo.Secao);
builder.Services.AddOptions<OpcoesSombra>().BindConfiguration(OpcoesSombra.Secao).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<OpcoesAdmin>().BindConfiguration(OpcoesAdmin.Secao).ValidateDataAnnotations().ValidateOnStart();
builder.Services.Configure<FlagsDeMigracao>(builder.Configuration.GetSection(FlagsDeMigracao.Secao));

builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, PortaDoEndpointMatcherPolicy>());
builder.Services.AddHttpForwarder();
builder.Services.AddSingleton(_ => ClientesDeEncaminhamento.Criar());
builder.Services.AddOptions<OpcoesFlags>().BindConfiguration(OpcoesFlags.Secao).ValidateDataAnnotations().ValidateOnStart();
var opcoesFlags = builder.Configuration.GetSection(OpcoesFlags.Secao).Get<OpcoesFlags>() ?? new();
switch (opcoesFlags.Fonte.Trim().ToLowerInvariant())
{
    case OpcoesFlags.FonteArquivo:
        builder.Services.AddSingleton<IFonteDeFlags, FonteDeFlagsPorConfiguracao>();
        break;
    case OpcoesFlags.FonteOpenFeature:
        // OpenFeature + flagd (ADR-0008): o retrato é refeito a cada evento do provider, fora do caminho da requisição.
        builder.Services.AddSingleton(sp => new FonteDeFlagsOpenFeature(
            ProvedorFlagd.Criar(opcoesFlags.Flagd, sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ProvedorFlagd))),
            FonteDeFlagsOpenFeature.DominioPadrao,
            sp.GetRequiredService<ILogger<FonteDeFlagsOpenFeature>>()));
        builder.Services.AddSingleton<IFonteDeFlags>(sp => sp.GetRequiredService<FonteDeFlagsOpenFeature>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<FonteDeFlagsOpenFeature>());
        break;
    default:
        throw new InvalidOperationException($"Flags:Fonte inválida: {opcoesFlags.Fonte} (arquivo ou openfeature).");
}

builder.Services.AddSingleton<KillSwitchAdministrativo>();
builder.Services.AddSingleton<EncaminhadorSoap>();

// Sombra (ADR-0009): canal limitado, workers e clientes HTTP próprios, fora do caminho do cliente. Leituras são
// repetidas no novo; o cancelamento só é avaliado, sem escrita, no endpoint interno do novo.
builder.Services.AddSingleton(_ => ClientesDaSombra.Criar());
builder.Services.AddSingleton<CanalDaSombra>();
builder.Services.AddSingleton<PublicadorDaSombra>();
builder.Services.AddSingleton<ComparadorDaSombra>();
builder.Services.AddSingleton<AvaliadorDoCancelamento>();
builder.Services.AddOptions<OpcoesReproducao>().BindConfiguration(OpcoesReproducao.Secao).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton<CofreDeReproducao>();
builder.Services.AddHostedService<LimpezaDaReproducao>();
builder.Services.AddHostedService<TrabalhadorDaSombra>();
builder.Services.AddSingleton<MedidoresDeEstado>();
builder.Services.AddHealthChecks().AddCheck<FlagsCarregadasHealthCheck>("flags", tags: ["ready"]);

var app = builder.Build();

if (app.Services.GetRequiredService<IOptions<OpcoesFachada>>().Value.Porta == app.Services.GetRequiredService<IOptions<OpcoesAdmin>>().Value.Porta)
{
    throw new InvalidOperationException("Fachada:Porta e Admin:Porta precisam ser diferentes.");
}

// Os gauges são observáveis: só precisam existir para entrar na coleta.
app.Services.GetRequiredService<MedidoresDeEstado>();

// Plano de dados só na porta pública; administração só na interna (PortaDoEndpointMatcherPolicy).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).SomenteNaPorta(Plano.Dados);
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).SomenteNaPorta(Plano.Dados);
app.MapEndpointsAdministrativos();

app.MapPost("/NfseWS.asmx", (HttpContext ctx, EncaminhadorSoap encaminhador) => encaminhador.ProcessarSoapAsync(ctx))
    .SomenteNaPorta(Plano.Dados);

// /admin nunca vai ao legado, nem pela porta pública.
app.Map("/admin/{**resto}", () => Results.NotFound()).SomenteNaPorta(Plano.Dados);

// Todo o resto (GET ?wsdl, /health do legado, outros caminhos) atravessa sem alteração.
app.Map("/{**caminho}", (HttpContext ctx, EncaminhadorSoap encaminhador) => encaminhador.EncaminharAoLegadoAsync(ctx))
    .SomenteNaPorta(Plano.Dados);

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposto para os testes de integração com <c>WebApplicationFactory</c>.</summary>
public partial class Program;
