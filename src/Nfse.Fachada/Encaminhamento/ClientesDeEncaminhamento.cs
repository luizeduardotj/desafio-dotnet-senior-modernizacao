using System.Diagnostics;
using System.Net;
using Yarp.ReverseProxy.Forwarder;

namespace Nfse.Fachada.Encaminhamento;

/// <summary>Um cliente HTTP por destino, com pool próprio. Os testes trocam por handlers em memória.</summary>
public sealed class ClientesDeEncaminhamento(HttpMessageInvoker legado, HttpMessageInvoker novo) : IDisposable
{
    public HttpMessageInvoker Legado { get; } = legado;

    public HttpMessageInvoker Novo { get; } = novo;

    public static ClientesDeEncaminhamento Criar() => new(CriarCliente(), CriarCliente());

    public void Dispose()
    {
        Legado.Dispose();
        Novo.Dispose();
    }

    // Configuração recomendada pelo YARP para encaminhamento direto: sem proxy, redirect, cookies nem descompressão
    // (os bytes passam como vieram), com propagação W3C do trace.
    private static HttpMessageInvoker CriarCliente() => new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        // O Service do Kubernetes balanceia por conexão. Sem prazo de vida, sob carga constante a conexão nunca fica
        // ociosa e réplicas novas (canário do Argo Rollouts, scale-out do HPA) não recebem nada (medido em k3d).
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        EnableMultipleHttp2Connections = true,
        ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current),
    });
}
