using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Security;
using System.Text;
using System.Text.Json;
using Nfse.Aplicacao.Cancelamento;
using Nfse.Dominio.Cancelamento;
using Nfse.Dominio.Notas;

namespace Nfse.Host.Testes;

public class CancelamentoTests : IClassFixture<FabricaDoHost>
{
    private static readonly DateTime Agora = new(2026, 10, 9, 15, 0, 0);
    private readonly FabricaDoHost _fabrica;

    public CancelamentoTests(FabricaDoHost fabrica)
    {
        _fabrica = fabrica;
        Responder(DecisaoDeCancelamento.Permitido);
    }

    private void Responder(DecisaoDeCancelamento decisao, SituacaoNota antes = SituacaoNota.Normal, bool guiaPaga = false, DateTime? dataCancelamento = null) =>
        _fabrica.Dados.RespostaDoCancelamento = new ResultadoDoGateway(
            decisao,
            decisao == DecisaoDeCancelamento.NotaNaoEncontrada ? null : new EstadoParaCancelamento(antes, new Competencia(2026, 10), guiaPaga),
            decisao == DecisaoDeCancelamento.NotaNaoEncontrada ? null : Agora,
            dataCancelamento ?? (decisao is DecisaoDeCancelamento.Permitido or DecisaoDeCancelamento.JaCancelada ? Agora : null));

    private static string Pedido(string numero = "1", string? motivo = "Servico nao prestado", bool comCodigo = true, string im = "10000001") =>
        "<CancelarNfseEnvio><Pedido><InfPedidoCancelamento><IdentificacaoNfse>" +
        $"<Numero>{numero}</Numero><Cnpj>00000000000000</Cnpj><InscricaoMunicipal>{im}</InscricaoMunicipal></IdentificacaoNfse>" +
        (comCodigo ? "<CodigoCancelamento>2</CodigoCancelamento>" : "") +
        (motivo is null ? "" : $"<Motivo>{motivo}</Motivo>") +
        "</InfPedidoCancelamento></Pedido></CancelarNfseEnvio>";

