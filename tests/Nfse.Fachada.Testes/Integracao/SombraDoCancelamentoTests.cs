using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Nfse.Fachada.Flags;
using Nfse.Fachada.Observabilidade;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Testes.Integracao;

/// <summary>
/// ADR-0009, seções 3 e 5: em <c>sombra</c>, o cancelamento é executado só pelo legado, uma vez; o novo só avalia a
/// decisão no endpoint interno, sem escrever. A fachada nunca repete o cancelamento, nem no legado nem no
/// <c>/NfseWS.asmx</c> do novo.
/// </summary>
public sealed class SombraDoCancelamentoTests : IAsyncLifetime, IDisposable
{
    private const string Pedido = "06-cancelar-nfse.xml";
    private const string Sucesso = "06-cancelar-nfse.resposta.xml";
    private const string Inexistente = "06e-cancelar-nfse-inexistente.resposta.xml";
    private const string Interno = "/interno/sombra/cancelamento";

    /// <summary>CNPJ, IM e motivo do pedido de cancelamento: não podem aparecer em nenhum log.</summary>
    private static readonly string[] DadosPessoais = ["13500075000183", "10000025", "Servico nao prestado"];

    private readonly ConcurrentQueue<(string Instrumento, Dictionary<string, string?> Tags)> _medicoes = new();
    private readonly MeterListener _ouvinte = new();
    private ServidorFalso _legado = null!;
    private ServidorFalso _novo = null!;

    public SombraDoCancelamentoTests()
    {
        _ouvinte.InstrumentPublished = (instrumento, ouvinte) =>
        {
            if (instrumento.Meter.Name == Telemetria.Nome && instrumento.Name.StartsWith("sombra_", StringComparison.Ordinal))
            {
                ouvinte.EnableMeasurementEvents(instrumento);
            }
        };
        _ouvinte.SetMeasurementEventCallback<long>((i, _, tags, _) => Gravar(i, tags));
        _ouvinte.SetMeasurementEventCallback<double>((i, _, tags, _) => Gravar(i, tags));
        _ouvinte.Start();
    }

    public async Task InitializeAsync()
    {
        _legado = await ServidorFalso.IniciarAsync();
        _legado.Responder = SombraTests.Responder(Exemplos.Bytes(Sucesso));
        _novo = await ServidorFalso.IniciarAsync();
        _novo.Responder = (ctx, req) => req.Caminho == Interno
            ? Avaliacao("igual", null)(ctx, req)
            : SombraTests.Responder(Exemplos.Bytes(Sucesso))(ctx, req);
    }

    public async Task DisposeAsync()
    {
        await _legado.DisposeAsync();
        await _novo.DisposeAsync();
    }

    public void Dispose() => _ouvinte.Dispose();

    [Fact]
    public async Task Igual_envia_o_contrato_so_ao_endpoint_interno_e_o_legado_e_chamado_uma_vez()
    {
        await using var fachada = Fachada(Flags(100));
        byte[] pedido = Exemplos.Bytes(Pedido);

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, pedido);

