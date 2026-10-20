using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Nfse.Fachada.Observabilidade;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Testes.Integracao;

/// <summary>
/// <c>soap_cnpj_inconsistente_total{operacao}</c> (ADR-0004, C18/K04; ADR-0012): medida na sombra, fora do caminho do
/// cliente, sem consulta extra e sem o documento em label ou log.
/// </summary>
public sealed class CnpjInconsistenteTests : IAsyncLifetime, IDisposable
{
    private const string CnpjDoCadastro = "11100003000113";
    private const string CnpjDeOutro = "99999999000191";

    private readonly ConcurrentQueue<(string Instrumento, Dictionary<string, string?> Tags)> _medicoes = new();
    private readonly MeterListener _ouvinte = new();
    private ServidorFalso _legado = null!;
    private ServidorFalso _novo = null!;

    public CnpjInconsistenteTests()
    {
        _ouvinte.InstrumentPublished = (instrumento, ouvinte) =>
        {
            if (instrumento.Meter.Name == Telemetria.Nome && instrumento.Name.StartsWith("soap_cnpj_", StringComparison.Ordinal))
            {
                ouvinte.EnableMeasurementEvents(instrumento);
            }
        };
        _ouvinte.SetMeasurementEventCallback<long>((i, _, tags, _) =>
        {
            var dicionario = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                dicionario[tag.Key] = tag.Value?.ToString();
            }

            _medicoes.Enqueue((i.Name, dicionario));
        });
        _ouvinte.Start();
    }

    public async Task InitializeAsync()
    {
        _legado = await ServidorFalso.IniciarAsync();
        _novo = await ServidorFalso.IniciarAsync();
    }

    public async Task DisposeAsync()
    {
        await _legado.DisposeAsync();
        await _novo.DisposeAsync();
    }

    public void Dispose() => _ouvinte.Dispose();

    [Fact]
    public async Task C18_ConsultarNfse_com_cnpj_de_outro_conta_inconsistente_sem_o_documento_no_log()
    {
        _legado.Responder = SombraTests.Responder(Exemplos.Bytes("04-consultar-nfse.resposta.xml"));
        _novo.Responder = SombraTests.Responder(Exemplos.Bytes("04-consultar-nfse.resposta.xml"));
        await using FabricaDaFachada fachada = Fachada();
        byte[] pedido = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Exemplos.Bytes("04-consultar-nfse.xml"))
            .Replace(CnpjDoCadastro, CnpjDeOutro, StringComparison.Ordinal));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, pedido);

        Assert.Equal(Exemplos.Bytes("04-consultar-nfse.resposta.xml"), await resposta.Content.ReadAsByteArrayAsync());
        await SombraTests.Eventualmente(() => Contou("soap_cnpj_inconsistente_total", OperacoesSoap.ConsultarNfse));
        Assert.True(Contou("soap_cnpj_conferido_total", OperacoesSoap.ConsultarNfse));
        Assert.All(_medicoes, m => Assert.Equal(["operacao"], m.Tags.Keys));
        Assert.DoesNotContain(fachada.Logs.Mensagens, m => m.Contains(CnpjDeOutro, StringComparison.Ordinal) || m.Contains(CnpjDoCadastro, StringComparison.Ordinal));
    }

    [Fact]
    public async Task K04_CancelarNfse_conta_o_resultado_que_o_novo_conferiu_no_cadastro()
    {
        _legado.Responder = SombraTests.Responder(Exemplos.Bytes("06-cancelar-nfse.resposta.xml"));
        _novo.Responder = (ctx, _) =>
        {
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                resultado = "igual",
                classe = (string?)null,
                decisaoLegado = "sucesso",
                decisaoNovo = "sucesso",
                cnpjConfere = false,
            }));
        };
        await using FabricaDaFachada fachada = Fachada();

        (await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes("06-cancelar-nfse.xml"))).Dispose();

        await SombraTests.Eventualmente(() => Contou("soap_cnpj_inconsistente_total", OperacoesSoap.CancelarNfse));
        Assert.True(Contou("soap_cnpj_conferido_total", OperacoesSoap.CancelarNfse));
        Assert.Single(_legado.Recebidas);
    }

    [Fact]
    public async Task K04_sem_cnpjConfere_na_resposta_do_novo_nada_e_contado()
    {
        _legado.Responder = SombraTests.Responder(Exemplos.Bytes("06-cancelar-nfse.resposta.xml"));
        _novo.Responder = (ctx, _) =>
        {
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync("{\"resultado\":\"igual\",\"classe\":null,\"decisaoLegado\":\"sucesso\",\"decisaoNovo\":\"sucesso\"}");
        };
        await using FabricaDaFachada fachada = Fachada();

        (await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes("06-cancelar-nfse.xml"))).Dispose();

        await SombraTests.Eventualmente(() => fachada.Logs.Mensagens.Any(m => m.Contains("Sombra CancelarNfse: igual", StringComparison.Ordinal)));
        Assert.False(Contou("soap_cnpj_conferido_total", OperacoesSoap.CancelarNfse));
    }

    private bool Contou(string instrumento, string operacao) =>
        _medicoes.Any(m => m.Instrumento == instrumento && m.Tags.TryGetValue("operacao", out string? op) && op == operacao);

    private FabricaDaFachada Fachada() =>
        new(_legado.CriarHandler(), _novo.CriarHandler(), new FlagsFixas(SombraTests.Flags(100)), new Dictionary<string, string?>
        {
            ["Novo:UrlInterna"] = "http://novo.test:8081",
            ["Sombra:ChaveHmac"] = "chave-de-teste-da-sombra",
            ["Logging:LogLevel:Nfse.Fachada"] = "Debug",
        });
}