    private async Task<string> SoapAsync(string entrada)
    {
        string envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>" +
            $"<CancelarNfseRequest xmlns=\"http://notacarioca.rio.gov.br/\"><inputXML>{SecurityElement.Escape(entrada)}</inputXML></CancelarNfseRequest></soap:Body></soap:Envelope>";
        using var conteudo = new StringContent(envelope, Encoding.UTF8, "text/xml");
        using var pedido = new HttpRequestMessage(HttpMethod.Post, "/NfseWS.asmx") { Content = conteudo };
        pedido.Headers.Add("SOAPAction", "\"http://notacarioca.rio.gov.br/CancelarNfse\"");
        using HttpResponseMessage resposta = await _fabrica.CreateClient().SendAsync(pedido);
        return await resposta.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task K10_sucesso_ecoa_o_pedido_sem_protocolo()
    {
        string corpo = await SoapAsync(Pedido());

        Assert.Contains(
            SecurityElement.Escape("<CancelarNfseResposta><Cancelamento><Confirmacao><Pedido><InfPedidoCancelamento><IdentificacaoNfse><Numero>1</Numero>" +
            "<Cnpj>00000000000000</Cnpj><InscricaoMunicipal>10000001</InscricaoMunicipal></IdentificacaoNfse><CodigoCancelamento>2</CodigoCancelamento>" +
            "</InfPedidoCancelamento></Pedido><DataHora>2026-10-09T15:00:00</DataHora></Confirmacao></Cancelamento></CancelarNfseResposta>"),
            corpo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DecisaoDeCancelamento.NotaNaoEncontrada, "E050")]
    [InlineData(DecisaoDeCancelamento.NotaSubstituida, "E051")]
    [InlineData(DecisaoDeCancelamento.PrazoExpirado, "E052")]
    [InlineData(DecisaoDeCancelamento.GuiaPaga, "E053")]
    public async Task Decisao_do_plsql_vira_o_codigo_do_legado(DecisaoDeCancelamento decisao, string codigo)
    {
        Responder(decisao, antes: decisao == DecisaoDeCancelamento.NotaSubstituida ? SituacaoNota.Substituida : SituacaoNota.Normal,
            guiaPaga: decisao == DecisaoDeCancelamento.GuiaPaga);

        Assert.Contains($"&lt;Codigo&gt;{codigo}&lt;/Codigo&gt;", await SoapAsync(Pedido()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task E001_numero_nao_numerico_e_E010_prestador_inexistente()
    {
        Assert.Contains("&lt;Codigo&gt;E001&lt;/Codigo&gt;", await SoapAsync(Pedido(numero: "abc")), StringComparison.Ordinal);
        Assert.Contains("&lt;Codigo&gt;E010&lt;/Codigo&gt;", await SoapAsync(Pedido(im: "99999999")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task K05_K06_codigo_padrao_1_e_motivo_padrao()
    {
        string corpo = await SoapAsync(Pedido(motivo: null, comCodigo: false));

        Assert.Contains("&lt;CodigoCancelamento&gt;1&lt;/CodigoCancelamento&gt;", corpo, StringComparison.Ordinal);
        Assert.Equal("Cancelamento solicitado pelo contribuinte", _fabrica.Dados.UltimoCancelamento!.Value.Motivo);
    }

    [Fact]
    public async Task Dual_run_conta_quando_o_dominio_discorda_do_plsql()
    {
        long contagem = 0;
        using var ouvinte = new MeterListener();
        ouvinte.InstrumentPublished = (instrumento, l) =>
        {
            if (instrumento.Name == "cancelamento_regra_divergente_total")
            {
                l.EnableMeasurementEvents(instrumento);
            }
        };
        ouvinte.SetMeasurementEventCallback<long>((_, valor, _, _) => Interlocked.Add(ref contagem, valor));
        ouvinte.Start();

        // PL/SQL diz prazo expirado, mas o snapshot está dentro do prazo: o domínio diria "permitido"
        Responder(DecisaoDeCancelamento.PrazoExpirado);
        string corpo = await SoapAsync(Pedido());

        Assert.Contains("&lt;Codigo&gt;E052&lt;/Codigo&gt;", corpo, StringComparison.Ordinal); // vale o PL/SQL
        Assert.Equal(1, Interlocked.Read(ref contagem));
    }

    private HttpClient Rest(string inscricoes = "10000001")
    {
        HttpClient cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add(AutenticacaoDeTeste.Header, inscricoes);
        return cliente;
    }

    private const string UrlRest = "/api/v1/prestadores/10000001/nfse/1/cancelamento";

    [Fact]
    public async Task Rest_sucesso_e_idempotente()
    {
        Responder(DecisaoDeCancelamento.JaCancelada, antes: SituacaoNota.Cancelada, dataCancelamento: new DateTime(2026, 7, 24, 12, 33, 0));

        HttpResponseMessage resposta = await Rest().PostAsJsonAsync(UrlRest, new { motivo = "erro de digitação" });
        JsonElement corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.True(corpo.GetProperty("jaEstavaCancelada").GetBoolean());
        Assert.Equal("2026-07-24T12:33:00-03:00", corpo.GetProperty("dataHora").GetString());
    }

    [Theory]
    [InlineData(DecisaoDeCancelamento.NotaNaoEncontrada, HttpStatusCode.NotFound)]
    [InlineData(DecisaoDeCancelamento.NotaSubstituida, HttpStatusCode.Conflict)]
    [InlineData(DecisaoDeCancelamento.PrazoExpirado, HttpStatusCode.UnprocessableEntity)]
    [InlineData(DecisaoDeCancelamento.GuiaPaga, HttpStatusCode.UnprocessableEntity)]
    public async Task Rest_negativas_em_problem_details(DecisaoDeCancelamento decisao, HttpStatusCode status)
    {
        Responder(decisao);

        HttpResponseMessage resposta = await Rest().PostAsJsonAsync(UrlRest, new { motivo = "x" });

        Assert.Equal(status, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task K11_motivo_acima_de_255_bytes_e_400_antes_do_banco()
    {
        _fabrica.Dados.RespostaDoCancelamento = _fabrica.Dados.RespostaDoCancelamento;
        HttpResponseMessage resposta = await Rest().PostAsJsonAsync(UrlRest, new { motivo = new string('ç', 200) }); // 400 bytes

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Rest_outro_prestador_e_403()
    {
        HttpResponseMessage resposta = await Rest("10000002").PostAsJsonAsync(UrlRest, new { motivo = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }
}
