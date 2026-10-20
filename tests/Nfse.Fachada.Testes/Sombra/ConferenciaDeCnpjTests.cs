using System.Text;
using Nfse.Fachada.Sombra;
using Nfse.Fachada.Testes.Integracao;

namespace Nfse.Fachada.Testes.Sombra;

/// <summary>C18/K04 (ADR-0004): CNPJ do pedido × CNPJ do cadastro que o legado devolve em cada nota, sem consultar o banco.</summary>
public sealed class ConferenciaDeCnpjTests
{
    private static readonly byte[] Pedido = Exemplos.Bytes("04-consultar-nfse.xml");
    private static readonly byte[] Resposta = Exemplos.Bytes("04-consultar-nfse.resposta.xml");

    [Fact]
    public void C18_cnpj_do_pedido_igual_ao_do_cadastro_confere() =>
        Assert.True(ConferenciaDeCnpj.DaConsulta(Pedido, Resposta));

    [Fact]
    public void C18_cnpj_do_pedido_diferente_do_cadastro_nao_confere() =>
        Assert.False(ConferenciaDeCnpj.DaConsulta(Trocar(Pedido, "11100003000113", "99999999000191"), Resposta));

    [Fact]
    public void C18_pedido_sem_cnpj_nao_e_conferido() =>
        Assert.Null(ConferenciaDeCnpj.DaConsulta(Trocar(Pedido, "&lt;Cnpj&gt;11100003000113&lt;/Cnpj&gt;", ""), Resposta));

    [Theory]
    [InlineData("E010")]
    [InlineData("E040")]
    public void C18_resposta_sem_nota_nao_tem_cadastro_para_conferir(string codigo) =>
        Assert.Null(ConferenciaDeCnpj.DaConsulta(Pedido, Encoding.UTF8.GetBytes(SombraTests.RespostaComCodigo("ConsultarNfse", codigo))));

    [Fact]
    public void C18_pedido_malformado_nao_e_conferido() =>
        Assert.Null(ConferenciaDeCnpj.DaConsulta("<soap:Envelope>"u8, Resposta));

    [Fact]
    public void C18_so_vale_para_ConsultarNfse() =>
        Assert.Null(ConferenciaDeCnpj.DaConsulta(Exemplos.Bytes("05-consultar-nfse-servico-tomado.xml"), Exemplos.Bytes("05-consultar-nfse-servico-tomado.resposta.xml")));

    [Theory]
    [InlineData("01234567000195", "1234567000195", true)]
    [InlineData("11.100.003/0001-13", "11100003000113", true)]
    [InlineData("11100003000113", "11100003000114", false)]
    [InlineData("", "11100003000113", null)]
    [InlineData("000", "11100003000113", null)]
    public void C18_conferencia_ignora_formatacao_e_zeros_a_esquerda(string informado, string cadastrado, bool? confere) =>
        Assert.Equal(confere, ConferenciaDeCnpj.Conferir(informado, cadastrado));

    private static byte[] Trocar(byte[] corpo, string de, string para) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(corpo).Replace(de, para, StringComparison.Ordinal));
}
