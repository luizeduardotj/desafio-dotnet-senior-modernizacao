using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Nfse.Fachada.Observabilidade;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Testes.Integracao;

/// <summary>Métricas da ADR-0012. O Meter é estático e outros testes rodam em paralelo: as asserções são de presença.</summary>
public sealed class TelemetriaTests : IAsyncLifetime, IDisposable
{
    private readonly ConcurrentQueue<Medicao> _medicoes = new();
    private readonly MeterListener _ouvinte = new();
    private ServidorFalso _legado = null!;

    public TelemetriaTests()
    {
        _ouvinte.InstrumentPublished = (instrumento, ouvinte) =>
        {
            if (instrumento.Meter.Name == Telemetria.Nome)
            {
                ouvinte.EnableMeasurementEvents(instrumento);
            }
        };
        _ouvinte.SetMeasurementEventCallback<long>((i, valor, tags, _) => Gravar(i, valor, tags));
        _ouvinte.SetMeasurementEventCallback<double>((i, valor, tags, _) => Gravar(i, valor, tags));
        _ouvinte.Start();
    }

    public async Task InitializeAsync()
    {
        _legado = await ServidorFalso.IniciarAsync();
        _legado.Responder = (ctx, _) => ctx.Response.WriteAsync("do-legado");
    }

    public async Task DisposeAsync() => await _legado.DisposeAsync();

    public void Dispose() => _ouvinte.Dispose();

    [Fact]
    public async Task Requisicoes_duracao_e_fallback_com_labels_da_ADR_0012()
    {
        var novo = new NovoComFalha(async (req, ct) =>
        {
            string corpo = await req.Content!.ReadAsStringAsync(ct);
            return corpo.Contains("CancelarNfseEnvio", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("<soap:Fault/>") }
                : corpo.Contains("ConsultarNfseServicoTomadoEnvio", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("indisponivel") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("do-novo") };
        });
        await using var fachada = new FabricaDaFachada(_legado.CriarHandler(), novo, new FlagsFixas(PassThroughTests.Flags("novo", 100)));

        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes("04-consultar-nfse.xml"))).Dispose();
        (await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes("06-cancelar-nfse.xml"))).Dispose();
        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfseServicoTomado, Exemplos.Bytes("05-consultar-nfse-servico-tomado.xml"))).Dispose();
        (await fachada.EnviarSoapAsync("RecepcionarLoteRps", Exemplos.Bytes("01-recepcionar-lote-rps.xml"))).Dispose();

        AssertContem("nfse_requisicoes_total", ("operacao", "ConsultarNfse"), ("modo", "novo"), ("destino", "novo"), ("resultado", "ok"));
        AssertContem("nfse_requisicoes_total", ("operacao", "CancelarNfse"), ("modo", "novo"), ("destino", "novo"), ("resultado", "fault"));
        AssertContem("nfse_requisicoes_total", ("operacao", "ConsultarNfseServicoTomado"), ("modo", "novo"), ("destino", "legado"), ("resultado", "ok"));
        AssertContem("nfse_requisicoes_total", ("operacao", "RecepcionarLoteRps"), ("modo", "legado"), ("destino", "legado"), ("resultado", "ok"));
        AssertContem("nfse_fallback_total", ("operacao", "ConsultarNfseServicoTomado"), ("causa", "http_503"));
        AssertContem("nfse_duracao_segundos", ("operacao", "ConsultarNfseServicoTomado"), ("destino", "novo"));
        AssertContem("nfse_duracao_segundos", ("operacao", "ConsultarNfseServicoTomado"), ("destino", "legado"));
        AssertContem("nfse_duracao_segundos", ("operacao", "RecepcionarLoteRps"), ("destino", "legado"));
        Assert.DoesNotContain(_medicoes, m => m.Instrumento == "nfse_fallback_total" && m.Tags["operacao"] == "CancelarNfse");
    }

    [Fact]
    public async Task Flags_indisponiveis_aparecem_no_modo()
    {
        await using var fachada = new FabricaDaFachada(_legado.CriarHandler(), null, new FlagsFixas(null));

        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes("04-consultar-nfse.xml"))).Dispose();

        AssertContem("nfse_requisicoes_total", ("operacao", "ConsultarNfse"), ("modo", "indisponivel"), ("destino", "legado"), ("resultado", "ok"));
    }

    [Fact]
    public async Task Labels_so_tem_dimensoes_de_baixa_cardinalidade()
    {
        await using var fachada = new FabricaDaFachada(_legado.CriarHandler(), null, new FlagsFixas(PassThroughTests.Flags("sombra")));
        foreach (string pedido in Exemplos.Pedidos())
        {
            (await fachada.EnviarSoapAsync(Exemplos.Operacao(pedido), Exemplos.Bytes(pedido))).Dispose();
        }

        (await fachada.EnviarSoapAsync("x", [], "\"http://notacarioca.rio.gov.br/10000001\"")).Dispose();

        string[] permitidas = ["operacao", "modo", "destino", "resultado", "causa"];
        Assert.NotEmpty(_medicoes);
        Assert.All(_medicoes, m => Assert.All(m.Tags.Keys, chave => Assert.Contains(chave, permitidas)));
        Assert.DoesNotContain(_medicoes, m => m.Instrumento == "fachada_roteamento_total");
        foreach (string dado in new[] { "10000001", "10000025", "11100003000113", "01234567000195" })
        {
            Assert.DoesNotContain(_medicoes, m => m.Tags.Values.Any(v => v?.Contains(dado, StringComparison.Ordinal) == true));
        }

        AssertContem("nfse_requisicoes_total", ("operacao", "desconhecida"));
    }

    private void AssertContem(string instrumento, params (string Chave, string Valor)[] tags) =>
        Assert.True(
            _medicoes.Any(m => m.Instrumento == instrumento && tags.All(t => m.Tags.TryGetValue(t.Chave, out string? v) && v == t.Valor)),
            $"{instrumento} sem {string.Join(", ", tags)}; medições: {string.Join(" | ", _medicoes.Where(m => m.Instrumento == instrumento))}");

    private void Gravar<T>(Instrument instrumento, T valor, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dicionario = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            dicionario[tag.Key] = tag.Value?.ToString();
        }

        _medicoes.Enqueue(new Medicao(instrumento.Name, Convert.ToDouble(valor, System.Globalization.CultureInfo.InvariantCulture), dicionario));
    }

    private sealed record Medicao(string Instrumento, double Valor, Dictionary<string, string?> Tags)
    {
        public override string ToString() => $"{Instrumento}{{{string.Join(",", Tags.Select(t => $"{t.Key}={t.Value}"))}}}";
    }
}
