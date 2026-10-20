using System.Net;
using System.Security;
using System.Text;

namespace Nfse.Host.Testes;

/// <summary>
/// Banco indisponível (ADR-0008, "Modo novo e falhas"): na leitura, 503 para a fachada tentar o legado; no
/// cancelamento, o Fault de erro interno, porque depois do envio não há fallback.
/// </summary>
public sealed class BancoIndisponivelTests : IClassFixture<FabricaDoHost>, IDisposable
{
    private readonly FabricaDoHost _fabrica;

    public BancoIndisponivelTests(FabricaDoHost fabrica)
    {
        _fabrica = fabrica;
        _fabrica.Dados.FalhaDoBanco = new BancoForaDoAr();
    }

    public void Dispose() => _fabrica.Dados.FalhaDoBanco = null;

    [Theory]
    [InlineData("ConsultarNfse",
        "<ConsultarNfseEnvio><Prestador><Cnpj>0</Cnpj><InscricaoMunicipal>10000001</InscricaoMunicipal></Prestador>" +
        "<PeriodoEmissao><DataInicial>2026-10-01</DataInicial><DataFinal>2026-10-31</DataFinal></PeriodoEmissao></ConsultarNfseEnvio>")]
    [InlineData("ConsultarNfseServicoTomado",
        "<ConsultarNfseServicoTomadoEnvio><Consulente><CpfCnpj><Cnpj>01234567000195</Cnpj></CpfCnpj></Consulente>" +
        "<PeriodoEmissao><DataInicial>2026-10-01</DataInicial><DataFinal>2026-10-31</DataFinal></PeriodoEmissao></ConsultarNfseServicoTomadoEnvio>")]
    public async Task Leitura_com_banco_fora_do_ar_e_503_e_nao_fault(string operacao, string entrada)
    {
        (HttpStatusCode status, string corpo) = await EnviarAsync(operacao, entrada);

        // O corpo (ProblemDetails do UseStatusCodePages) não chega ao cliente: a fachada descarta o 503 e vai ao legado.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.DoesNotContain("soap:Fault", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelamento_com_banco_fora_do_ar_e_fault_de_erro_interno()
    {
        (HttpStatusCode status, string corpo) = await EnviarAsync("CancelarNfse",
            "<CancelarNfseEnvio><Pedido><InfPedidoCancelamento><IdentificacaoNfse><Numero>1</Numero><Cnpj>0</Cnpj>" +
            "<InscricaoMunicipal>10000001</InscricaoMunicipal></IdentificacaoNfse><CodigoCancelamento>1</CodigoCancelamento>" +
            "<Motivo>teste</Motivo></InfPedidoCancelamento></Pedido></CancelarNfseEnvio>");

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Contains("<faultcode>soap:Server</faultcode>", corpo, StringComparison.Ordinal);
    }

    private async Task<(HttpStatusCode Status, string Corpo)> EnviarAsync(string operacao, string entrada)
    {
        string envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>" +
            $"<{operacao}Request xmlns=\"http://notacarioca.rio.gov.br/\"><inputXML>{SecurityElement.Escape(entrada)}</inputXML></{operacao}Request></soap:Body></soap:Envelope>";
        using var conteudo = new StringContent(envelope, Encoding.UTF8, "text/xml");
        using var pedido = new HttpRequestMessage(HttpMethod.Post, "/NfseWS.asmx") { Content = conteudo };
        pedido.Headers.Add("SOAPAction", $"\"http://notacarioca.rio.gov.br/{operacao}\"");
        using HttpResponseMessage resposta = await _fabrica.CreateClient().SendAsync(pedido);
        return (resposta.StatusCode, await resposta.Content.ReadAsStringAsync());
    }
}