        Assert.Equal(Exemplos.Bytes(Sucesso), await resposta.Content.ReadAsByteArrayAsync());
        await SombraTests.Eventualmente(() => Sombra(fachada).Any(m => m.Contains("Sombra CancelarNfse: igual", StringComparison.Ordinal)));
        RequisicaoRecebida noNovo = Assert.Single(_novo.Recebidas);
        Assert.Equal("POST", noNovo.Metodo);
        Assert.Equal(Interno, noNovo.Caminho);
        using JsonDocument corpo = JsonDocument.Parse(noNovo.Corpo);
        Assert.Equal(Encoding.UTF8.GetString(pedido), corpo.RootElement.GetProperty("envelope").GetString());
        Assert.Equal(Encoding.UTF8.GetString(Exemplos.Bytes(Sucesso)), corpo.RootElement.GetProperty("respostaLegado").GetString());
        Assert.Equal(200, corpo.RootElement.GetProperty("statusLegado").GetInt32());
        Assert.InRange(corpo.RootElement.GetProperty("idadeMs").GetInt64(), 0, 10_000);
        Assert.Single(_legado.Recebidas);
        AssertMedicao("sombra_comparacoes_total", ("resultado", "igual"));
        AssertMedicao("sombra_atraso");
    }

    [Fact]
    public async Task Soap_fault_do_legado_vai_ao_novo_com_o_status()
    {
        _legado.Responder = SombraTests.Responder(Encoding.UTF8.GetBytes("<soap:Fault/>"), StatusCodes.Status500InternalServerError);
        _novo.Responder = Avaliacao("divergente", "indeterminado");
        await using var fachada = Fachada(Flags(100));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes(Pedido));

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        Assert.Contains("divergência indeterminado", await Divergencia(fachada), StringComparison.Ordinal);
        using JsonDocument corpo = JsonDocument.Parse(Assert.Single(_novo.Recebidas).Corpo);
        Assert.Equal(500, corpo.RootElement.GetProperty("statusLegado").GetInt32());
        Assert.Single(_legado.Recebidas);
    }

    [Theory]
    [InlineData("nao_classificada", "nao_classificada")]
    [InlineData(null, "nao_classificada")]
    [InlineData("corrida", "corrida")]
    [InlineData("fronteira_relogio", "fronteira_relogio")]
    [InlineData("indeterminado", "indeterminado")]
    [InlineData("decisao_consciente", "decisao_consciente")]
    public async Task Divergencia_usa_a_classe_do_novo_com_pedido_pseudonimizado(string? classe, string esperada)
    {
        _novo.Responder = Avaliacao("divergente", classe);
        await using var fachada = Fachada(Flags(100));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes(Pedido));

        Assert.Equal(Exemplos.Bytes(Sucesso), await resposta.Content.ReadAsByteArrayAsync());
        string registro = await Divergencia(fachada);
        Assert.Contains($"divergência {esperada} trace", registro, StringComparison.Ordinal);
        Assert.Contains("decisão legado sucesso novo E053", registro, StringComparison.Ordinal);
        Assert.Contains("status legado 200", registro, StringComparison.Ordinal);
        Assert.Matches("requisição <CancelarNfseEnvio><Pedido><InfPedidoCancelamento><IdentificacaoNfse><Numero>1</Numero><Cnpj>hmac:[0-9a-f]{64}</Cnpj>", registro);
        AssertMedicao("sombra_comparacoes_total", ("resultado", "divergente"));
        AssertMedicao("sombra_divergencias_total", ("classe", esperada));
        Assert.Single(_legado.Recebidas);
        Assert.Single(_novo.Recebidas);
        AssertSemDadosPessoais(fachada);
    }

    [Fact]
    public async Task Decisao_fora_do_formato_nao_vai_ao_log()
    {
        _novo.Responder = Responder("{\"resultado\":\"divergente\",\"classe\":null,\"decisaoLegado\":\"CNPJ 13500075000183\",\"decisaoNovo\":\"E053\"}");
        await using var fachada = Fachada(Flags(100));

        (await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes(Pedido))).Dispose();

        Assert.Contains("decisão legado ? novo E053", await Divergencia(fachada), StringComparison.Ordinal);
        AssertSemDadosPessoais(fachada);
    }

    public static TheoryData<string, string> FalhasDoNovo => new()
    {
        { "transporte", "(transporte)" },
        { "timeout", "(timeout)" },
        { "500", "(http_500)" },
        { "404", "(http_404)" },
        { "json", "(resposta_invalida)" },
        { "classe", "(resposta_invalida)" },
        { "resultado", "(resposta_invalida)" },
    };

    [Theory]
    [MemberData(nameof(FalhasDoNovo))]
    public async Task Novo_interno_fora_do_ar_e_erro_novo_e_o_cliente_recebe_o_legado(string falha, string esperado)
    {
        var novo = new NovoComFalha(async (_, ct) => falha switch
        {
            "transporte" => throw new HttpRequestException(HttpRequestError.ConnectionError, "conexão recusada"),
            "timeout" => await Task.Delay(TimeSpan.FromSeconds(30), ct).ContinueWith<HttpResponseMessage>(_ => throw new TaskCanceledException(), TaskScheduler.Default),
            "500" => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("erro") },
            "404" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "json" => Json("não é json"),
            "classe" => Json("{\"resultado\":\"divergente\",\"classe\":\"10000025\"}"),
            _ => Json("{\"resultado\":\"talvez\"}"),
        });
        await using var fachada = Fachada(Flags(100), novo, ("Sombra:Timeout", "00:00:00.200"));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes(Pedido));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(Exemplos.Bytes(Sucesso), await resposta.Content.ReadAsByteArrayAsync());
        await SombraTests.Eventualmente(() => Sombra(fachada).Any(m => m.Contains("erro_novo", StringComparison.Ordinal)));
        Assert.Contains(esperado, Sombra(fachada).Single(m => m.Contains("erro_novo", StringComparison.Ordinal)), StringComparison.Ordinal);
        AssertMedicao("sombra_divergencias_total", ("classe", "erro_novo"));
        Assert.Equal(1, novo.Chamadas);
        Assert.Single(_legado.Recebidas);
    }

    [Fact]
    public async Task Sem_url_interna_descarta_sem_chamar_o_novo()
    {
        await using var fachada = Fachada(Flags(100), _novo.CriarHandler(), ("Novo:UrlInterna", null));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes(Pedido));

        Assert.Equal(Exemplos.Bytes(Sucesso), await resposta.Content.ReadAsByteArrayAsync());
        await SombraTests.Eventualmente(() => Sombra(fachada).Any(m => m.Contains("cópia descartada (sem_url_interna)", StringComparison.Ordinal)));
        AssertMedicao("sombra_descartadas_total", ("motivo", "sem_url_interna"));
        Assert.Empty(_novo.Recebidas);
        Assert.Single(_legado.Recebidas);
    }

    [Fact]
    public async Task Chave_rara_vai_para_a_sombra_mesmo_com_amostragem_0()
    {
        _legado.Responder = (ctx, req) => SombraTests.Responder(req.Corpo.AsSpan().IndexOf("&lt;Numero&gt;999"u8) >= 0
            ? Exemplos.Bytes(Inexistente)
            : Exemplos.Bytes(Sucesso))(ctx, req);
        await using var fachada = Fachada(Flags(0));
        byte[] inexistente = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Exemplos.Bytes(Pedido)).Replace("&lt;Numero&gt;1&lt;", "&lt;Numero&gt;999&lt;", StringComparison.Ordinal));

        // Sucesso (chave frequente) com amostragem 0: não vai. E050 (rara): vai sempre.
        (await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes(Pedido))).Dispose();
        (await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, inexistente)).Dispose();

        await SombraTests.Eventualmente(() => Sombra(fachada).Any(m => m.Contains("Sombra CancelarNfse: igual", StringComparison.Ordinal)));
        using JsonDocument corpo = JsonDocument.Parse(Assert.Single(_novo.Recebidas).Corpo);
        Assert.Contains("E050", corpo.RootElement.GetProperty("respostaLegado").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, _legado.Recebidas.Count);
    }

    [Fact]
    public async Task Cada_cancelamento_chega_uma_vez_ao_legado_e_nunca_ao_soap_do_novo()
    {
        // Divergente em toda requisição: é onde a sombra de leitura reexecutaria legado e novo. Aqui, nada repete.
        _novo.Responder = Avaliacao("divergente", "nao_classificada");
        await using var fachada = Fachada(Flags(100), ("Sombra:ReexecucoesPorSegundo", "1000"));
        const int Requisicoes = 5;

        for (int i = 0; i < Requisicoes; i++)
        {
            (await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes(Pedido))).Dispose();
        }

        await SombraTests.Eventualmente(() => Sombra(fachada).Count(m => m.Contains("divergência", StringComparison.Ordinal)) == Requisicoes);
        await Task.Delay(200);
        Assert.Equal(Requisicoes, _legado.Recebidas.Count);
        Assert.All(_legado.Recebidas, r => Assert.Equal("/NfseWS.asmx", r.Caminho));
        Assert.Equal(Requisicoes, _novo.Recebidas.Count);
        Assert.All(_novo.Recebidas, r => Assert.Equal(Interno, r.Caminho));
        Assert.DoesNotContain(_medicoes, m => m.Instrumento == "sombra_reexecucoes_total" && m.Tags["operacao"] == OperacoesSoap.CancelarNfse);
    }

    [Fact]
    public async Task Leitura_em_sombra_continua_indo_ao_soap_do_novo_e_nao_ao_interno()
    {
        _legado.Responder = SombraTests.Responder(Exemplos.Bytes("04-consultar-nfse.resposta.xml"));
        _novo.Responder = SombraTests.Responder(Exemplos.Bytes("04-consultar-nfse.resposta.xml"));
        await using var fachada = Fachada(Flags(100));

        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes("04-consultar-nfse.xml"))).Dispose();

        await SombraTests.Eventualmente(() => Sombra(fachada).Any(m => m.Contains("Sombra ConsultarNfse: igual", StringComparison.Ordinal)));
        Assert.Equal("/NfseWS.asmx", Assert.Single(_novo.Recebidas).Caminho);
    }

    [Fact]
    public async Task Em_modo_novo_nada_muda_e_nao_ha_sombra_invertida()
    {
        _legado.Responder = SombraTests.Responder(Exemplos.Bytes("04-consultar-nfse.resposta.xml"));
        _novo.Responder = SombraTests.Responder(Exemplos.Bytes("04-consultar-nfse.resposta.xml"));
        FlagsDeMigracao flags = Flags(100);
        flags.Operacoes[OperacoesSoap.CancelarNfse].Modo = ModoDeMigracao.Novo;
        flags.Operacoes[OperacoesSoap.CancelarNfse].Percentual = 100;
        await using var fachada = Fachada(flags);

        (await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes(Pedido))).Dispose();

        // Uma leitura em sombra depois: quando ela é comparada, uma eventual cópia do cancelamento já teria passado (FIFO).
        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes("04-consultar-nfse.xml"))).Dispose();
        await SombraTests.Eventualmente(() => Sombra(fachada).Any(m => m.Contains("Sombra ConsultarNfse: igual", StringComparison.Ordinal)));

        Assert.Equal(["/NfseWS.asmx", "/NfseWS.asmx"], _novo.Recebidas.Select(r => r.Caminho));
        Assert.Equal(Exemplos.SoapAction(OperacoesSoap.CancelarNfse), _novo.Recebidas.First().SoapAction);
        Assert.Equal(Exemplos.SoapAction(OperacoesSoap.ConsultarNfse), Assert.Single(_legado.Recebidas).SoapAction);
        Assert.DoesNotContain(Sombra(fachada), m => m.Contains("Sombra CancelarNfse", StringComparison.Ordinal));
    }

    private static FlagsDeMigracao Flags(int amostragem) => SombraTests.Flags(amostragem);

    private static Func<HttpContext, RequisicaoRecebida, Task> Avaliacao(string resultado, string? classe) =>
        Responder(JsonSerializer.Serialize(new
        {
            resultado,
            classe,
            decisaoLegado = resultado == "igual" ? "E053" : "sucesso",
            decisaoNovo = "E053",
        }));

    private static Func<HttpContext, RequisicaoRecebida, Task> Responder(string json) =>
        (ctx, _) =>
        {
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync(json);
        };

    private static HttpResponseMessage Json(string corpo) =>
        new(HttpStatusCode.OK) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };

    private static IEnumerable<string> Sombra(FabricaDaFachada fachada) =>
        fachada.Logs.Mensagens.Where(m => m.Contains("|Sombra ", StringComparison.Ordinal));

    private static async Task<string> Divergencia(FabricaDaFachada fachada)
    {
        await SombraTests.Eventualmente(() => Sombra(fachada).Any(m => m.Contains("divergência", StringComparison.Ordinal)));
        return Sombra(fachada).Single(m => m.Contains("divergência", StringComparison.Ordinal));
    }

    private static void AssertSemDadosPessoais(FabricaDaFachada fachada)
    {
        foreach (string mensagem in fachada.Logs.Mensagens)
        {
            foreach (string dado in DadosPessoais)
            {
                Assert.DoesNotContain(dado, mensagem, StringComparison.Ordinal);
            }
        }
    }

    private void AssertMedicao(string instrumento, params (string Chave, string Valor)[] tags) =>
        Assert.Contains(_medicoes, m =>
            m.Instrumento == instrumento
            && m.Tags.TryGetValue("operacao", out string? op) && op == OperacoesSoap.CancelarNfse
            && tags.All(t => m.Tags.TryGetValue(t.Chave, out string? v) && v == t.Valor));

    private void Gravar(Instrument instrumento, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dicionario = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            dicionario[tag.Key] = tag.Value?.ToString();
        }

        _medicoes.Enqueue((instrumento.Name, dicionario));
    }

    private FabricaDaFachada Fachada(FlagsDeMigracao flags, params (string Chave, string? Valor)[] configuracao) =>
        Fachada(flags, _novo.CriarHandler(), configuracao);

    private FabricaDaFachada Fachada(FlagsDeMigracao flags, HttpMessageHandler novo, params (string Chave, string? Valor)[] configuracao)
    {
        var valores = new Dictionary<string, string?>
        {
            ["Novo:UrlInterna"] = "http://novo.test:8081",
            ["Sombra:ChaveHmac"] = "chave-de-teste-da-sombra",
            ["Logging:LogLevel:Nfse.Fachada"] = "Debug",
        };
        foreach ((string chave, string? valor) in configuracao)
        {
            valores[chave] = valor;
        }

        return new FabricaDaFachada(_legado.CriarHandler(), novo, new FlagsFixas(flags), valores);
    }
}
