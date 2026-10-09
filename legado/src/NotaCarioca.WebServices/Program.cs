using System;
using System.IO;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using NotaCarioca.Dados;

namespace NotaCarioca.WebServices
{
    public class Program
    {
        private const string Ns = "http://notacarioca.rio.gov.br/";

        public static void Main(string[] args)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = null; });
            WebApplication app = builder.Build();

            app.MapGet("/health", async (HttpContext ctx) =>
            {
                try
                {
                    ContribuinteRepositorio repo = new ContribuinteRepositorio();
                    if (repo.PingBanco())
                    {
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.WriteAsync("OK");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("health: " + ex.Message);
                }
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsync("INDISPONIVEL");
            });

            app.MapGet("/NfseWS.asmx", async (HttpContext ctx) =>
            {
                if (!ctx.Request.Query.ContainsKey("wsdl"))
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.WriteAsync("<html><body><h1>NfseWS</h1><a href=\"NfseWS.asmx?wsdl\">Descrição do serviço</a></body></html>");
                    return;
                }
                string caminho = Path.Combine(AppContext.BaseDirectory, "NfseWS.wsdl");
                ctx.Response.ContentType = "text/xml; charset=utf-8";
                await ctx.Response.WriteAsync(File.ReadAllText(caminho, Encoding.UTF8));
            });

            app.MapPost("/NfseWS.asmx", async (HttpContext ctx) =>
            {
                string acao = ctx.Request.Headers["SOAPAction"].ToString().Trim().Trim('"');
                string operacao = acao.Substring(acao.LastIndexOf('/') + 1);

                string corpo;
                using (StreamReader sr = new StreamReader(ctx.Request.Body, Encoding.UTF8))
                {
                    corpo = await sr.ReadToEndAsync();
                }

                string saida;
                try
                {
                    string entrada = LerInputXml(corpo);
                    NfseWS ws = new NfseWS();
                    switch (operacao)
                    {
                        case "RecepcionarLoteRps": saida = ws.RecepcionarLoteRps(entrada); break;
                        case "ConsultarSituacaoLoteRps": saida = ws.ConsultarSituacaoLoteRps(entrada); break;
                        case "ConsultarLoteRps": saida = ws.ConsultarLoteRps(entrada); break;
                        case "ConsultarNfse": saida = ws.ConsultarNfse(entrada); break;
                        case "ConsultarNfseServicoTomado": saida = ws.ConsultarNfseServicoTomado(entrada); break;
                        case "CancelarNfse": saida = ws.CancelarNfse(entrada); break;
                        default:
                            await Falha(ctx, "soap:Client", "Operação desconhecida: " + operacao);
                            return;
                    }
                }
                catch (XmlException)
                {
                    await Falha(ctx, "soap:Client", "Envelope SOAP inválido");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("erro em " + operacao + ": " + ex);
                    await Falha(ctx, "soap:Server", "Erro interno: " + ex.Message);
                    return;
                }

                ctx.Response.ContentType = "text/xml; charset=utf-8";
                await ctx.Response.WriteAsync(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                    "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>" +
                    "<" + operacao + "Response xmlns=\"" + Ns + "\"><outputXML>" +
                    System.Security.SecurityElement.Escape(saida) +
                    "</outputXML></" + operacao + "Response></soap:Body></soap:Envelope>");
            });

            AgendadorLotes.Iniciar();
            app.Run();
        }

        private static string LerInputXml(string envelope)
        {
            using (XmlReader xr = XmlReader.Create(new StringReader(envelope)))
            {
                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "inputXML")
                    {
                        return xr.ReadElementContentAsString();
                    }
                }
            }
            return "";
        }

        private static async System.Threading.Tasks.Task Falha(HttpContext ctx, string codigo, string texto)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "text/xml; charset=utf-8";
            await ctx.Response.WriteAsync(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><soap:Fault>" +
                "<faultcode>" + codigo + "</faultcode><faultstring>" + System.Security.SecurityElement.Escape(texto) +
                "</faultstring></soap:Fault></soap:Body></soap:Envelope>");
        }
    }
}
