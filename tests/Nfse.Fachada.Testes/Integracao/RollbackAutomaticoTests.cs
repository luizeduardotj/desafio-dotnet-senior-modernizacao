using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Nfse.Fachada.Flags;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Testes.Integracao;

/// <summary>ADR-0014 na fachada real: disparo, direção única, header interno, desarme pela flag e sombra intacta.</summary>
public sealed class RollbackAutomaticoTests : IAsyncLifetime
{
    private const string Consulta = "04-consultar-nfse.xml";
    private const string Cancelamento = "06-cancelar-nfse.xml";
    private const string HeaderDoDualRun = "X-Nfse-Dual-Run";

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

    private static FlagsDeMigracao Flags(string modo = "novo", int percentual = 100, bool ligado = true)
    {
        FlagsDeMigracao flags = PassThroughTests.Flags(modo, percentual);
        foreach (FlagsDaOperacao op in flags.Operacoes.Values)
        {
            op.RollbackAutomatico = new FlagsDoRollbackAutomatico { Ligado = ligado, Rearme = "r1" };
        }

        return flags;
    }

    private FabricaDaFachada Fachada(FlagsFixas flags) =>
        new(_legado.CriarHandler(), _novo.CriarHandler(), flags, new Dictionary<string, string?>
        {
            ["RollbackAutomatico:MinimoDeAmostras"] = "5",
            ["RollbackAutomatico:Referencias:ConsultarNfse:TaxaDeFault"] = "0.001",
        });

    private void NovoComDualRunDivergente() =>
        _novo.Responder = (ctx, _) =>
        {
            ctx.Response.Headers[HeaderDoDualRun] = "divergente";
            return ctx.Response.WriteAsync("do-novo");
        };

