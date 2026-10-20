using System.Text;
using System.Text.RegularExpressions;
using Nfse.Fachada.Roteamento;
using Nfse.Fachada.Sombra.Reproducao;

namespace Nfse.Fachada.Testes.Integracao;

/// <summary>O registro da divergência aponta o arquivo cifrado, quando a reprodução está ligada (ADR-0009, fase 2).</summary>
public sealed partial class ReproducaoNaSombraTests : IAsyncLifetime, IDisposable
{
    private static readonly string Chave = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private readonly string _pasta = Directory.CreateTempSubdirectory("fachada-reproducao-").FullName;
    private ServidorFalso _legado = null!;
    private ServidorFalso _novo = null!;

    public async Task InitializeAsync()
    {
        byte[] resposta = Exemplos.Bytes("04-consultar-nfse.resposta.xml");
        _legado = await ServidorFalso.IniciarAsync();
        _legado.Responder = SombraTests.Responder(resposta);
        _novo = await ServidorFalso.IniciarAsync();
        _novo.Responder = SombraTests.Responder(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(resposta)
            .Replace("&lt;Numero&gt;23&lt;", "&lt;Numero&gt;99&lt;", StringComparison.Ordinal)));
    }

    public async Task DisposeAsync()
    {
        await _legado.DisposeAsync();
        await _novo.DisposeAsync();
    }

    public void Dispose() => Directory.Delete(_pasta, recursive: true);

    [Fact]
    public async Task Ligada_grava_o_pedido_cifrado_e_o_log_leva_so_o_identificador()
    {
        await using FabricaDaFachada fachada = Fachada(new()
        {
            ["Sombra:Reproducao:Habilitada"] = "true",
            ["Sombra:Reproducao:Diretorio"] = _pasta,
            ["Sombra:Reproducao:ChaveId"] = "k1",
            ["Sombra:Reproducao:Chave"] = Chave,
        });

        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes("04-consultar-nfse.xml"))).Dispose();

        string registro = await DivergenciaAsync(fachada);
        string id = Reproducao().Match(registro).Groups[1].Value;
        Assert.Equal(Path.Combine(_pasta, id + CofreDeReproducao.Extensao), Assert.Single(Directory.GetFiles(_pasta)));
        Assert.DoesNotContain(fachada.Logs.Mensagens, m => m.Contains(Chave, StringComparison.Ordinal) || m.Contains("11100003000113", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Desligada_por_padrao_o_log_diz_que_nao_ha_reproducao()
    {
        await using FabricaDaFachada fachada = Fachada(new() { ["Sombra:Reproducao:Diretorio"] = _pasta });

        (await fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes("04-consultar-nfse.xml"))).Dispose();

        Assert.EndsWith("reprodução -", (await DivergenciaAsync(fachada)).Split('|')[1], StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_pasta));
    }

    [Fact]
    public async Task Ligada_sem_chave_valida_a_fachada_nao_sobe()
    {
        await using FabricaDaFachada fachada = Fachada(new()
        {
            ["Sombra:Reproducao:Habilitada"] = "true",
            ["Sombra:Reproducao:Diretorio"] = _pasta,
            ["Sombra:Reproducao:ChaveId"] = "k1",
            ["Sombra:Reproducao:Chave"] = "curta",
        });

        var erro = await Assert.ThrowsAnyAsync<Exception>(() => fachada.EnviarSoapAsync(OperacoesSoap.ConsultarNfse, Exemplos.Bytes("04-consultar-nfse.xml")));
        Assert.Contains("Sombra:Reproducao:Chave", erro.ToString(), StringComparison.Ordinal);
    }

    private static async Task<string> DivergenciaAsync(FabricaDaFachada fachada)
    {
        await SombraTests.Eventualmente(() => fachada.Logs.Mensagens.Any(m => m.Contains("|Sombra ConsultarNfse: divergência", StringComparison.Ordinal)));
        return fachada.Logs.Mensagens.Single(m => m.Contains("|Sombra ConsultarNfse: divergência", StringComparison.Ordinal));
    }

    [GeneratedRegex("reprodução (\\d{8}T\\d{6}Z-[0-9a-f]{32})")]
    private static partial Regex Reproducao();

    private FabricaDaFachada Fachada(Dictionary<string, string?> configuracao)
    {
        configuracao["Sombra:ChaveHmac"] = "chave-de-teste-da-sombra";
        configuracao["Sombra:ReexecucoesPorSegundo"] = "0";
        configuracao["Logging:LogLevel:Nfse.Fachada"] = "Debug";
        return new FabricaDaFachada(_legado.CriarHandler(), _novo.CriarHandler(), new FlagsFixas(SombraTests.Flags(100)), configuracao);
    }
}
