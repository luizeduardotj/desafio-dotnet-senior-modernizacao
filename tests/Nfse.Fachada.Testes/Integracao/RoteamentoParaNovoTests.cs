using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Testes.Integracao;

/// <summary>ADR-0008, "Modo novo e falhas": fallback só em leitura; cancelamento sem retry depois do envio.</summary>
public sealed class RoteamentoParaNovoTests : IAsyncLifetime
{
    private const string Consulta = "04-consultar-nfse.xml";
    private const string Cancelamento = "06-cancelar-nfse.xml";

    private ServidorFalso _legado = null!;
    private ServidorFalso _novo = null!;

    public async Task InitializeAsync()
    {
        _legado = await ServidorFalso.IniciarAsync();
        _legado.Responder = (ctx, _) => ctx.Response.WriteAsync("do-legado");
        _novo = await ServidorFalso.IniciarAsync();
        _novo.Responder = (ctx, _) => ctx.Response.WriteAsync("do-novo");
    }

    public async Task DisposeAsync()
    {
        await _legado.DisposeAsync();
        await _novo.DisposeAsync();
    }

    public static TheoryData<string, string> TresOperacoes => new()
    {
        { OperacoesSoap.ConsultarNfse, Consulta },
        { OperacoesSoap.ConsultarNfseServicoTomado, "05-consultar-nfse-servico-tomado.xml" },
        { OperacoesSoap.CancelarNfse, Cancelamento },
    };

    [Theory]
    [MemberData(nameof(TresOperacoes))]
    public async Task Novo_100_atende_com_os_mesmos_bytes(string operacao, string pedido)
    {
        await using var fachada = Fachada(_novo.CriarHandler(), PassThroughTests.Flags("novo", 100));
        byte[] corpo = Exemplos.Bytes(pedido);

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(operacao, corpo);

        Assert.Equal("do-novo", await resposta.Content.ReadAsStringAsync());
        RequisicaoRecebida recebida = Assert.Single(_novo.Recebidas);
        Assert.Equal(corpo, recebida.Corpo);
        Assert.Equal(Exemplos.SoapAction(operacao), recebida.SoapAction);
        Assert.Empty(_legado.Recebidas);
    }

