using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nfse.Fachada.Admin;
using Nfse.Fachada.Encaminhamento;
using Nfse.Fachada.Flags;
using Nfse.Fachada.Sombra;

namespace Nfse.Fachada.Testes.Integracao;

internal sealed class FlagsFixas(FlagsDeMigracao? valor) : IFonteDeFlags
{
    public FlagsDeMigracao? Valor { get; set; } = valor;

    public string Nome => "fixas";

    public FlagsDeMigracao? Atual() => Valor;
}

/// <summary>Fachada real, com legado e novo trocados por handlers em memória e flags controladas pelo teste.</summary>
internal sealed class FabricaDaFachada(
    HttpMessageHandler legado,
    HttpMessageHandler? novo,
    IFonteDeFlags flags,
    IReadOnlyDictionary<string, string?>? configuracao = null) : WebApplicationFactory<Program>
{
    public const int PortaPublica = 9080;
    public const int PortaAdmin = 9081;
    public const string Token = "token-de-teste";

    public LogsCapturados Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testes");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var valores = new Dictionary<string, string?>
            {
                ["Legado:Url"] = "http://legado.test",
                ["Novo:Url"] = novo is null ? null : "http://novo.test",
                ["Novo:TimeoutLeitura"] = "00:00:00.300",
                ["Novo:TimeoutCancelamento"] = "00:00:00.300",
                ["Fachada:Porta"] = PortaPublica.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Admin:Porta"] = PortaAdmin.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Admin:Token"] = Token,
            };
            foreach ((string chave, string? valor) in configuracao ?? new Dictionary<string, string?>())
            {
                valores[chave] = valor;
            }

            config.AddInMemoryCollection(valores);
        });
        builder.ConfigureLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Debug));
        builder.ConfigureTestServices(services =>
        {
            HttpMessageHandler novoOuFalha = novo ?? new NovoComFalha((_, _) => throw new InvalidOperationException("novo não configurado"));
            services.RemoveAll<ClientesDeEncaminhamento>();
            services.AddSingleton(new ClientesDeEncaminhamento(
                new HttpMessageInvoker(legado, disposeHandler: false),
                new HttpMessageInvoker(novoOuFalha, disposeHandler: false)));
            services.RemoveAll<ClientesDaSombra>();
            services.AddSingleton(new ClientesDaSombra(
                new HttpMessageInvoker(legado, disposeHandler: false),
                new HttpMessageInvoker(novoOuFalha, disposeHandler: false)));
            services.AddTransient<IStartupFilter, PortaPublicaNoTestServer>();
            services.RemoveAll<IFonteDeFlags>();
            services.AddSingleton(flags);
        });
    }

    public async Task<HttpResponseMessage> EnviarSoapAsync(string operacao, byte[] corpo, string? soapAction = null)
    {
        using var requisicao = new HttpRequestMessage(HttpMethod.Post, "/NfseWS.asmx")
        {
            Content = new ByteArrayContent(corpo),
        };
        requisicao.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=utf-8");
        requisicao.Headers.TryAddWithoutValidation("SOAPAction", soapAction ?? Exemplos.SoapAction(operacao));
        return await CreateClient().SendAsync(requisicao);
    }

    /// <summary>Requisição como se chegasse por uma porta específica do Kestrel.</summary>
    public Task<HttpContext> EnviarNaPortaAsync(int porta, string metodo, string caminho, string? token = null, string? query = null) =>
        Server.SendAsync(ctx =>
        {
            ctx.Connection.LocalPort = porta;
            ctx.Request.Method = metodo;
            ctx.Request.Path = caminho;
            if (query is not null)
            {
                ctx.Request.QueryString = new QueryString(query);
            }

            if (token is not null)
            {
                ctx.Request.Headers[EndpointsAdministrativos.HeaderDoToken] = token;
            }
        });
}

/// <summary>
/// O TestServer não tem porta (LocalPort 0). Requisições do <c>CreateClient</c> passam a chegar pela porta pública;
/// as de <see cref="FabricaDaFachada.EnviarNaPortaAsync"/> mantêm a porta que o teste escolheu.
/// </summary>
internal sealed class PortaPublicaNoTestServer : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((ctx, proximo) =>
        {
            if (ctx.Connection.LocalPort == 0)
            {
                ctx.Connection.LocalPort = FabricaDaFachada.PortaPublica;
            }

            return proximo(ctx);
        });
        next(app);
    };
}

/// <summary>Todas as mensagens de log formatadas, para conferir que não há dado pessoal (ADR-0012).</summary>
internal sealed class LogsCapturados : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _mensagens = new();

    public IReadOnlyCollection<string> Mensagens => _mensagens;

    public ILogger CreateLogger(string categoryName) => new Capturador(categoryName, _mensagens);

    public void Dispose()
    {
    }

    private sealed class Capturador(string categoria, System.Collections.Concurrent.ConcurrentQueue<string> destino) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string estado = state is IEnumerable<KeyValuePair<string, object?>> campos
                ? string.Join(";", campos.Select(c => $"{c.Key}={c.Value}"))
                : string.Empty;
            destino.Enqueue($"{categoria}|{formatter(state, exception)}|{estado}|{exception}");
        }
    }
}
