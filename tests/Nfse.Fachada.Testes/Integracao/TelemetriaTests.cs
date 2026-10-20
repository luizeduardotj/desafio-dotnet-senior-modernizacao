using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nfse.Fachada.Admin;
using Nfse.Fachada.Flags;
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
        AssertContem("nfse_duracao", ("operacao", "ConsultarNfseServicoTomado"), ("destino", "novo"));
        AssertContem("nfse_duracao", ("operacao", "ConsultarNfseServicoTomado"), ("destino", "legado"));
        AssertContem("nfse_duracao", ("operacao", "RecepcionarLoteRps"), ("destino", "legado"));
        Assert.DoesNotContain(_medicoes, m => m.Instrumento == "nfse_fallback_total" && m.Tags["operacao"] == "CancelarNfse");
    }

    [Fact]
    public async Task Overhead_da_fachada_por_operacao()
    {
        await using var fachada = new FabricaDaFachada(_legado.CriarHandler(), null, new FlagsFixas(PassThroughTests.Flags("sombra")));

        (await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes("06-cancelar-nfse.xml"))).Dispose();
        (await fachada.EnviarSoapAsync("RecepcionarLoteRps", Exemplos.Bytes("01-recepcionar-lote-rps.xml"))).Dispose();

        AssertContem("fachada_overhead", ("operacao", "CancelarNfse"));
        AssertContem("fachada_overhead", ("operacao", "RecepcionarLoteRps"));
        Assert.All(_medicoes.Where(m => m.Instrumento == "fachada_overhead"), m =>
        {
            Assert.InRange(m.Valor, 0, 5);
            Assert.Equal(["operacao"], m.Tags.Keys);
        });
    }

    [Fact]
    public async Task Gauges_de_modo_e_kill_switch()
    {
        FlagsDeMigracao flags = PassThroughTests.Flags("novo", 5);
        flags.Operacoes[OperacoesSoap.CancelarNfse].Modo = ModoDeMigracao.Legado;
        await using var fachada = new FabricaDaFachada(_legado.CriarHandler(), null, new FlagsFixas(flags));
        _ = fachada.Server;

        _ouvinte.RecordObservableInstruments();
        AssertContem("migracao_modo_info", ("operacao", "ConsultarNfse"), ("modo", "novo"), ("percentual", "5"));
        AssertContem("migracao_modo_info", ("operacao", "CancelarNfse"), ("modo", "legado"), ("percentual", "5"));
        Assert.Contains(_medicoes, m => m.Instrumento == "kill_switch_ativo" && m.Valor == 0 && m.Tags.Count == 0);

        _medicoes.Clear();
        fachada.Services.GetRequiredService<KillSwitchAdministrativo>().Ativar(OperacoesSoap.CancelarNfse);
        _ouvinte.RecordObservableInstruments();
        Assert.Contains(_medicoes, m => m.Instrumento == "kill_switch_ativo" && m.Valor == 1);
    }

    [Fact]
    public async Task Gauge_de_modo_com_flags_indisponiveis()
    {
        await using var fachada = new FabricaDaFachada(_legado.CriarHandler(), null, new FlagsFixas(null));
        _ = fachada.Server;

        _ouvinte.RecordObservableInstruments();

        AssertContem("migracao_modo_info", ("operacao", "ConsultarNfseServicoTomado"), ("modo", "indisponivel"), ("percentual", "0"));
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

        string[] permitidas = ["operacao", "modo", "destino", "resultado", "causa", "classe", "motivo", "gatilho"];
        Assert.NotEmpty(_medicoes);
        Assert.All(_medicoes, m => Assert.All(m.Tags.Keys, chave => Assert.Contains(chave, permitidas)));
        Assert.DoesNotContain(_medicoes, m => m.Instrumento == "fachada_roteamento_total");
        foreach (string dado in new[] { "10000001", "10000025", "11100003000113", "01234567000195" })
        {
            Assert.DoesNotContain(_medicoes, m => m.Tags.Values.Any(v => v?.Contains(dado, StringComparison.Ordinal) == true));
        }

        AssertContem("nfse_requisicoes_total", ("operacao", "desconhecida"));
    }

    [Fact]
    public async Task Sombra_com_os_nomes_e_labels_da_ADR_0012()
    {
        await using ServidorFalso novo = await ServidorFalso.IniciarAsync();
        byte[] grande = new byte[(1024 * 1024) + 1];
        _legado.Responder = (ctx, req) => SombraTests.Responder(req.Corpo.AsSpan().IndexOf("Tomador"u8) >= 0
            ? grande
            : Exemplos.Bytes("04-consultar-nfse.resposta.xml"))(ctx, req);
        novo.Responder = (ctx, req) => SombraTests.Responder(req.SoapAction.Contains("Tomado", StringComparison.Ordinal)
            ? Exemplos.Bytes("05-consultar-nfse-servico-tomado.resposta.xml")
            : Exemplos.Bytes("04-consultar-nfse.resposta.xml"))(ctx, req);
        await using var fachada = new FabricaDaFachada(
            _legado.CriarHandler(), novo.CriarHandler(), new FlagsFixas(SombraTests.Flags(100)),
            new Dictionary<string, string?> { ["Sombra:ReexecucoesPorSegundo"] = "1000" });

        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes("04-consultar-nfse.xml"))).Dispose();
        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfseServicoTomado, Exemplos.Bytes("05-consultar-nfse-servico-tomado.xml"))).Dispose();

        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes("04e-consultar-nfse-por-tomador.xml"))).Dispose();

        // Espera todas as medições verificadas: reexecução e descarte podem chegar depois do atraso das outras.
        await SombraTests.Eventualmente(() =>
            _medicoes.Count(m => m.Instrumento == "sombra_atraso") >= 2
            && Tem("sombra_reexecucoes_total", ("operacao", "ConsultarNfseServicoTomado"), ("resultado", "divergiu"))
            && Tem("sombra_divergencias_total", ("operacao", "ConsultarNfseServicoTomado"), ("classe", "nao_classificada"))
            && Tem("sombra_descartadas_total", ("operacao", "ConsultarNfse"), ("motivo", "resposta_grande")));
        AssertContem("sombra_comparacoes_total", ("operacao", "ConsultarNfse"), ("resultado", "igual"));
        AssertContem("sombra_comparacoes_total", ("operacao", "ConsultarNfseServicoTomado"), ("resultado", "divergente"));
        AssertContem("sombra_divergencias_total", ("operacao", "ConsultarNfseServicoTomado"), ("classe", "nao_classificada"));
        AssertContem("sombra_reexecucoes_total", ("operacao", "ConsultarNfseServicoTomado"), ("resultado", "divergiu"));
        AssertContem("sombra_atraso", ("operacao", "ConsultarNfse"));
        AssertContem("sombra_descartadas_total", ("operacao", "ConsultarNfse"), ("motivo", "resposta_grande"));
        Assert.All(_medicoes.Where(m => m.Instrumento == "sombra_atraso"), m => Assert.InRange(m.Valor, 0, 10));
    }

    private bool Tem(string instrumento, params (string Chave, string Valor)[] tags) =>
        _medicoes.ToArray().Any(m => m.Instrumento == instrumento && tags.All(t => m.Tags.TryGetValue(t.Chave, out string? v) && v == t.Valor));

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
