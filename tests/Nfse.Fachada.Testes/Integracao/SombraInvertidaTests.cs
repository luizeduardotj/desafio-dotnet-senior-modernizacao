using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Nfse.Fachada.Flags;
using Nfse.Fachada.Roteamento;
using Nfse.Fachada.Sombra;

namespace Nfse.Fachada.Testes.Integracao;

/// <summary>
/// Sombra invertida (ADR-0009, fase 2): em <c>novo</c>, o novo responde ao cliente e a leitura é repetida no legado para
/// comparação. O cancelamento nunca é repetido: a flag é recusada na validação, e um retrato que chegue sem validação
/// também não gera cópia (escrita única, seção 5).
/// </summary>
public sealed class SombraInvertidaTests : IAsyncLifetime
{
    private const string Consulta = "04-consultar-nfse.xml";
    private const string RespostaDaConsulta = "04-consultar-nfse.resposta.xml";

    private ServidorFalso _legado = null!;
    private ServidorFalso _novo = null!;

    public async Task InitializeAsync()
    {
        _legado = await ServidorFalso.IniciarAsync();
        _legado.Responder = SombraTests.Responder(Exemplos.Bytes(RespostaDaConsulta));
        _novo = await ServidorFalso.IniciarAsync();
        _novo.Responder = SombraTests.Responder(Exemplos.Bytes(RespostaDaConsulta));
    }

    public async Task DisposeAsync()
    {
        await _legado.DisposeAsync();
        await _novo.DisposeAsync();
    }

    [Fact]
    public async Task Novo_responde_ao_cliente_e_o_legado_recebe_a_copia()
    {
        await using var fachada = Fachada(Flags(100));
        byte[] pedido = Exemplos.Bytes(Consulta);

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, pedido);

