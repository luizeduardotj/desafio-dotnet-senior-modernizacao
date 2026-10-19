using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Testes;

public class OperacoesSoapTests
{
    [Theory]
    [InlineData("\"http://notacarioca.rio.gov.br/ConsultarNfse\"", "ConsultarNfse")]
    [InlineData("http://notacarioca.rio.gov.br/CancelarNfse", "CancelarNfse")]
    [InlineData("  \"http://notacarioca.rio.gov.br/ConsultarNfseServicoTomado\"  ", "ConsultarNfseServicoTomado")]
    [InlineData("\"urn:qualquer/coisa/ConsultarNfse\"", "ConsultarNfse")]
    [InlineData("ConsultarNfse", "ConsultarNfse")]
    [InlineData("\"\"ConsultarNfse\"\"", "ConsultarNfse")]
    [InlineData("http://notacarioca.rio.gov.br/", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void C21_operacao_e_o_sufixo_apos_a_ultima_barra(string? soapAction, string esperada) =>
        Assert.Equal(esperada, OperacoesSoap.ExtrairOperacao(soapAction));

    [Theory]
    [InlineData("ConsultarNfse", true)]
    [InlineData("ConsultarNfseServicoTomado", true)]
    [InlineData("CancelarNfse", true)]
    [InlineData("consultarnfse", false)]
    [InlineData("CANCELARNFSE", false)]
    [InlineData("RecepcionarLoteRps", false)]
    [InlineData("ConsultarLoteRps", false)]
    [InlineData("ConsultarSituacaoLoteRps", false)]
    [InlineData("", false)]
    public void C29_escopo_compara_com_maiusculas(string operacao, bool emEscopo) =>
        Assert.Equal(emEscopo, OperacoesSoap.EstaEmEscopo(operacao));

    [Fact]
    public void C29_sufixo_com_caixa_diferente_nao_entra_no_escopo() =>
        Assert.False(OperacoesSoap.EstaEmEscopo(OperacoesSoap.ExtrairOperacao("\"http://notacarioca.rio.gov.br/consultarNfse\"")));

    [Theory]
    [InlineData("ConsultarNfse", "ConsultarNfse")]
    [InlineData("RecepcionarLoteRps", "RecepcionarLoteRps")]
    [InlineData("12345678901", "desconhecida")]
    [InlineData("<script>", "desconhecida")]
    public void Nome_para_telemetria_nao_repete_texto_livre_do_cliente(string operacao, string esperado) =>
        Assert.Equal(esperado, OperacoesSoap.NomeParaTelemetria(operacao));
}