    private static async Task<string> CorpoAsync(FabricaDaFachada fachada, string operacao, string pedido)
    {
        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(operacao, Exemplos.Bytes(pedido));
        return await resposta.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Faults_do_novo_no_minimo_de_amostras_disparam_e_a_proxima_vai_ao_legado()
    {
        _novo.Responder = (ctx, _) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return ctx.Response.WriteAsync("fault-do-novo");
        };
        await using var fachada = Fachada(new FlagsFixas(Flags()));

        for (int i = 0; i < 5; i++)
        {
            using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));
            Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        }

        Assert.Equal("do-legado", await CorpoAsync(fachada, OperacoesSoap.ConsultarNfse, Consulta));
        Assert.Equal(5, _novo.Recebidas.Count);
        Assert.Single(_legado.Recebidas);
        Assert.Contains(fachada.Logs.Mensagens, m => m.Contains("Rollback automático: ConsultarNfse volta ao legado nesta réplica (gatilho fault", StringComparison.Ordinal));
        // As outras operações continuam no novo.
        _novo.Responder = (ctx, _) => ctx.Response.WriteAsync("do-novo");
        Assert.Equal("do-novo", await CorpoAsync(fachada, OperacoesSoap.ConsultarNfseServicoTomado, "05-consultar-nfse-servico-tomado.xml"));
    }

    [Fact]
    public async Task Abaixo_do_minimo_de_amostras_continua_no_novo()
    {
        _novo.Responder = (ctx, _) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        };
        await using var fachada = Fachada(new FlagsFixas(Flags()));

        // Mínimo de 5: quatro requisições, todas com fault, não disparam.
        for (int i = 0; i < 4; i++)
        {
            using HttpResponseMessage _ = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));
        }

        Assert.Equal(4, _novo.Recebidas.Count);
        Assert.Empty(_legado.Recebidas);
        Assert.False((await EstadoAsync(fachada, OperacoesSoap.ConsultarNfse)).GetProperty("disparado").GetBoolean());
    }

    private static async Task<JsonElement> EstadoAsync(FabricaDaFachada fachada, string operacao)
    {
        HttpContext ctx = await fachada.EnviarNaPortaAsync(FabricaDaFachada.PortaAdmin, "GET", "/admin/rollback-automatico", FabricaDaFachada.Token);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        using JsonDocument estado = await JsonDocument.ParseAsync(ctx.Response.Body);
        return estado.RootElement.EnumerateArray().Single(e => e.GetProperty("operacao").GetString() == operacao).Clone();
    }

    [Fact]
    public async Task Dual_run_divergente_dispara_na_primeira_e_o_header_nao_chega_ao_ERP()
    {
        NovoComDualRunDivergente();
        await using var fachada = Fachada(new FlagsFixas(Flags()));

        using HttpResponseMessage primeira = await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes(Cancelamento));

        Assert.Equal("do-novo", await primeira.Content.ReadAsStringAsync());
        Assert.False(primeira.Headers.Contains(HeaderDoDualRun));
        Assert.Equal("do-legado", await CorpoAsync(fachada, OperacoesSoap.CancelarNfse, Cancelamento));
        Assert.Single(_novo.Recebidas);
    }

    [Fact]
    public async Task Troca_do_Rearme_nas_flags_desarma()
    {
        NovoComDualRunDivergente();
        var flags = new FlagsFixas(Flags());
        await using var fachada = Fachada(flags);
        Assert.Equal("do-novo", await CorpoAsync(fachada, OperacoesSoap.CancelarNfse, Cancelamento));
        Assert.Equal("do-legado", await CorpoAsync(fachada, OperacoesSoap.CancelarNfse, Cancelamento));

        _novo.Responder = (ctx, _) => ctx.Response.WriteAsync("do-novo");
        flags.Valor!.Operacoes[OperacoesSoap.CancelarNfse].RollbackAutomatico.Rearme = "2026-10-20-incidente-42";

        Assert.Equal("do-novo", await CorpoAsync(fachada, OperacoesSoap.CancelarNfse, Cancelamento));
        Assert.Contains(fachada.Logs.Mensagens, m => m.Contains("Rollback automático de CancelarNfse desarmado", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Desligado_nas_flags_nao_dispara()
    {
        NovoComDualRunDivergente();
        await using var fachada = Fachada(new FlagsFixas(Flags(ligado: false)));

        Assert.Equal("do-novo", await CorpoAsync(fachada, OperacoesSoap.CancelarNfse, Cancelamento));
        Assert.Equal("do-novo", await CorpoAsync(fachada, OperacoesSoap.CancelarNfse, Cancelamento));
    }

    [Fact]
    public async Task Kill_switch_ligado_e_desligado_nao_desarma_o_automatico()
    {
        NovoComDualRunDivergente();
        var flags = new FlagsFixas(Flags());
        await using var fachada = Fachada(flags);
        Assert.Equal("do-novo", await CorpoAsync(fachada, OperacoesSoap.CancelarNfse, Cancelamento));

        flags.Valor!.KillSwitch = true;
        Assert.Equal("do-legado", await CorpoAsync(fachada, OperacoesSoap.CancelarNfse, Cancelamento));
        flags.Valor.KillSwitch = false;

        Assert.Equal("do-legado", await CorpoAsync(fachada, OperacoesSoap.CancelarNfse, Cancelamento));
        Assert.Single(_novo.Recebidas);
    }

    [Fact]
    public async Task Sombra_continua_copiando_para_o_novo_com_a_operacao_disparada()
    {
        _novo.Responder = (ctx, _) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        };
        var flags = new FlagsFixas(Flags());
        await using var fachada = Fachada(flags);
        for (int i = 0; i < 5; i++)
        {
            using HttpResponseMessage _ = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));
        }

        // O plantão volta a operação para sombra sem trocar o Rearme: o cliente recebe o legado e a cópia vai ao novo.
        _novo.Responder = (ctx, _) => ctx.Response.WriteAsync("do-legado");
        FlagsDaOperacao consulta = flags.Valor!.Operacoes[OperacoesSoap.ConsultarNfse];
        consulta.Modo = ModoDeMigracao.Sombra;
        consulta.Sombra.Amostragem = 100;

        Assert.Equal("do-legado", await CorpoAsync(fachada, OperacoesSoap.ConsultarNfse, Consulta));
        await Eventualmente(() => _novo.Recebidas.Count == 6);
        RequisicaoRecebida copia = _novo.Recebidas.Last();
        Assert.Equal("/NfseWS.asmx", copia.Caminho);
        Assert.Equal(Exemplos.Bytes(Consulta), copia.Corpo);
    }

    [Fact]
    public async Task Estado_na_porta_interna_e_sem_dado_pessoal()
    {
        NovoComDualRunDivergente();
        await using var fachada = Fachada(new FlagsFixas(Flags()));
        await CorpoAsync(fachada, OperacoesSoap.CancelarNfse, Cancelamento);

        JsonElement cancelamento = await EstadoAsync(fachada, OperacoesSoap.CancelarNfse);
        Assert.True(cancelamento.GetProperty("disparado").GetBoolean());
        Assert.Equal("regra_divergente", cancelamento.GetProperty("gatilho").GetString());
        Assert.Equal("r1", cancelamento.GetProperty("rearme").GetString());

        HttpContext semToken = await fachada.EnviarNaPortaAsync(FabricaDaFachada.PortaAdmin, "GET", "/admin/rollback-automatico");
        Assert.Equal(StatusCodes.Status401Unauthorized, semToken.Response.StatusCode);

        // IM e CNPJ do exemplo 06 não aparecem em log nenhum.
        Assert.DoesNotContain(fachada.Logs.Mensagens, m => m.Contains("10000025", StringComparison.Ordinal) || m.Contains("01234567000195", StringComparison.Ordinal));
    }

    private static async Task Eventualmente(Func<bool> condicao)
    {
        for (int i = 0; i < 100 && !condicao(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condicao());
    }
}
