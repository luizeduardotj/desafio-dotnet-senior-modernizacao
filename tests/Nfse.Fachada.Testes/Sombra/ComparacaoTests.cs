using System.Text;
using Nfse.Fachada.Sombra;

namespace Nfse.Fachada.Testes.Sombra;

public sealed class ComparacaoTests
{
    private static byte[] Resposta(string outputXml) => Encoding.UTF8.GetBytes(
        "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><R><outputXML>" +
        System.Security.SecurityElement.Escape(outputXml) + "</outputXML></R></soap:Body></soap:Envelope>");

    [Fact]
    public void Caminhos_divergentes_do_outputXML_com_posicao()
    {
        byte[] a = Resposta("<L><N a=\"1\"><X>1</X><Y>2</Y></N><N><X>3</X></N></L>");
        byte[] b = Resposta("<L><N a=\"2\"><X>1</X><Y>9</Y></N><N><X>3</X></N><N/></L>");

        Assert.Equal(["/L/N[1]/@a", "/L/N[1]/Y[1]/text()", "/L/N[3]"], DiferencaXml.Caminhos(a, b));
    }

    [Fact]
    public void Respostas_reais_iguais_nao_tem_caminho_divergente()
    {
        byte[] resposta = Exemplos.Bytes("04-consultar-nfse.resposta.xml");

        Assert.Empty(DiferencaXml.Caminhos(resposta, resposta));
    }

    [Fact]
    public void No_maximo_10_caminhos_e_sem_valores()
    {
        string notas(string valor) => "<L>" + string.Concat(Enumerable.Range(0, 30).Select(_ => $"<N>{valor}</N>")) + "</L>";

        IReadOnlyList<string> caminhos = DiferencaXml.Caminhos(Resposta(notas("10000001")), Resposta(notas("10000002")));

        Assert.Equal(DiferencaXml.MaximoDeCaminhos, caminhos.Count);
        Assert.All(caminhos, c => Assert.DoesNotContain("1000000", c, StringComparison.Ordinal));
    }

    [Fact]
    public void Xml_invalido_de_um_lado_diverge_na_raiz()
    {
        Assert.Equal(["/"], DiferencaXml.Caminhos(Resposta("<L/>"), "nao e xml"u8.ToArray()));
    }

    [Fact]
    public void Codigos_de_retorno_lidos_do_outputXML()
    {
        byte[] resposta = Exemplos.Bytes("06e-cancelar-nfse-inexistente.resposta.xml");

        Assert.Equal(["E050"], XmlSoap.CodigosDeRetorno(resposta));
        Assert.Empty(XmlSoap.CodigosDeRetorno(Exemplos.Bytes("04-consultar-nfse.resposta.xml")));
    }

    [Fact]
    public void Codigo_escrito_num_campo_livre_nao_conta()
    {
        // Um "<Codigo>E050</Codigo>" digitado na discriminação vem escapado duas vezes e não é estrutura.
        byte[] resposta = Resposta("<ListaNfse><Discriminacao>&lt;ListaMensagemRetorno&gt;&lt;MensagemRetorno&gt;&lt;Codigo&gt;E050&lt;/Codigo&gt;</Discriminacao></ListaNfse>");

        Assert.Empty(XmlSoap.CodigosDeRetorno(resposta));
    }

    [Fact]
    public void C20_FaultServer_com_texto_diferente()
    {
        static byte[] Fault(string codigo, string texto) => Encoding.UTF8.GetBytes(
            "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><soap:Fault>" +
            $"<faultcode>{codigo}</faultcode><faultstring>{texto}</faultstring></soap:Fault></soap:Body></soap:Envelope>");

        Assert.True(ClassificadorDeDivergencia.C20_FaultServerComTextoDiferente(500, Fault("soap:Server", "ORA-1"), 500, Fault("soap:Server", "Erro interno")));
        Assert.False(ClassificadorDeDivergencia.C20_FaultServerComTextoDiferente(500, Fault("soap:Client", "ORA-1"), 500, Fault("soap:Server", "Erro interno")));
        Assert.False(ClassificadorDeDivergencia.C20_FaultServerComTextoDiferente(500, Fault("soap:Server", "a"), 200, Resposta("<L/>")));
        Assert.Equal((ClasseDeDivergencia.DecisaoConsciente, "C20"),
            ClassificadorDeDivergencia.SemReexecucao([], 500, Fault("soap:Server", "ORA-1"), 500, Fault("soap:Server", "Erro interno")));
    }

    [Fact]
    public void C28_pedido_com_DTD_no_envelope_ou_no_inputXML()
    {
        Assert.True(ClassificadorDeDivergencia.C28_PedidoComDtd("<!DOCTYPE x><e/>"u8));
        Assert.True(ClassificadorDeDivergencia.C28_PedidoComDtd("<e><inputXML>&lt;!DOCTYPE x&gt;</inputXML></e>"u8));
        Assert.False(ClassificadorDeDivergencia.C28_PedidoComDtd(Exemplos.Bytes("04-consultar-nfse.xml")));
    }

    [Fact]
    public void Erro_5xx_do_novo_que_nao_e_C20_e_erro_novo_e_o_resto_vai_para_a_reexecucao()
    {
        Assert.Equal((ClasseDeDivergencia.ErroNovo, null), ClassificadorDeDivergencia.SemReexecucao([], 200, Resposta("<L/>"), 503, "x"u8));
        Assert.Null(ClassificadorDeDivergencia.SemReexecucao(Exemplos.Bytes("04-consultar-nfse.xml"), 200, Resposta("<L/>"), 200, Resposta("<M/>")));
    }

    [Fact]
    public void Limite_de_reexecucao_por_operacao()
    {
        var limite = new LimiteDeReexecucao(() => 1);

        Assert.True(limite.TentarConsumir("ConsultarNfse"));
        Assert.False(limite.TentarConsumir("ConsultarNfse"));
        Assert.True(limite.TentarConsumir("ConsultarNfseServicoTomado"));
        Assert.False(new LimiteDeReexecucao(() => 0).TentarConsumir("ConsultarNfse"));
    }
}