        Assert.Equal(Exemplos.Bytes(RespostaDaConsulta), await resposta.Content.ReadAsByteArrayAsync());
        await SombraTests.Eventualmente(() => LogsDaSombra(fachada).Any(m => m.Contains("Sombra ConsultarNfse: igual", StringComparison.Ordinal)));
        Assert.Equal(pedido, Assert.Single(_novo.Recebidas).Corpo);
        RequisicaoRecebida noLegado = Assert.Single(_legado.Recebidas);
        Assert.Equal(pedido, noLegado.Corpo);
        Assert.Equal("/NfseWS.asmx", noLegado.Caminho);
        Assert.Equal(Exemplos.SoapAction(OperacoesSoap.ConsultarNfse), noLegado.SoapAction);
    }

    [Fact]
    public async Task Divergencia_compara_legado_com_novo_e_registra_o_modo()
    {
        byte[] doLegado = Exemplos.Bytes(RespostaDaConsulta);
        byte[] doNovo = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(doLegado).Replace("&lt;Numero&gt;23&lt;/Numero&gt;", "&lt;Numero&gt;99&lt;/Numero&gt;", StringComparison.Ordinal));
        _novo.Responder = SombraTests.Responder(doNovo);
        await using var fachada = Fachada(Flags(100));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));

        Assert.Equal(doNovo, await resposta.Content.ReadAsByteArrayAsync());
        await SombraTests.Eventualmente(() => LogsDaSombra(fachada).Any(m => m.Contains("divergência", StringComparison.Ordinal)));
        string registro = LogsDaSombra(fachada).Single(m => m.Contains("divergência", StringComparison.Ordinal));
        Assert.Contains("divergência nao_classificada (regra -)", registro, StringComparison.Ordinal);
        Assert.Contains("modo sombra_invertida", registro, StringComparison.Ordinal);
        Assert.Contains("ListaNfse[1]/CompNfse[1]/Nfse[1]/InfNfse[1]/Numero[1]/text()", registro, StringComparison.Ordinal);

        // A reexecução repete os dois lados, uma vez: o novo atendeu 2 vezes (cliente e reexecução), o legado também.
        await SombraTests.Eventualmente(() => _legado.Recebidas.Count == 2 && _novo.Recebidas.Count == 2);
    }

    [Fact]
    public async Task Com_fallback_quem_respondeu_foi_o_legado_e_nao_ha_copia()
    {
        _novo.Responder = SombraTests.Responder([], StatusCodes.Status503ServiceUnavailable);
        await using var fachada = Fachada(Flags(100));

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));
        Assert.Equal(Exemplos.Bytes(RespostaDaConsulta), await resposta.Content.ReadAsByteArrayAsync());

        // Uma segunda leitura, com o novo de volta: quando ela é comparada, uma cópia da primeira já teria passado (FIFO).
        _novo.Responder = SombraTests.Responder(Exemplos.Bytes(RespostaDaConsulta));
        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta))).Dispose();
        await SombraTests.Eventualmente(() => LogsDaSombra(fachada).Any(m => m.Contains("Sombra ConsultarNfse: igual", StringComparison.Ordinal)));

        Assert.Equal(2, _legado.Recebidas.Count); // o fallback da primeira e a cópia da segunda
        Assert.Single(LogsDaSombra(fachada), m => m.Contains("Sombra ConsultarNfse", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Legado_fora_do_ar_na_copia_e_descarte_e_o_cliente_nao_percebe()
    {
        var legado = new NovoComFalha((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "conexão recusada"));
        await using var fachada = Fachada(Flags(100), legado);

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(Exemplos.Bytes(RespostaDaConsulta), await resposta.Content.ReadAsByteArrayAsync());
        await SombraTests.Eventualmente(() => LogsDaSombra(fachada).Any(m => m.Contains("cópia descartada (erro_legado)", StringComparison.Ordinal)));
        Assert.DoesNotContain(LogsDaSombra(fachada), m => m.Contains("erro_novo", StringComparison.Ordinal));
        Assert.Equal(1, legado.Chamadas);
    }

    [Fact]
    public async Task Sem_a_flag_o_modo_novo_nao_copia()
    {
        FlagsDeMigracao flags = Flags(100);
        flags.Operacoes[OperacoesSoap.ConsultarNfse].Sombra.Invertida = false;
        await using var fachada = Fachada(flags);

        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta))).Dispose();
        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfseServicoTomado, Exemplos.Bytes("05-consultar-nfse-servico-tomado.xml"))).Dispose();

        await SombraTests.Eventualmente(() => LogsDaSombra(fachada).Any(m => m.Contains("Sombra ConsultarNfseServicoTomado", StringComparison.Ordinal)));
        Assert.Equal([Exemplos.SoapAction(OperacoesSoap.ConsultarNfseServicoTomado)], _legado.Recebidas.Select(r => r.SoapAction));
    }

    [Fact]
    public async Task CancelarNfse_com_a_flag_mesmo_sem_validacao_vai_so_ao_novo_uma_vez_e_nunca_ao_legado()
    {
        // FlagsFixas entrega o retrato sem Validar(): é o caso de uma fonte com defeito. A fachada continua sem copiar.
        FlagsDeMigracao flags = Flags(100);
        Assert.NotEmpty(flags.Validar());
        _novo.Responder = SombraTests.Responder(Exemplos.Bytes("06-cancelar-nfse.resposta.xml"));
        await using var fachada = Fachada(flags);

        using HttpResponseMessage resposta = await fachada.EnviarSoapAsync(OperacoesSoap.CancelarNfse, Exemplos.Bytes("06-cancelar-nfse.xml"));
        Assert.Equal(Exemplos.Bytes("06-cancelar-nfse.resposta.xml"), await resposta.Content.ReadAsByteArrayAsync());

        // Uma leitura invertida depois: quando ela é comparada, uma eventual cópia do cancelamento já teria passado (FIFO).
        _novo.Responder = SombraTests.Responder(Exemplos.Bytes(RespostaDaConsulta));
        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes(Consulta))).Dispose();
        await SombraTests.Eventualmente(() => LogsDaSombra(fachada).Any(m => m.Contains("Sombra ConsultarNfse: igual", StringComparison.Ordinal)));
        await Task.Delay(200);

        Assert.Equal(Exemplos.SoapAction(OperacoesSoap.CancelarNfse), _novo.Recebidas.First().SoapAction);
        Assert.Single(_novo.Recebidas, r => r.SoapAction == Exemplos.SoapAction(OperacoesSoap.CancelarNfse));
        Assert.DoesNotContain(_legado.Recebidas, r => r.SoapAction == Exemplos.SoapAction(OperacoesSoap.CancelarNfse));
        Assert.All(_novo.Recebidas, r => Assert.Equal("/NfseWS.asmx", r.Caminho));
        Assert.DoesNotContain(LogsDaSombra(fachada), m => m.Contains("Sombra CancelarNfse", StringComparison.Ordinal));
    }

    [Fact]
    public void So_leituras_em_novo_com_a_flag_sao_copiaveis()
    {
        FlagsDeMigracao flags = Flags(100);

        Assert.True(PublicadorDaSombra.CopiavelInvertida(OperacoesSoap.ConsultarNfse, flags));
        Assert.True(PublicadorDaSombra.CopiavelInvertida(OperacoesSoap.ConsultarNfseServicoTomado, flags));
        Assert.False(PublicadorDaSombra.CopiavelInvertida(OperacoesSoap.CancelarNfse, flags));
        Assert.False(PublicadorDaSombra.CopiavelInvertida("RecepcionarLoteRps", flags));
        Assert.False(PublicadorDaSombra.CopiavelInvertida(OperacoesSoap.ConsultarNfse, null));

        flags.Operacoes[OperacoesSoap.ConsultarNfse].Modo = ModoDeMigracao.Sombra;
        Assert.False(PublicadorDaSombra.CopiavelInvertida(OperacoesSoap.ConsultarNfse, flags));
    }

    /// <summary>As três operações em <c>novo</c> a 100%, com sombra invertida (inclusive no cancelamento, que é inválido).</summary>
    private static FlagsDeMigracao Flags(int amostragem)
    {
        FlagsDeMigracao flags = SombraTests.Flags(amostragem);
        foreach (FlagsDaOperacao op in flags.Operacoes.Values)
        {
            op.Modo = ModoDeMigracao.Novo;
            op.Percentual = 100;
            op.Sombra.Invertida = true;
        }

        return flags;
    }

    private static IEnumerable<string> LogsDaSombra(FabricaDaFachada fachada) =>
        fachada.Logs.Mensagens.Where(m => m.Contains("|Sombra ", StringComparison.Ordinal));

    private FabricaDaFachada Fachada(FlagsDeMigracao flags, HttpMessageHandler? legado = null) =>
        new(legado ?? _legado.CriarHandler(), _novo.CriarHandler(), new FlagsFixas(flags), new Dictionary<string, string?>
        {
            ["Novo:UrlInterna"] = "http://novo.test:8081",
            ["Sombra:ChaveHmac"] = "chave-de-teste-da-sombra",
            ["Logging:LogLevel:Nfse.Fachada"] = "Debug",
        });
}
