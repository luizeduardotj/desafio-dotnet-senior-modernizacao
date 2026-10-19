using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Nfse.Fachada.Flags;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Testes.Integracao;

/// <summary>ADR-0009, seções 1, 2 e 4: sombra de leitura com legado e novo falsos.</summary>
public sealed class SombraTests : IAsyncLifetime
{
    private const string Consulta = "04-consultar-nfse.xml";
    private const string RespostaDaConsulta = "04-consultar-nfse.resposta.xml";
    private const string ServicoTomado = "05-consultar-nfse-servico-tomado.xml";
    private const string RespostaDoServicoTomado = "05-consultar-nfse-servico-tomado.resposta.xml";
    private const string ChaveHmac = "chave-de-teste-da-sombra";

    /// <summary>CNPJs, IMs e razões sociais dos exemplos: não podem aparecer em nenhum log.</summary>
    private static readonly string[] DadosPessoais =
    [
        "11100003000113", "10000001", "PRESTADORA EXEMPLO", "TOMADORA EXEMPLO", "3703701000185", "2469134000181",
        "01234567000195", "10000025",
    ];

    private readonly TaskCompletionSource _liberarNovo = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ServidorFalso _legado = null!;
    private ServidorFalso _novo = null!;

    public async Task InitializeAsync()
    {
        _legado = await ServidorFalso.IniciarAsync();
        _legado.Responder = Responder(Exemplos.Bytes(RespostaDaConsulta));
        _novo = await ServidorFalso.IniciarAsync();
        _novo.Responder = Responder(Exemplos.Bytes(RespostaDaConsulta));
    }

    public async Task DisposeAsync()
    {
        _liberarNovo.TrySetResult();
        await _legado.DisposeAsync();
        await _novo.DisposeAsync();
    }

    [Fact]
    public async Task Respostas_iguais_contam_como_igual_e_o_novo_recebe_a_mesma_requisicao()
    {
        await using var fachada = Fachada(Flags(100));
        byte[] pedido = Exemplos.Bytes(Consulta);

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, pedido);

