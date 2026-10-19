using System.Diagnostics;
using System.Net;

namespace Nfse.Fachada.Sombra;

/// <summary>
/// Clientes HTTP só da sombra, com pool próprio: uma sombra lenta não ocupa conexões do caminho do cliente
/// (<see cref="Encaminhamento.ClientesDeEncaminhamento"/>). O do legado só é usado na reexecução de divergências.
/// </summary>
public sealed class ClientesDaSombra(HttpMessageInvoker legado, HttpMessageInvoker novo) : IDisposable
{
    public HttpMessageInvoker Legado { get; } = legado;

    public HttpMessageInvoker Novo { get; } = novo;

    public static ClientesDaSombra Criar() => new(CriarCliente(), CriarCliente());

    public void Dispose()
    {
        Legado.Dispose();
        Novo.Dispose();
    }

    // Sem descompressão nem redirect: os bytes comparados são os que o cliente receberia.
    private static HttpMessageInvoker CriarCliente() => new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ActivityHeadersPropagator = DistributedContextPropagator.Current,
    });
}
