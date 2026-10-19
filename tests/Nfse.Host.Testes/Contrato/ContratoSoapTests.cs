using System.Net;
using System.Text;
using System.Xml.Linq;
using Nfse.Aplicacao.Cancelamento;
using Nfse.Aplicacao.Consultas;
using Nfse.Dominio.Cancelamento;
using Nfse.Dominio.Notas;

namespace Nfse.Host.Testes.Contrato;

/// <summary>
/// Contrato SOAP do serviço novo contra as respostas reais do legado em <c>tools/exemplos</c>: o pedido do exemplo é
/// enviado ao Host como está e a resposta tem de ter a mesma forma (elementos, namespaces e ordem, no envelope e no XML
/// de negócio dentro de <c>outputXML</c>) e o mesmo código de erro. Os valores são do golden master (tests/paridade),
/// que precisa do Oracle; este teste não precisa.
/// </summary>
[Trait("Categoria", "Contrato")]
public class ContratoSoapTests : IClassFixture<FabricaDoHost>
{
    private static readonly string Pasta = Path.Combine(AppContext.BaseDirectory, "exemplos");
    private static readonly DateTime Agora = new(2026, 10, 9, 15, 26, 34);
    private readonly FabricaDoHost _fabrica;

    public ContratoSoapTests(FabricaDoHost fabrica)
    {
        _fabrica = fabrica;
        foreach ((long id, string im) in new[] { (3L, "10000003"), (25L, "10000025"), (26L, "10000026") })
        {
            if (!_fabrica.Dados.Prestadores.Exists(p => p.InscricaoMunicipal == im))
            {
                _fabrica.Dados.Prestadores.Add(new PrestadorLido(id, im, "13500075000183", $"PRESTADOR {id}", Ativo: true));
            }
        }

        _fabrica.Dados.Notas.Clear();
        _fabrica.Dados.Notas.Add(DadosFalsos.Nota(1, 23, new DateTime(2026, 10, 2, 11, 19, 0)));
        _fabrica.Dados.Notas.Add(DadosFalsos.Nota(2, 22, new DateTime(2026, 10, 3, 10, 6, 0)));
        _fabrica.Dados.Notas.Add(DadosFalsos.Nota(3, 21, new DateTime(2026, 10, 4, 9, 53, 0)));
    }

    [Theory]
    [InlineData("04-consultar-nfse.xml", null)]
    [InlineData("04e-consultar-nfse-por-tomador.xml", null)]
    [InlineData("05-consultar-nfse-servico-tomado.xml", null)]
    [InlineData("06-cancelar-nfse.xml", DecisaoDeCancelamento.Permitido)]
    [InlineData("06c-cancelar-nfse-prazo-expirado.xml", DecisaoDeCancelamento.PrazoExpirado)]
    [InlineData("06d-cancelar-nfse-substituida.xml", DecisaoDeCancelamento.NotaSubstituida)]
    [InlineData("06e-cancelar-nfse-inexistente.xml", DecisaoDeCancelamento.NotaNaoEncontrada)]
    [InlineData("06f-cancelar-nfse-guia-paga.xml", DecisaoDeCancelamento.GuiaPaga)]
    public async Task Resposta_tem_a_forma_do_exemplo_do_legado(string pedido, DecisaoDeCancelamento? decisao)
    {
        if (decisao is { } d)
        {
            Responder(d);
        }

        string operacao = XDocument.Load(Path.Combine(Pasta, pedido)).Descendants()
            .First(e => e.Name.LocalName.EndsWith("Request", StringComparison.Ordinal)).Name.LocalName[..^"Request".Length];
        using var conteudo = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(Pasta, pedido)));
        conteudo.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=utf-8");
        using var requisicao = new HttpRequestMessage(HttpMethod.Post, "/NfseWS.asmx") { Content = conteudo };
        requisicao.Headers.Add("SOAPAction", $"\"http://notacarioca.rio.gov.br/{operacao}\"");
        using HttpResponseMessage resposta = await _fabrica.CreateClient().SendAsync(requisicao);
        string corpo = await resposta.Content.ReadAsStringAsync();

        Assert.True(resposta.StatusCode == HttpStatusCode.OK, corpo);
        Assert.Equal("text/xml", resposta.Content.Headers.ContentType?.MediaType);
        XDocument esperado = XDocument.Load(Path.Combine(Pasta, pedido[..^".xml".Length] + ".resposta.xml"));
        XDocument obtido = XDocument.Parse(corpo);

        Assert.Equal(Forma(esperado.Root!), Forma(obtido.Root!));
        Assert.Equal(Forma(Negocio(esperado)), Forma(Negocio(obtido)));
        Assert.Equal(Codigos(esperado), Codigos(obtido));
    }

    private void Responder(DecisaoDeCancelamento decisao) =>
        _fabrica.Dados.RespostaDoCancelamento = new ResultadoDoGateway(
            decisao,
            decisao == DecisaoDeCancelamento.NotaNaoEncontrada ? null : new EstadoParaCancelamento(
                decisao == DecisaoDeCancelamento.NotaSubstituida ? SituacaoNota.Substituida : SituacaoNota.Normal,
                new Competencia(2026, 9), decisao == DecisaoDeCancelamento.GuiaPaga),
            decisao == DecisaoDeCancelamento.NotaNaoEncontrada ? null : Agora,
            decisao == DecisaoDeCancelamento.Permitido ? Agora : null);

    /// <summary>O XML de negócio que o legado devolve escapado dentro de <c>outputXML</c>.</summary>
    private static XElement Negocio(XDocument envelope) =>
        XElement.Parse(envelope.Descendants().Single(e => e.Name.LocalName == "outputXML").Value);

    private static string[] Codigos(XDocument envelope) =>
        [.. Negocio(envelope).Descendants("Codigo").Select(c => c.Value)];

    /// <summary>
    /// Nome qualificado e filhos em ordem, recursivo; irmãos consecutivos com a mesma forma (as <c>CompNfse</c> de uma
    /// lista) contam uma vez, para a quantidade de notas da massa de teste não fazer parte do contrato.
    /// </summary>
    internal static string Forma(XElement elemento)
    {
        var filhos = new StringBuilder();
        string? anterior = null;
        foreach (XElement filho in elemento.Elements())
        {
            string forma = Forma(filho);
            if (forma != anterior)
            {
                filhos.Append(forma);
            }

            anterior = forma;
        }

        string atributos = string.Concat(elemento.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => $" @{a.Name}"));
        return filhos.Length == 0 ? $"<{elemento.Name}{atributos}/>" : $"<{elemento.Name}{atributos}>{filhos}</{elemento.Name}>";
    }
}
