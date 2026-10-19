using System.Security;

namespace Nfse.Fachada.Encaminhamento;

/// <summary>O mesmo <c>soap:Fault</c> que o legado devolve em erro (<c>Program.cs</c>, método <c>Falha</c>).</summary>
internal static class FaultSoap
{
    public static Task EscreverAsync(HttpContext ctx, string codigo, string texto)
    {
        ctx.Response.Clear();
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ctx.Response.ContentType = "text/xml; charset=utf-8";
        return ctx.Response.WriteAsync(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><soap:Fault>" +
            "<faultcode>" + codigo + "</faultcode><faultstring>" + SecurityElement.Escape(texto) +
            "</faultstring></soap:Fault></soap:Body></soap:Envelope>",
            ctx.RequestAborted);
    }
}