    [Fact]
    public async Task Lista_de_inclusao_com_a_IM_extraida_vai_ao_novo_e_as_outras_ficam()
    {
        var flags = PassThroughTests.Flags("novo", 0);
        flags.Operacoes[OperacoesSoap.CancelarNfse].Inclusao = ["10000025"];
        await using var fachada = Fachada(_novo.CriarHandler(), flags);

        using HttpResponseMessage incluida = await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes("06-cancelar-nfse.xml"));
        using HttpResponseMessage outra = await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes("06c-cancelar-nfse-prazo-expirado.xml"));

        Assert.Equal("do-novo", await incluida.Content.ReadAsStringAsync());
        Assert.Equal("do-legado", await outra.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Chave_nao_extraida_vai_ao_legado_em_canario_e_ao_novo_em_100()
    {
        byte[] comDtd = Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a \"b\">]><Envelope><inputXML>&a;</inputXML></Envelope>");

        await using (var canario = Fachada(_novo.CriarHandler(), PassThroughTests.Flags("novo", 99)))
        {
            using HttpResponseMessage resposta = await canario.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, comDtd);
            Assert.Equal("do-legado", await resposta.Content.ReadAsStringAsync());
        }

        await using (var total = Fachada(_novo.CriarHandler(), PassThroughTests.Flags("novo", 100)))
        {
            using HttpResponseMessage resposta = await total.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, comDtd);
            Assert.Equal("do-novo", await resposta.Content.ReadAsStringAsync());
            Assert.Equal(comDtd, Assert.Single(_novo.Recebidas).Corpo);
        }
    }

    [Fact]
    public async Task Novo_sem_url_configurada_cai_no_legado()
    {
        await using var fachada = new FabricaDaFachada(_legado.CriarHandler(), null, new FlagsFixas(PassThroughTests.Flags("novo", 100)));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));

        Assert.Equal("do-legado", await resposta.Content.ReadAsStringAsync());
    }

    public static TheoryData<string> FalhasDoNovo => ["502", "503", "504", "conexao recusada", "conexao caiu depois do envio", "timeout"];

    [Theory]
    [MemberData(nameof(FalhasDoNovo))]
    public async Task Leitura_com_falha_no_novo_tenta_uma_vez_no_legado(string falha)
    {
        NovoComFalha novo = Novo(falha);
        await using var fachada = Fachada(novo, PassThroughTests.Flags("novo", 100));
        byte[] corpo = Exemplos.Bytes(Consulta);

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, corpo);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("do-legado", await resposta.Content.ReadAsStringAsync());
        Assert.Equal(1, novo.Chamadas);
        Assert.Equal(corpo, Assert.Single(_legado.Recebidas).Corpo);
    }

    [Theory]
    [InlineData("soap:Client")]
    [InlineData("soap:Server")]
    public async Task Leitura_com_fault_500_do_novo_e_a_resposta_do_novo(string faultcode)
    {
        // C20: o novo devolve soap:Fault com HTTP 500 de propósito, como o legado. Não é falha de transporte.
        string fault = $"<soap:Envelope><soap:Body><soap:Fault><faultcode>{faultcode}</faultcode></soap:Fault></soap:Body></soap:Envelope>";
        var novo = new NovoComFalha(async (req, ct) =>
        {
            await req.Content!.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(fault) };
        });
        await using var fachada = Fachada(novo, PassThroughTests.Flags("novo", 100));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        Assert.Equal(fault, await resposta.Content.ReadAsStringAsync());
        Assert.Equal(1, novo.Chamadas);
        Assert.Empty(_legado.Recebidas);
    }

    [Fact]
    public async Task Leitura_com_falha_no_novo_e_no_legado_nao_repete_de_novo()
    {
        NovoComFalha novo = Novo("503");
        _legado.Responder = (ctx, _) =>
        {
            ctx.Response.StatusCode = 503;
            return Task.CompletedTask;
        };
        await using var fachada = Fachada(novo, PassThroughTests.Flags("novo", 100));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
        Assert.Equal(1, novo.Chamadas);
        Assert.Single(_legado.Recebidas);
    }

    [Theory]
    [InlineData("500")]
    [InlineData("502")]
    [InlineData("503")]
    [InlineData("504")]
    public async Task Cancelamento_com_5xx_do_novo_devolve_o_5xx_sem_retry_nem_fallback(string falha)
    {
        NovoComFalha novo = Novo(falha);
        await using var fachada = Fachada(novo, PassThroughTests.Flags("novo", 100));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes(Cancelamento));

        Assert.Equal(int.Parse(falha, System.Globalization.CultureInfo.InvariantCulture), (int)resposta.StatusCode);
        Assert.Equal("erro-do-novo", await resposta.Content.ReadAsStringAsync());
        Assert.Equal(1, novo.Chamadas);
        Assert.Empty(_legado.Recebidas);
    }

    [Theory]
    [InlineData("conexao caiu depois do envio")]
    [InlineData("timeout")]
    public async Task Cancelamento_enviado_com_falha_devolve_fault_sem_retry_nem_fallback(string falha)
    {
        NovoComFalha novo = Novo(falha);
        await using var fachada = Fachada(novo, PassThroughTests.Flags("novo", 100));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes(Cancelamento));

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        string corpo = await resposta.Content.ReadAsStringAsync();
        Assert.Contains("<faultcode>soap:Server</faultcode>", corpo, StringComparison.Ordinal);
        Assert.StartsWith("text/xml", resposta.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, novo.Chamadas);
        Assert.Empty(_legado.Recebidas);
    }

    [Fact]
    public async Task Cancelamento_com_conexao_que_nem_abriu_vai_ao_legado()
    {
        NovoComFalha novo = Novo("conexao recusada");
        await using var fachada = Fachada(novo, PassThroughTests.Flags("novo", 100));
        byte[] corpo = Exemplos.Bytes(Cancelamento);

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, corpo);

        Assert.Equal("do-legado", await resposta.Content.ReadAsStringAsync());
        Assert.Equal(1, novo.Chamadas);
        Assert.Equal(corpo, Assert.Single(_legado.Recebidas).Corpo);
    }

    [Fact]
    public async Task Logs_nao_tem_IM_nem_documento()
    {
        await using var fachada = Fachada(_novo.CriarHandler(), PassThroughTests.Flags("novo", 100));
        NovoComFalha falho = Novo("503");
        await using var comFallback = Fachada(falho, PassThroughTests.Flags("novo", 100));

        foreach ((string operacao, string pedido) in new[]
        {
            (OperacoesSoap.ConsultarNfse, "04e-consultar-nfse-por-tomador.xml"),
            (OperacoesSoap.ConsultarNfseServicoTomado, "05-consultar-nfse-servico-tomado.xml"),
            (OperacoesSoap.CancelarNfse, Cancelamento),
        })
        {
            (await fachada.EnviarSoapAsync(operacao, Exemplos.Bytes(pedido))).Dispose();
            (await comFallback.EnviarSoapAsync(operacao, Exemplos.Bytes(pedido))).Dispose();
        }

        string[] logs = [.. fachada.Logs.Mensagens, .. comFallback.Logs.Mensagens];
        Assert.Contains(logs, l => l.Contains("ConsultarNfseServicoTomado", StringComparison.Ordinal));
        foreach (string dado in new[] { "10000001", "10000025", "11100003000113", "13500075000183", "01234567000195", "1234567000195" })
        {
            Assert.DoesNotContain(logs, l => l.Contains(dado, StringComparison.Ordinal));
        }
    }

    private FabricaDaFachada Fachada(HttpMessageHandler novo, Flags.FlagsDeMigracao flags) =>
        new(_legado.CriarHandler(), novo, new FlagsFixas(flags));

    private static NovoComFalha Novo(string falha) => falha switch
    {
        "500" or "502" or "503" or "504" => new NovoComFalha(async (req, ct) =>
        {
            await req.Content!.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage((HttpStatusCode)int.Parse(falha, System.Globalization.CultureInfo.InvariantCulture))
            {
                Content = new StringContent("erro-do-novo"),
            };
        }),
        "conexao recusada" => new NovoComFalha((_, _) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (novo.test:80)")),
        "conexao caiu depois do envio" => new NovoComFalha(async (req, ct) =>
        {
            await req.Content!.ReadAsByteArrayAsync(ct);
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
        }),
        "timeout" => new NovoComFalha(async (req, ct) =>
        {
            await req.Content!.ReadAsByteArrayAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(falha)),
    };
}
