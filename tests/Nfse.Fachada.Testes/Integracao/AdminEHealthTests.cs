using System.Net;
using Microsoft.AspNetCore.Http;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Testes.Integracao;

/// <summary>Kill switch nível 2 (plano, seção 8) e health da própria fachada.</summary>
public sealed class AdminEHealthTests : IAsyncLifetime
{
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("token-errado")]
    [InlineData("TOKEN-DE-TESTE")]
    public async Task Sem_token_valido_responde_401_e_nao_altera(string? token)
    {
        await using var fachada = Fachada();

        HttpContext ctx = await fachada.EnviarNaPortaAsync(FabricaDaFachada.PortaAdmin, "POST", "/admin/kill-switch", token);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.Equal("do-novo", await ConsultarAsync(fachada));
    }

    [Theory]
    [InlineData(FabricaDaFachada.PortaPublica)]
    [InlineData(7000)]
    public async Task Admin_fora_da_porta_interna_nao_existe_nem_vai_ao_legado(int porta)
    {
        await using var fachada = Fachada();

        foreach (string metodo in new[] { "GET", "POST", "DELETE" })
        {
            HttpContext ctx = await fachada.EnviarNaPortaAsync(porta, metodo, "/admin/kill-switch", FabricaDaFachada.Token);
            Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        }

        Assert.Empty(_legado.Recebidas);
        Assert.Equal("do-novo", await ConsultarAsync(fachada));
    }

    [Theory]
    [InlineData("POST", "/NfseWS.asmx")]
    [InlineData("GET", "/NfseWS.asmx")]
    [InlineData("GET", "/qualquer-coisa")]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/health/live")]
    [InlineData("GET", "/health/ready")]
    public async Task Porta_interna_nao_e_plano_de_dados(string metodo, string caminho)
    {
        await using var fachada = Fachada();

        HttpContext ctx = await fachada.EnviarNaPortaAsync(FabricaDaFachada.PortaAdmin, metodo, caminho, FabricaDaFachada.Token);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.Empty(_legado.Recebidas);
        Assert.Empty(_novo.Recebidas);
    }

    [Fact]
    public async Task Sem_token_configurado_os_endpoints_ficam_fechados()
    {
        await using var fachada = Fachada(new Dictionary<string, string?> { ["Admin:Token"] = "" });

        HttpContext ctx = await fachada.EnviarNaPortaAsync(FabricaDaFachada.PortaAdmin, "POST", "/admin/kill-switch", "");

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Global_vale_na_proxima_requisicao_e_desliga_com_delete()
    {
        await using var fachada = Fachada();
        Assert.Equal("do-novo", await ConsultarAsync(fachada));

        HttpContext liga = await fachada.EnviarNaPortaAsync(FabricaDaFachada.PortaAdmin, "POST", "/admin/kill-switch", FabricaDaFachada.Token);
        Assert.Equal(StatusCodes.Status200OK, liga.Response.StatusCode);
        Assert.Equal("do-legado", await ConsultarAsync(fachada));
        Assert.Equal("do-legado", await CancelarAsync(fachada));

        HttpContext desliga = await fachada.EnviarNaPortaAsync(FabricaDaFachada.PortaAdmin, "DELETE", "/admin/kill-switch", FabricaDaFachada.Token);
        Assert.Equal(StatusCodes.Status200OK, desliga.Response.StatusCode);
        Assert.Equal("do-novo", await ConsultarAsync(fachada));
    }

    [Fact]
    public async Task Por_operacao_afeta_so_a_operacao()
    {
        await using var fachada = Fachada();

        HttpContext ctx = await fachada.EnviarNaPortaAsync(
            FabricaDaFachada.PortaAdmin, "POST", "/admin/kill-switch", FabricaDaFachada.Token, "?operacao=CancelarNfse");

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal("do-legado", await CancelarAsync(fachada));
        Assert.Equal("do-novo", await ConsultarAsync(fachada));

        await fachada.EnviarNaPortaAsync(FabricaDaFachada.PortaAdmin, "DELETE", "/admin/kill-switch", FabricaDaFachada.Token, "?operacao=CancelarNfse");
        Assert.Equal("do-novo", await CancelarAsync(fachada));
    }

    [Fact]
    public async Task Operacao_fora_do_escopo_e_rejeitada()
    {
        await using var fachada = Fachada();

        HttpContext ctx = await fachada.EnviarNaPortaAsync(
            FabricaDaFachada.PortaAdmin, "POST", "/admin/kill-switch", FabricaDaFachada.Token, "?operacao=RecepcionarLoteRps");

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Kill_switch_administrativo_funciona_com_flags_indisponiveis()
    {
        var flags = new FlagsFixas(null);
        await using var fachada = new FabricaDaFachada(_legado.CriarHandler(), _novo.CriarHandler(), flags);

        HttpContext ctx = await fachada.EnviarNaPortaAsync(FabricaDaFachada.PortaAdmin, "POST", "/admin/kill-switch", FabricaDaFachada.Token);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal("do-legado", await ConsultarAsync(fachada));
    }

    [Fact]
    public async Task Live_responde_sempre_e_ready_depende_das_flags()
    {
        var flags = new FlagsFixas(PassThroughTests.Flags("legado"));
        await using var fachada = new FabricaDaFachada(_legado.CriarHandler(), null, flags);
        HttpClient cliente = fachada.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(new Uri("/health/ready", UriKind.Relative))).StatusCode);

        flags.Valor = null;

        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await cliente.GetAsync(new Uri("/health/ready", UriKind.Relative))).StatusCode);
        Assert.Empty(_legado.Recebidas);
    }

    private FabricaDaFachada Fachada(IReadOnlyDictionary<string, string?>? configuracao = null) =>
        new(_legado.CriarHandler(), _novo.CriarHandler(), new FlagsFixas(PassThroughTests.Flags("novo", 100)), configuracao);

    private static async Task<string> ConsultarAsync(FabricaDaFachada fachada)
    {
        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes("04-consultar-nfse.xml"));
        return await resposta.Content.ReadAsStringAsync();
    }

    private static async Task<string> CancelarAsync(FabricaDaFachada fachada)
    {
        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes("06-cancelar-nfse.xml"));
        return await resposta.Content.ReadAsStringAsync();
    }
}
