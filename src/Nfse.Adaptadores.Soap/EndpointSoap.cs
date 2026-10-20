using System.Data.Common;
using System.Diagnostics;
using System.Security;
using System.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nfse.Adaptadores.Soap.Operacoes;

namespace Nfse.Adaptadores.Soap;

/// <summary>
/// <c>POST /NfseWS.asmx</c> no serviço novo: mesmo despacho por <c>SOAPAction</c> (C21) e mesmo envelope do legado
/// (<c>legado/src/NotaCarioca.WebServices/Program.cs</c>).
/// </summary>
public static partial class EndpointSoap
{
    private const string Ns = "http://notacarioca.rio.gov.br/";

    /// <summary>
    /// Sinal interno para a fachada (ADR-0014): o dual-run do <c>CancelarNfse</c> deu <c>nao_classificada</c>. A
    /// fachada lê, dispara o rollback automático e remove o header antes do ERP. Não leva dado do pedido.
    /// </summary>
    public const string HeaderDoDualRun = "X-Nfse-Dual-Run";

    public const string DualRunDivergente = "divergente";

    public static IServiceCollection AdicionarAdaptadorSoap(this IServiceCollection services)
    {
        services.AddSingleton<ConsultarNfseSoap>();
        services.AddSingleton<ConsultarNfseServicoTomadoSoap>();
        services.AddSingleton<CancelarNfseSoap>();
        return services;
    }

    public static IEndpointRouteBuilder MapNfseSoap(this IEndpointRouteBuilder app)
    {
        app.MapPost("/NfseWS.asmx", ProcessarAsync);
        return app;
    }

    private static async Task ProcessarAsync(
        HttpContext ctx,
        ConsultarNfseSoap consultarNfse,
        ConsultarNfseServicoTomadoSoap consultarServicoTomado,
        CancelarNfseSoap cancelarNfse,
        ILoggerFactory logs)
    {
        string acao = ctx.Request.Headers["SOAPAction"].ToString().Trim().Trim('"');
        string operacao = acao[(acao.LastIndexOf('/') + 1)..];

        string corpo;
        using (var leitor = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8))
        {
            corpo = await leitor.ReadToEndAsync(ctx.RequestAborted).ConfigureAwait(false);
        }

        string saida;
        try
        {
            string entrada = LerInputXml(corpo);
            switch (operacao)
            {
                case "ConsultarNfse":
                    saida = await consultarNfse.ExecutarAsync(entrada, ctx.RequestAborted).ConfigureAwait(false);
                    break;
                case "ConsultarNfseServicoTomado":
                    saida = await consultarServicoTomado.ExecutarAsync(entrada, ctx.RequestAborted).ConfigureAwait(false);
                    break;
                case "CancelarNfse":
                    // Sem CancellationToken da requisição: depois de chegar ao banco, o cancelamento termina e comita
                    // mesmo que o cliente desista, e o reenvio é idempotente (K01, ADR-0009).
                    (saida, bool regraDivergente) = await cancelarNfse.ExecutarAsync(entrada, CancellationToken.None).ConfigureAwait(false);
                    if (regraDivergente)
                    {
                        ctx.Response.Headers[HeaderDoDualRun] = DualRunDivergente;
                    }

                    break;
                case "RecepcionarLoteRps":
                case "ConsultarSituacaoLoteRps":
                case "ConsultarLoteRps":
                    // Ficam no legado (ADR-0001). Se chegarem aqui por engano, 503 faz a fachada usar o legado.
                    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                default:
                    await FalhaAsync(ctx, "soap:Client", "Operação desconhecida: " + operacao).ConfigureAwait(false);
                    return;
            }
        }
        catch (XmlException)
        {
            await FalhaAsync(ctx, "soap:Client", "Envelope SOAP inválido").ConfigureAwait(false);
            return;
        }
        catch (DbException ex) when (operacao is "ConsultarNfse" or "ConsultarNfseServicoTomado")
        {
            // Banco fora do ar, pool esgotado, timeout: numa leitura, 503 faz a fachada tentar o legado uma vez
            // (ADR-0008). O 500 é o Fault que a fachada entrega ao cliente sem fallback. O cancelamento continua com o
            // Fault abaixo: depois do envio não há fallback.
            LogBancoIndisponivel(logs.CreateLogger(typeof(EndpointSoap)), operacao, ex);
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
#pragma warning disable CA1031 // o contrato do legado devolve Fault para qualquer erro interno
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogErroInterno(logs.CreateLogger(typeof(EndpointSoap)), NomeParaLog(operacao), ex);
            // C20: o legado devolve ex.Message (vaza o Oracle). Aqui o texto é genérico, com o trace para o suporte.
            await FalhaAsync(ctx, "soap:Server", $"Erro interno: falha ao processar a requisição (trace {Activity.Current?.TraceId})")
                .ConfigureAwait(false);
            return;
        }

        ctx.Response.ContentType = "text/xml; charset=utf-8";
        await ctx.Response.WriteAsync(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>" +
            "<" + operacao + "Response xmlns=\"" + Ns + "\"><outputXML>" +
            SecurityElement.Escape(saida) +
            "</outputXML></" + operacao + "Response></soap:Body></soap:Envelope>", ctx.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>C22: o primeiro elemento <c>inputXML</c> em qualquer namespace; sem ele, texto vazio (que vira E001).</summary>
    private static string LerInputXml(string envelope)
    {
        using var leitor = XmlReader.Create(new StringReader(envelope), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        while (leitor.Read())
        {
            if (leitor.NodeType == XmlNodeType.Element && leitor.LocalName == "inputXML")
            {
                return leitor.ReadElementContentAsString();
            }
        }

        return "";
    }

    private static Task FalhaAsync(HttpContext ctx, string codigo, string texto)
    {
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ctx.Response.ContentType = "text/xml; charset=utf-8";
        return ctx.Response.WriteAsync(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><soap:Fault>" +
            "<faultcode>" + codigo + "</faultcode><faultstring>" + SecurityElement.Escape(texto) +
            "</faultstring></soap:Fault></soap:Body></soap:Envelope>", ctx.RequestAborted);
    }

    // O header vem do cliente: só nomes conhecidos vão para o log (ADR-0012).
    private static string NomeParaLog(string operacao) =>
        operacao is "ConsultarNfse" or "ConsultarNfseServicoTomado" or "CancelarNfse" ? operacao : "desconhecida";

    [LoggerMessage(Level = LogLevel.Error, Message = "Erro interno na operação SOAP {Operacao}")]
    private static partial void LogErroInterno(ILogger logger, string operacao, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Banco indisponível na operação SOAP {Operacao}; 503 para a fachada usar o legado")]
    private static partial void LogBancoIndisponivel(ILogger logger, string operacao, Exception ex);
}
