using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Nfse.Fachada.Testes.Integracao;

/// <summary>Legado (ou novo) falso em memória: grava o que recebeu e responde com o que o teste mandar.</summary>
internal sealed class ServidorFalso : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ServidorFalso(WebApplication app) => _app = app;

    public ConcurrentQueue<RequisicaoRecebida> Recebidas { get; } = new();

    /// <summary>Resposta padrão: 200 com <c>legado:{operação}</c>.</summary>
    public Func<HttpContext, RequisicaoRecebida, Task> Responder { get; set; } = (ctx, req) =>
        ctx.Response.WriteAsync(req.SoapAction);

    public static async Task<ServidorFalso> IniciarAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        WebApplication app = builder.Build();
        var servidor = new ServidorFalso(app);

        app.Run(async ctx =>
        {
            using var corpo = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(corpo, ctx.RequestAborted);
            var recebida = new RequisicaoRecebida(
                ctx.Request.Method,
                ctx.Request.Path + ctx.Request.QueryString,
                ctx.Request.Headers["SOAPAction"].ToString(),
                corpo.ToArray());
            servidor.Recebidas.Enqueue(recebida);
            await servidor.Responder(ctx, recebida);
        });

        await app.StartAsync();
        return servidor;
    }

    public HttpMessageHandler CriarHandler() => _app.GetTestServer().CreateHandler();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

internal sealed record RequisicaoRecebida(string Metodo, string Caminho, string SoapAction, byte[] Corpo);

/// <summary>Serviço novo que falha do jeito que o teste mandar, antes ou depois de ler o corpo.</summary>
internal sealed class NovoComFalha(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> comportamento) : HttpMessageHandler
{
    private int _chamadas;

    public int Chamadas => _chamadas;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _chamadas);
        return comportamento(request, cancellationToken);
    }
}