        Assert.Equal(Exemplos.Bytes(RespostaDaConsulta), await resposta.Content.ReadAsByteArrayAsync());
        await Eventualmente(() => Sombra(fachada).Any(m => m.Contains("Sombra ConsultarNfse: igual", StringComparison.Ordinal)));
        RequisicaoRecebida noNovo = Assert.Single(_novo.Recebidas);
        Assert.Equal(pedido, noNovo.Corpo);
        Assert.Equal("/NfseWS.asmx", noNovo.Caminho);
        Assert.Equal(Exemplos.SoapAction(OperacoesSoap.ConsultarNfse), noNovo.SoapAction);
        Assert.Single(_legado.Recebidas);
    }

    [Fact]
    public async Task Divergencia_que_persiste_na_reexecucao_fica_nao_classificada_com_hashes_caminhos_e_pedido_pseudonimizado()
    {
        byte[] doLegado = Exemplos.Bytes(RespostaDaConsulta);
        byte[] doNovo = TrocarPrimeiro(doLegado, "&lt;Numero&gt;23&lt;/Numero&gt;", "&lt;Numero&gt;99&lt;/Numero&gt;");
        _novo.Responder = Responder(doNovo);
        await using var fachada = Fachada(Flags(100));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));

        Assert.Equal(doLegado, await resposta.Content.ReadAsByteArrayAsync());
        string registro = await Divergencia(fachada);
        Assert.Contains("divergência nao_classificada (regra -)", registro, StringComparison.Ordinal);
        Assert.Contains("status 200/200", registro, StringComparison.Ordinal);
        Assert.Contains(Sha256(doLegado), registro, StringComparison.Ordinal);
        Assert.Contains(Sha256(doNovo), registro, StringComparison.Ordinal);
        Assert.Contains("caminhos /ConsultarNfseResposta/ListaNfse[1]/CompNfse[1]/Nfse[1]/InfNfse[1]/Numero[1]/text() requisição", registro, StringComparison.Ordinal);
        Assert.Matches("requisição <ConsultarNfseEnvio><Prestador><Cnpj>hmac:[0-9a-f]{64}</Cnpj>", registro);
        Assert.Contains("<DataInicial>2026-10-01</DataInicial><DataFinal>2026-10-31</DataFinal></PeriodoEmissao><Pagina>1</Pagina>", registro, StringComparison.Ordinal);

        // Reexecutou uma vez legado e novo.
        await Eventualmente(() => _legado.Recebidas.Count == 2 && _novo.Recebidas.Count == 2);
        AssertSemDadosPessoais(fachada);
    }

    [Fact]
    public async Task Divergencia_que_some_na_reexecucao_e_corrida()
    {
        // A nota 23 foi emitida entre a leitura do legado e a do novo: na reexecução os dois já a enxergam.
        byte[] antes = TrocarPrimeiro(Exemplos.Bytes(RespostaDaConsulta), "&lt;Numero&gt;23&lt;/Numero&gt;", "&lt;Numero&gt;98&lt;/Numero&gt;");
        byte[] depois = Exemplos.Bytes(RespostaDaConsulta);
        int chamadas = 0;
        _legado.Responder = (ctx, req) => Responder(Interlocked.Increment(ref chamadas) == 1 ? antes : depois)(ctx, req);
        _novo.Responder = Responder(depois);
        await using var fachada = Fachada(Flags(100));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));

        Assert.Equal(antes, await resposta.Content.ReadAsByteArrayAsync());
        Assert.Contains("divergência corrida", await Divergencia(fachada), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reexecucao_acima_do_limite_de_taxa_fica_nao_classificada_sem_nova_leitura()
    {
        _novo.Responder = Responder(TrocarPrimeiro(Exemplos.Bytes(RespostaDaConsulta), "&lt;Numero&gt;23&lt;/Numero&gt;", "&lt;Numero&gt;99&lt;/Numero&gt;"));
        await using var fachada = Fachada(Flags(100), ("Sombra:ReexecucoesPorSegundo", "0"));

        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta))).Dispose();

        Assert.Contains("divergência nao_classificada", await Divergencia(fachada), StringComparison.Ordinal);
        Assert.Single(_legado.Recebidas);
        Assert.Single(_novo.Recebidas);
    }

    [Fact]
    public async Task Novo_lento_nao_atrasa_o_cliente()
    {
        _novo.Responder = async (ctx, req) =>
        {
            await _liberarNovo.Task.WaitAsync(ctx.RequestAborted);
            await Responder(Exemplos.Bytes(RespostaDaConsulta))(ctx, req);
        };
        await using var fachada = Fachada(Flags(100), ("Sombra:Timeout", "00:00:30"));

        var cronometro = Stopwatch.StartNew();
        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));
        byte[] corpo = await resposta.Content.ReadAsByteArrayAsync();
        cronometro.Stop();

        // O cliente já tem a resposta do legado e o novo ainda não respondeu (está esperando ser liberado).
        Assert.Equal(Exemplos.Bytes(RespostaDaConsulta), corpo);
        Assert.False(_liberarNovo.Task.IsCompleted);
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(5), $"cliente esperou {cronometro.Elapsed}");
        await Eventualmente(() => _novo.Recebidas.Count == 1);
        Assert.DoesNotContain(Sombra(fachada), m => m.Contains("igual", StringComparison.Ordinal));

        _liberarNovo.SetResult();
        await Eventualmente(() => Sombra(fachada).Any(m => m.Contains("Sombra ConsultarNfse: igual", StringComparison.Ordinal)));
    }

    public static TheoryData<string, string> FalhasDoNovo => new()
    {
        { "transporte", "transporte" },
        { "timeout", "timeout" },
        { "503", "regra -" },
    };

    [Theory]
    [MemberData(nameof(FalhasDoNovo))]
    public async Task Novo_fora_do_ar_e_erro_novo_e_o_cliente_recebe_o_legado(string falha, string esperado)
    {
        var novo = new NovoComFalha(async (_, ct) => falha switch
        {
            "transporte" => throw new HttpRequestException(HttpRequestError.ConnectionError, "conexão recusada"),
            "timeout" => await Task.Delay(TimeSpan.FromSeconds(30), ct).ContinueWith<HttpResponseMessage>(_ => throw new TaskCanceledException(), TaskScheduler.Default),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("indisponivel") },
        });
        await using var fachada = Fachada(Flags(100), novo, ("Sombra:Timeout", "00:00:00.200"));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(Exemplos.Bytes(RespostaDaConsulta), await resposta.Content.ReadAsByteArrayAsync());
        await Eventualmente(() => Sombra(fachada).Any(m => m.Contains("erro_novo", StringComparison.Ordinal)));
        string registro = Sombra(fachada).Single(m => m.Contains("erro_novo", StringComparison.Ordinal));
        Assert.Contains(esperado, registro, StringComparison.Ordinal);
        Assert.Single(_legado.Recebidas);
    }

    [Fact]
    public async Task Canal_cheio_descarta_sem_bloquear_o_cliente()
    {
        _novo.Responder = async (ctx, req) =>
        {
            await _liberarNovo.Task.WaitAsync(ctx.RequestAborted);
            await Responder(Exemplos.Bytes(RespostaDaConsulta))(ctx, req);
        };
        await using var fachada = Fachada(
            Flags(100), ("Sombra:CapacidadeDoCanal", "1"), ("Sombra:Concorrencia", "1"), ("Sombra:Timeout", "00:00:30"));

        var cronometro = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
        {
            using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));
            Assert.Equal(Exemplos.Bytes(RespostaDaConsulta), await resposta.Content.ReadAsByteArrayAsync());
        }

        cronometro.Stop();

        // Um no worker (preso no novo), no máximo um no canal: pelo menos três descartados.
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(5), $"5 requisições levaram {cronometro.Elapsed}");
        Assert.True(Sombra(fachada).Count(m => m.Contains("cópia descartada (canal_cheio)", StringComparison.Ordinal)) >= 3);
        Assert.False(_liberarNovo.Task.IsCompleted);
    }

    [Fact]
    public async Task Resposta_do_legado_acima_de_1_MiB_chega_inteira_ao_cliente_e_e_descartada()
    {
        byte[] grande = new byte[(1024 * 1024) + 10];
        Array.Fill(grande, (byte)'x');
        _legado.Responder = Responder(grande);
        await using var fachada = Fachada(Flags(100));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));

        Assert.Equal(grande, await resposta.Content.ReadAsByteArrayAsync());
        await Eventualmente(() => Sombra(fachada).Any(m => m.Contains("cópia descartada (resposta_grande)", StringComparison.Ordinal)));
        Assert.Empty(_novo.Recebidas);
    }

    [Fact]
    public async Task Chave_rara_vai_para_a_sombra_mesmo_com_amostragem_0()
    {
        byte[] e030 = Encoding.UTF8.GetBytes(RespostaComCodigo("ConsultarNfse", "E030"));
        _legado.Responder = (ctx, req) => Responder(req.Corpo.AsSpan().IndexOf("PeriodoEmissao"u8) >= 0 && req.Corpo.AsSpan().IndexOf("Tomador"u8) >= 0
            ? e030
            : Exemplos.Bytes(RespostaDaConsulta))(ctx, req);
        _novo.Responder = Responder(e030);
        await using var fachada = Fachada(Flags(0));

        // Sucesso (chave frequente) com amostragem 0: não vai. E030 (rara): vai sempre.
        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta))).Dispose();
        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes("04e-consultar-nfse-por-tomador.xml"))).Dispose();

        await Eventualmente(() => Sombra(fachada).Any(m => m.Contains("igual", StringComparison.Ordinal)));
        Assert.Equal(Exemplos.Bytes("04e-consultar-nfse-por-tomador.xml"), Assert.Single(_novo.Recebidas).Corpo);
    }

    [Fact]
    public async Task Operacao_fora_de_sombra_nao_e_copiada()
    {
        FlagsDeMigracao flags = Flags(100);
        flags.Operacoes[OperacoesSoap.ConsultarNfse].Modo = ModoDeMigracao.Legado;
        await using var fachada = Fachada(flags);

        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta))).Dispose();
        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfseServicoTomado, Exemplos.Bytes(ServicoTomado))).Dispose();

        await Eventualmente(() => Sombra(fachada).Any(m => m.Contains("Sombra ConsultarNfseServicoTomado", StringComparison.Ordinal)));
        Assert.Equal(Exemplos.SoapAction(OperacoesSoap.ConsultarNfseServicoTomado), Assert.Single(_novo.Recebidas).SoapAction);
    }

    [Fact]
    public async Task C20_FaultServer_com_texto_diferente_e_decisao_consciente()
    {
        _legado.Responder = Fault("ORA-12899: valor muito grande para a coluna");
        _novo.Responder = Fault("Erro interno: falha ao processar a requisição");
        await using var fachada = Fachada(Flags(100));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        string registro = await Divergencia(fachada);
        Assert.Contains("divergência decisao_consciente (regra C20)", registro, StringComparison.Ordinal);
        Assert.Contains("status 500/500", registro, StringComparison.Ordinal);
        Assert.Contains("/soap:Envelope/soap:Body[1]/soap:Fault[1]/faultstring[1]/text()", registro, StringComparison.Ordinal);
        Assert.Single(_legado.Recebidas);
    }

    [Fact]
    public async Task C28_pedido_com_DTD_e_decisao_consciente()
    {
        byte[] comDtd = Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a \"10000001\">]><Envelope><inputXML>&a;</inputXML></Envelope>");
        _legado.Responder = Responder(Exemplos.Bytes(RespostaDaConsulta));
        _novo.Responder = Responder(Encoding.UTF8.GetBytes(RespostaComCodigo("ConsultarNfse", "E001")));
        await using var fachada = Fachada(Flags(100));

        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, comDtd)).Dispose();

        string registro = await Divergencia(fachada);
        Assert.Contains("divergência decisao_consciente (regra C28)", registro, StringComparison.Ordinal);
        Assert.Contains("requisição omitida", registro, StringComparison.Ordinal);
        Assert.Single(_legado.Recebidas);
        AssertSemDadosPessoais(fachada);
    }

    [Theory]
    [InlineData(ChaveHmac)]
    [InlineData("")]
    public async Task Nenhum_dado_pessoal_nos_logs(string chave)
    {
        _novo.Responder = Responder(Encoding.UTF8.GetBytes(RespostaComCodigo("ConsultarNfse", "E040")));
        await using var fachada = Fachada(Flags(100), ("Sombra:ChaveHmac", chave));

        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta))).Dispose();
        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfseServicoTomado, Exemplos.Bytes(ServicoTomado))).Dispose();

        await Eventualmente(() => Sombra(fachada).Count(m => m.Contains("divergência", StringComparison.Ordinal)) == 2);
        AssertSemDadosPessoais(fachada);
        if (chave.Length == 0)
        {
            // Sem chave, só os hashes: nenhuma requisição, nem pseudonimizada.
            Assert.All(Sombra(fachada).Where(m => m.Contains("divergência", StringComparison.Ordinal)),
                m => Assert.Contains("requisição omitida", m, StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(Sombra(fachada), m => m.Contains("<Consulente><CpfCnpj><Cnpj>hmac:", StringComparison.Ordinal));
        }
    }

    internal static FlagsDeMigracao Flags(int amostragem)
    {
        FlagsDeMigracao flags = PassThroughTests.Flags("sombra");
        foreach (FlagsDaOperacao op in flags.Operacoes.Values)
        {
            op.Sombra.Amostragem = amostragem;
        }

        return flags;
    }

    internal static Func<HttpContext, RequisicaoRecebida, Task> Responder(byte[] corpo, int status = StatusCodes.Status200OK) =>
        (ctx, _) =>
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/xml; charset=utf-8";
            return ctx.Response.Body.WriteAsync(corpo).AsTask();
        };

    internal static string RespostaComCodigo(string operacao, string codigo) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>" +
        $"<{operacao}Response xmlns=\"http://notacarioca.rio.gov.br/\"><outputXML>&lt;{operacao}Resposta&gt;&lt;ListaMensagemRetorno&gt;" +
        $"&lt;MensagemRetorno&gt;&lt;Codigo&gt;{codigo}&lt;/Codigo&gt;&lt;Mensagem&gt;m&lt;/Mensagem&gt;&lt;/MensagemRetorno&gt;" +
        $"&lt;/ListaMensagemRetorno&gt;&lt;/{operacao}Resposta&gt;</outputXML></{operacao}Response></soap:Body></soap:Envelope>";

    private static Func<HttpContext, RequisicaoRecebida, Task> Fault(string texto) => Responder(
        Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><soap:Fault>" +
            $"<faultcode>soap:Server</faultcode><faultstring>{texto}</faultstring></soap:Fault></soap:Body></soap:Envelope>"),
        StatusCodes.Status500InternalServerError);

    private static byte[] TrocarPrimeiro(byte[] corpo, string de, string para)
    {
        string texto = Encoding.UTF8.GetString(corpo);
        int i = texto.IndexOf(de, StringComparison.Ordinal);
        Assert.True(i >= 0);
        return Encoding.UTF8.GetBytes(string.Concat(texto.AsSpan(0, i), para, texto.AsSpan(i + de.Length)));
    }

    private static string Sha256(byte[] corpo) => Convert.ToHexStringLower(SHA256.HashData(corpo));

    private static IEnumerable<string> Sombra(FabricaDaFachada fachada) =>
        fachada.Logs.Mensagens.Where(m => m.Contains("|Sombra ", StringComparison.Ordinal));

    private static async Task<string> Divergencia(FabricaDaFachada fachada)
    {
        await Eventualmente(() => Sombra(fachada).Any(m => m.Contains("divergência", StringComparison.Ordinal)));
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

    internal static async Task Eventualmente(Func<bool> condicao)
    {
        var limite = Stopwatch.StartNew();
        while (!condicao())
        {
            Assert.True(limite.Elapsed < TimeSpan.FromSeconds(10), "condição não atingida em 10 s");
            await Task.Delay(20);
        }
    }

    private FabricaDaFachada Fachada(FlagsDeMigracao flags, params (string Chave, string Valor)[] configuracao) =>
        Fachada(flags, _novo.CriarHandler(), configuracao);

    private FabricaDaFachada Fachada(FlagsDeMigracao flags, HttpMessageHandler novo, params (string Chave, string Valor)[] configuracao)
    {
        // "igual" e os descartes saem em Debug, para não encher o log em produção.
        var valores = new Dictionary<string, string?> { ["Sombra:ChaveHmac"] = ChaveHmac, ["Logging:LogLevel:Nfse.Fachada"] = "Debug" };
        foreach ((string chave, string valor) in configuracao)
        {
            valores[chave] = valor;
        }

        return new FabricaDaFachada(_legado.CriarHandler(), novo, new FlagsFixas(flags), valores);
    }
}
