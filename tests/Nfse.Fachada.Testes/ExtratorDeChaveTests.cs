using System.Text;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Testes;

public class ExtratorDeChaveTests
{
    [Theory]
    [InlineData("04-consultar-nfse.xml", OperacoesSoap.ConsultarNfse, "10000001")]
    [InlineData("04e-consultar-nfse-por-tomador.xml", OperacoesSoap.ConsultarNfse, "10000001")]
    [InlineData("05-consultar-nfse-servico-tomado.xml", OperacoesSoap.ConsultarNfseServicoTomado, "01234567000195")]
    [InlineData("06-cancelar-nfse.xml", OperacoesSoap.CancelarNfse, "10000025")]
    [InlineData("06c-cancelar-nfse-prazo-expirado.xml", OperacoesSoap.CancelarNfse, "10000001")]
    [InlineData("06f-cancelar-nfse-guia-paga.xml", OperacoesSoap.CancelarNfse, "10000026")]
    public void Extrai_a_chave_dos_exemplos_reais(string arquivo, string operacao, string chave) =>
        Assert.Equal(chave, ExtratorDeChave.Extrair(operacao, Exemplos.Bytes(arquivo)));

    [Fact]
    public void C21_operacao_do_header_decide_o_caminho_mesmo_com_outro_corpo() =>
        Assert.Null(ExtratorDeChave.Extrair(OperacoesSoap.CancelarNfse, Exemplos.Bytes("04-consultar-nfse.xml")));

    [Fact]
    public void Dtd_no_envelope_e_rejeitada()
    {
        string envelope = """
            <?xml version="1.0"?>
            <!DOCTYPE x [<!ENTITY im "10000001">]>
            <Envelope><inputXML>&lt;ConsultarNfseEnvio&gt;&lt;Prestador&gt;&lt;InscricaoMunicipal&gt;10000001&lt;/InscricaoMunicipal&gt;&lt;/Prestador&gt;&lt;/ConsultarNfseEnvio&gt;</inputXML></Envelope>
            """;
        Assert.Null(Extrair(OperacoesSoap.ConsultarNfse, envelope));
    }

    [Fact]
    public void Dtd_dentro_do_inputXML_e_rejeitada()
    {
        // O legado aceitaria (XmlDocument.LoadXml); a fachada não resolve entidade e manda ao legado.
        string interno = """<!DOCTYPE ConsultarNfseEnvio [<!ENTITY im SYSTEM "file:///etc/passwd">]><ConsultarNfseEnvio><Prestador><InscricaoMunicipal>&im;</InscricaoMunicipal></Prestador></ConsultarNfseEnvio>""";
        Assert.Null(Extrair(OperacoesSoap.ConsultarNfse, Envelope(interno)));
    }

    [Fact]
    public void C22_inputXML_e_o_primeiro_elemento_com_esse_nome_em_qualquer_namespace()
    {
        string envelope = """
            <e xmlns:a="urn:a"><a:inputXML>&lt;ConsultarNfseEnvio&gt;&lt;Prestador&gt;&lt;InscricaoMunicipal&gt;111&lt;/InscricaoMunicipal&gt;&lt;/Prestador&gt;&lt;/ConsultarNfseEnvio&gt;</a:inputXML><inputXML>&lt;ConsultarNfseEnvio&gt;&lt;Prestador&gt;&lt;InscricaoMunicipal&gt;222&lt;/InscricaoMunicipal&gt;&lt;/Prestador&gt;&lt;/ConsultarNfseEnvio&gt;</inputXML></e>
            """;
        Assert.Equal("111", Extrair(OperacoesSoap.ConsultarNfse, envelope));
    }

    [Fact]
    public void C23_espacos_nas_bordas_sao_ignorados() =>
        Assert.Equal("10000001", Extrair(OperacoesSoap.ConsultarNfse,
            Envelope("<ConsultarNfseEnvio><Prestador><InscricaoMunicipal>  10000001 </InscricaoMunicipal></Prestador></ConsultarNfseEnvio>")));

    [Fact]
    public void C23_namespace_padrao_no_xml_de_negocio_nao_casa_os_caminhos() =>
        Assert.Null(Extrair(OperacoesSoap.ConsultarNfse,
            Envelope("<ConsultarNfseEnvio xmlns=\"http://www.abrasf.org.br/nfse.xsd\"><Prestador><InscricaoMunicipal>10000001</InscricaoMunicipal></Prestador></ConsultarNfseEnvio>")));

    [Fact]
    public void C29_raiz_com_outra_caixa_nao_e_reconhecida() =>
        Assert.Null(Extrair(OperacoesSoap.ConsultarNfse,
            Envelope("<consultarNfseEnvio><Prestador><InscricaoMunicipal>10000001</InscricaoMunicipal></Prestador></consultarNfseEnvio>")));

    [Theory]
    [InlineData("<ConsultarNfseServicoTomadoEnvio><Consulente><CpfCnpj><Cpf>123.456.789-09</Cpf></CpfCnpj></Consulente></ConsultarNfseServicoTomadoEnvio>", "12345678909")]
    [InlineData("<ConsultarNfseServicoTomadoEnvio><Tomador><CpfCnpj><Cnpj>01.234.567/0001-95</Cnpj></CpfCnpj></Tomador></ConsultarNfseServicoTomadoEnvio>", "01234567000195")]
    [InlineData("<ConsultarNfseServicoTomadoEnvio><Consulente><CpfCnpj><Cnpj></Cnpj><Cpf>12345678909</Cpf></CpfCnpj></Consulente><Tomador><CpfCnpj><Cnpj>01234567000195</Cnpj></CpfCnpj></Tomador></ConsultarNfseServicoTomadoEnvio>", "12345678909")]
    [InlineData("<ConsultarNfseServicoTomadoEnvio><Consulente><CpfCnpj><Cnpj>abc</Cnpj></CpfCnpj></Consulente></ConsultarNfseServicoTomadoEnvio>", null)]
    [InlineData("<ConsultarNfseServicoTomadoEnvio><Consulente/></ConsultarNfseServicoTomadoEnvio>", null)]
    public void Documento_do_consulente_so_com_digitos_senao_o_do_tomador(string interno, string? esperado) =>
        Assert.Equal(esperado, Extrair(OperacoesSoap.ConsultarNfseServicoTomado, Envelope(interno)));

    [Theory]
    [InlineData("")]
    [InlineData("nao e xml")]
    [InlineData("<Envelope><Body/></Envelope>")]
    [InlineData("<Envelope><inputXML></inputXML></Envelope>")]
    [InlineData("<Envelope><inputXML>&lt;CancelarNfseEnvio&gt;</inputXML></Envelope>")]
    [InlineData("<Envelope><inputXML>&lt;CancelarNfseEnvio&gt;&lt;Pedido/&gt;&lt;/CancelarNfseEnvio&gt;</inputXML></Envelope>")]
    public void Corpo_sem_chave_devolve_null(string envelope) =>
        Assert.Null(Extrair(OperacoesSoap.CancelarNfse, envelope));

    private static string? Extrair(string operacao, string envelope) =>
        ExtratorDeChave.Extrair(operacao, Encoding.UTF8.GetBytes(envelope));

    private static string Envelope(string interno) =>
        "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><R xmlns=\"http://notacarioca.rio.gov.br/\"><inputXML>"
        + System.Security.SecurityElement.Escape(interno)
        + "</inputXML></R></soap:Body></soap:Envelope>";
}
