using System.Net;
using System.Security;
using System.Text;

namespace Nfse.Host.Testes;

public class SoapTests : IClassFixture<FabricaDoHost>
{
    private readonly FabricaDoHost _fabrica;

    public SoapTests(FabricaDoHost fabrica)
    {
        _fabrica = fabrica;
        _fabrica.Dados.Notas.Clear();
        _fabrica.Dados.Notas.Add(DadosFalsos.Nota(1, 21, new DateTime(2026, 10, 4, 9, 53, 0)));
        _fabrica.Dados.Notas.Add(DadosFalsos.Nota(2, 23, new DateTime(2026, 10, 2, 11, 19, 0)));
    }

    private static string Consulta(string im = "10000001", string ini = "2026-10-01", string fim = "2026-10-31", string extra = "") =>
        $"<ConsultarNfseEnvio><Prestador><Cnpj>0</Cnpj><InscricaoMunicipal>{im}</InscricaoMunicipal></Prestador>" +
        $"<PeriodoEmissao><DataInicial>{ini}</DataInicial><DataFinal>{fim}</DataFinal></PeriodoEmissao>{extra}</ConsultarNfseEnvio>";

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

    [Theory]
    [InlineData("<ConsultarNfseEnvio>", "E001")]
    [InlineData("<Outro/>", "E001")]
    public async Task E001_xml_invalido_ou_raiz_errada(string entrada, string codigo)
    {
        (_, string corpo) = await EnviarAsync("ConsultarNfse", entrada);

        Assert.Contains($"&lt;Codigo&gt;{codigo}&lt;/Codigo&gt;", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task C24_prestador_inexistente_vem_antes_de_tomador_invalido()
    {
        (_, string corpo) = await EnviarAsync("ConsultarNfse", Consulta(im: "99999999", extra: "<Tomador><CpfCnpj><Cnpj>123</Cnpj></CpfCnpj></Tomador>"));

        Assert.Contains("&lt;Codigo&gt;E010&lt;/Codigo&gt;", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task C25_prestador_inativo_e_E010()
    {
        (_, string corpo) = await EnviarAsync("ConsultarNfse", Consulta(im: "10000030"));

        Assert.Contains("&lt;Codigo&gt;E010&lt;/Codigo&gt;", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task C01_data_final_exclusiva_e_C13_competencia_ate_o_fim()
    {
        await EnviarAsync("ConsultarNfse", Consulta(fim: "2026-10-04"));

        Assert.Equal(new DateTime(2026, 10, 4), _fabrica.Dados.UltimoFiltro!.FimExclusivo);
        Assert.Equal(new DateTime(2026, 10, 4), _fabrica.Dados.UltimoFiltro.CompetenciaAte);
    }

    [Fact]
    public async Task C10_tomador_perde_zero_e_escape_igual_ao_legado()
    {
        (HttpStatusCode status, string corpo) = await EnviarAsync("ConsultarNfse", Consulta());

        Assert.True(status == HttpStatusCode.OK, corpo);
        // escape duplo: o XML de negócio escapa "&" e "<"; o envelope escapa de novo
        Assert.Contains("&lt;Cnpj&gt;1234567000195&lt;/Cnpj&gt;", corpo, StringComparison.Ordinal);
        Assert.Contains("Serviço &amp;amp; cia &amp;lt;teste&amp;gt;", corpo, StringComparison.Ordinal);
        // C08: ordem por emissão (23 emitida antes da 21)
        Assert.True(corpo.IndexOf("&lt;Numero&gt;23", StringComparison.Ordinal) < corpo.IndexOf("&lt;Numero&gt;21", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServicoTomado_compara_documento_como_texto_sem_prestador()
    {
        string entrada = "<ConsultarNfseServicoTomadoEnvio><Consulente><CpfCnpj><Cnpj>01.234.567/0001-95</Cnpj></CpfCnpj></Consulente>" +
            "<PeriodoEmissao><DataInicial>2026-10-01</DataInicial><DataFinal>2026-10-31</DataFinal></PeriodoEmissao></ConsultarNfseServicoTomadoEnvio>";

        (HttpStatusCode status, string corpo) = await EnviarAsync("ConsultarNfseServicoTomado", entrada);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("01234567000195", _fabrica.Dados.UltimoFiltroTomadas!.TomadorDigitos);
        Assert.Contains("ConsultarNfseServicoTomadoResposta", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServicoTomado_sem_documento_e_E020()
    {
        (_, string corpo) = await EnviarAsync("ConsultarNfseServicoTomado", "<ConsultarNfseServicoTomadoEnvio/>");

        Assert.Contains("&lt;Codigo&gt;E020&lt;/Codigo&gt;", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operacao_ainda_nao_extraida_devolve_503_para_a_fachada_ir_ao_legado()
    {
        (HttpStatusCode status, _) = await EnviarAsync("CancelarNfse", "<x/>");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
    }

    [Fact]
    public async Task Operacao_desconhecida_e_fault_soap_client()
    {
        (HttpStatusCode status, string corpo) = await EnviarAsync("Inventada", "<x/>");

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Contains("<faultcode>soap:Client</faultcode><faultstring>Operação desconhecida: Inventada</faultstring>", corpo, StringComparison.Ordinal);
    }
}
