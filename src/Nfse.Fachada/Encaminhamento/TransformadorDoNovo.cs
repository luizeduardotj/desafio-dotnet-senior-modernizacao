using Yarp.ReverseProxy.Forwarder;

namespace Nfse.Fachada.Encaminhamento;

/// <summary>
/// Transformação padrão do YARP, mas, em leitura, um 502/503/504 do novo é descartado antes de qualquer byte chegar
/// ao cliente, para que a mesma requisição possa ir uma vez ao legado.
/// </summary>
/// <remarks>
/// O 500 não entra: é o <c>soap:Fault</c> que o novo devolve de propósito, como o legado (C20; <c>soap:Client</c> para
/// envelope inválido, <c>soap:Server</c> em erro interno). Repeti-lo no legado levaria a requisição duas vezes ao
/// Oracle e poluiria a métrica de fallback.
/// </remarks>
internal sealed class TransformadorDoNovo(bool descartarIndisponibilidade) : HttpTransformer
{
    public int? StatusDescartado { get; private set; }

    public override ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken) =>
        Default.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

    public override ValueTask<bool> TransformResponseAsync(HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
    {
        if (descartarIndisponibilidade && proxyResponse is not null && Indisponibilidade((int)proxyResponse.StatusCode))
        {
            StatusDescartado = (int)proxyResponse.StatusCode;
            return ValueTask.FromResult(false);
        }

        return Default.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
    }

    internal static bool Indisponibilidade(int status) =>
        status is StatusCodes.Status502BadGateway or StatusCodes.Status503ServiceUnavailable or StatusCodes.Status504GatewayTimeout;

    public override ValueTask TransformResponseTrailersAsync(HttpContext httpContext, HttpResponseMessage proxyResponse, CancellationToken cancellationToken) =>
        Default.TransformResponseTrailersAsync(httpContext, proxyResponse, cancellationToken);
}
