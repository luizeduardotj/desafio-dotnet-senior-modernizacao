using System.Net;
using Microsoft.AspNetCore.Http;
using Nfse.Fachada.Flags;

namespace Nfse.Fachada.Testes.Integracao;

/// <summary>ADR-0001/0008: tudo que vai ao legado chega lá, e volta ao cliente, byte a byte.</summary>
public sealed class PassThroughTests : IAsyncLifetime
{
    private ServidorFalso _legado = null!;

    public static TheoryData<string> Pedidos => [.. Exemplos.Pedidos()];

    public async Task InitializeAsync()
    {
        _legado = await ServidorFalso.IniciarAsync();
        _legado.Responder = async (ctx, req) =>
        {
            // Responde como o legado respondeu ao exemplo, se a resposta estiver gravada.
            string operacao = req.SoapAction.Trim('"')[(req.SoapAction.Trim('"').LastIndexOf('/') + 1)..];
            string? pedido = Exemplos.Pedidos().FirstOrDefault(p => Exemplos.Bytes(p).AsSpan().SequenceEqual(req.Corpo));
            string? resposta = pedido is null ? null : Exemplos.Resposta(pedido);
            ctx.Response.ContentType = "text/xml; charset=utf-8";
            ctx.Response.Headers["X-Legado-Operacao"] = operacao;
            await ctx.Response.Body.WriteAsync(resposta is null ? "sem-resposta"u8.ToArray() : Exemplos.Bytes(resposta));
        };
    }

    public async Task DisposeAsync() => await _legado.DisposeAsync();

    [Theory]
    [MemberData(nameof(Pedidos))]
    public async Task Exemplos_atravessam_byte_a_byte_com_flags_em_legado(string pedido) =>
        await AtravessaIntacto(pedido, Flags("legado"));

    [Theory]
    [MemberData(nameof(Pedidos))]
    public async Task Exemplos_atravessam_byte_a_byte_com_corpo_bufferizado_em_sombra(string pedido) =>
        await AtravessaIntacto(pedido, Flags("sombra"));

    [Theory]
    [MemberData(nameof(Pedidos))]
    public async Task Exemplos_atravessam_byte_a_byte_com_flags_indisponiveis(string pedido) =>
        await AtravessaIntacto(pedido, null);

    [Fact]
    public async Task Operacao_desconhecida_vai_ao_legado_sem_alteracao()
    {
        await using var fachada = new FabricaDaFachada(_legado.CriarHandler(), null, new FlagsFixas(Flags("sombra")));
        byte[] corpo = Exemplos.Bytes("04-consultar-nfse.xml");

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync("x", corpo, "\"http://notacarioca.rio.gov.br/consultarnfse\"");

        RequisicaoRecebida recebida = Assert.Single(_legado.Recebidas);
        Assert.Equal("\"http://notacarioca.rio.gov.br/consultarnfse\"", recebida.SoapAction);
        Assert.Equal(corpo, recebida.Corpo);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Theory]
    [InlineData("/NfseWS.asmx?wsdl")]
    [InlineData("/NfseWS.asmx")]
    [InlineData("/health")]
    [InlineData("/qualquer/outro?caminho=1")]
    public async Task Get_atravessa_com_caminho_e_query(string caminho)
    {
        _legado.Responder = (ctx, _) => ctx.Response.WriteAsync("<definitions/>");
        await using var fachada = new FabricaDaFachada(_legado.CriarHandler(), null, new FlagsFixas(Flags("legado")));

        using HttpResponseMessage resposta = await fachada.CreateClient().GetAsync(new Uri(caminho, UriKind.Relative));

        RequisicaoRecebida recebida = Assert.Single(_legado.Recebidas);
        Assert.Equal("GET", recebida.Metodo);
        Assert.Equal(caminho, recebida.Caminho);
        Assert.Equal("<definitions/>", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Status_de_erro_do_legado_volta_como_veio()
    {
        _legado.Responder = (ctx, _) =>
        {
            ctx.Response.StatusCode = 500;
            return ctx.Response.WriteAsync("<soap:Fault/>");
        };
        await using var fachada = new FabricaDaFachada(_legado.CriarHandler(), null, new FlagsFixas(Flags("legado")));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync("RecepcionarLoteRps", Exemplos.Bytes("01-recepcionar-lote-rps.xml"));

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        Assert.Equal("<soap:Fault/>", await resposta.Content.ReadAsStringAsync());
        Assert.Single(_legado.Recebidas);
    }

    [Fact]
    public async Task Corpo_acima_de_1_MiB_vai_ao_legado_inteiro_sem_rejeicao()
    {
        // Em canário a chave não é extraída de um corpo grande; o legado recebe os mesmos bytes.
        var novo = new NovoComFalha((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        await using var fachada = new FabricaDaFachada(_legado.CriarHandler(), novo, new FlagsFixas(Flags("novo", percentual: 99)));
        byte[] original = Exemplos.Bytes("04-consultar-nfse.xml");
        byte[] corpo = [.. original, .. Enumerable.Repeat((byte)' ', (2 * 1024 * 1024) + 17)];

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync("ConsultarNfse", corpo);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(corpo, Assert.Single(_legado.Recebidas).Corpo);
        Assert.Equal(0, novo.Chamadas);
    }

    private async Task AtravessaIntacto(string pedido, FlagsDeMigracao? flags)
    {
        await using var fachada = new FabricaDaFachada(_legado.CriarHandler(), null, new FlagsFixas(flags));
        string operacao = Exemplos.Operacao(pedido);
        byte[] corpo = Exemplos.Bytes(pedido);

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(operacao, corpo);

        RequisicaoRecebida recebida = Assert.Single(_legado.Recebidas);
        Assert.Equal("POST", recebida.Metodo);
        Assert.Equal("/NfseWS.asmx", recebida.Caminho);
        Assert.Equal(Exemplos.SoapAction(operacao), recebida.SoapAction);
        Assert.Equal(corpo, recebida.Corpo);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(operacao, Assert.Single(resposta.Headers.GetValues("X-Legado-Operacao")));
        string? arquivoResposta = Exemplos.Resposta(pedido);
        if (arquivoResposta is not null)
        {
            Assert.Equal(Exemplos.Bytes(arquivoResposta), await resposta.Content.ReadAsByteArrayAsync());
        }
    }

    internal static FlagsDeMigracao Flags(string modo, int percentual = 0)
    {
        var flags = new FlagsDeMigracao();
        foreach (string operacao in Roteamento.OperacoesSoap.EmEscopo)
        {
            flags.Operacoes[operacao] = new FlagsDaOperacao { Modo = Enum.Parse<ModoDeMigracao>(modo, ignoreCase: true), Percentual = percentual };
        }

        return flags;
    }
}
