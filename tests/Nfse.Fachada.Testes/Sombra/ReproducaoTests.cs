using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nfse.Fachada.Roteamento;
using Nfse.Fachada.Sombra;
using Nfse.Fachada.Sombra.Reproducao;
using Nfse.Fachada.Testes.Integracao;

namespace Nfse.Fachada.Testes.Sombra;

/// <summary>Payload cifrado da divergência e ferramenta de reprodução (ADR-0009, seção 4, fase 2; ADR-0012).</summary>
public sealed class ReproducaoTests : IDisposable
{
    private static readonly byte[] ChaveAtual = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] ChaveAntiga = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTimeOffset Agora = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);

    /// <summary>CNPJ, IM e motivo dos exemplos: não podem aparecer no arquivo nem na saída da ferramenta.</summary>
    private static readonly string[] DadosPessoais = ["11100003000113", "10000001", "13500075000183", "10000025", "Servico nao prestado", "PRESTADORA EXEMPLO"];

    private readonly string _pasta = Directory.CreateTempSubdirectory("fachada-reproducao-").FullName;

    public void Dispose() => Directory.Delete(_pasta, recursive: true);

    [Fact]
    public void Cifra_e_decifra_sem_dado_pessoal_em_claro_no_registro()
    {
        ConteudoDeReproducao conteudo = Conteudo(OperacoesSoap.ConsultarNfse);

        RegistroDeReproducao registro = EnvelopeCifrado.Cifrar(conteudo, "id-1", OperacoesSoap.ConsultarNfse, "trace", Agora, "k1", ChaveAtual);

        AssertSemDadosPessoais(JsonSerializer.Serialize(registro));
        ConteudoDeReproducao decifrado = EnvelopeCifrado.Decifrar(registro, new Dictionary<string, byte[]> { ["k1"] = ChaveAtual });
        Assert.Equal(conteudo.Requisicao, decifrado.Requisicao);
        Assert.Equal(conteudo.SoapAction, decifrado.SoapAction);
        Assert.Equal(conteudo.Sha256Resposta, decifrado.Sha256Resposta);
    }

    [Fact]
    public void Chave_errada_ausente_ou_metadado_alterado_nao_decifram()
    {
        RegistroDeReproducao registro = EnvelopeCifrado.Cifrar(Conteudo(OperacoesSoap.ConsultarNfse), "id-1", OperacoesSoap.ConsultarNfse, "trace", Agora, "k1", ChaveAtual);

        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCifrado.Decifrar(registro, new Dictionary<string, byte[]> { ["k1"] = ChaveAntiga }));
        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCifrado.Decifrar(registro, new Dictionary<string, byte[]> { ["k2"] = ChaveAtual }));

        // O dado associado amarra os metadados em claro: trocar a operação (ou o trace, o instante) invalida a tag.
        RegistroDeReproducao adulterado = registro with { Operacao = OperacoesSoap.CancelarNfse };
        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCifrado.Decifrar(adulterado, new Dictionary<string, byte[]> { ["k1"] = ChaveAtual }));
    }

    [Fact]
    public void Validacao_exige_diretorio_e_chave_de_32_bytes_sem_citar_a_chave()
    {
        Assert.Empty(Validar(new OpcoesReproducao()));

        var semChave = new OpcoesReproducao { Habilitada = true, Diretorio = _pasta, ChaveId = "k1", Chave = "c2VncmVkby1jdXJ0bw==" };
        string erros = string.Join(" | ", Validar(semChave).Select(r => r.ErrorMessage));
        Assert.Contains("Sombra:Reproducao:Chave", erros, StringComparison.Ordinal);
        Assert.DoesNotContain("c2VncmVkby1jdXJ0bw", erros, StringComparison.Ordinal);

        Assert.NotEmpty(Validar(new OpcoesReproducao { Habilitada = true, ChaveId = "k1", Chave = Convert.ToBase64String(ChaveAtual) }));
        Assert.NotEmpty(Validar(new OpcoesReproducao { Habilitada = true, Diretorio = _pasta, ChaveId = "chave com espaço", Chave = Convert.ToBase64String(ChaveAtual) }));
        Assert.Empty(Validar(Opcoes()));
    }

    [Fact]
    public async Task Desligada_por_padrao_nao_grava_nada()
    {
        var cofre = new CofreDeReproducao(Monitor(new OpcoesReproducao { Diretorio = _pasta }), NullLogger<CofreDeReproducao>.Instance);

        Assert.Equal("-", await cofre.GuardarAsync(Copia(OperacoesSoap.ConsultarNfse), "trace", CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_pasta));
    }

    [Fact]
    public async Task Leitura_guarda_so_o_pedido_e_o_hash_da_resposta_so_para_o_dono()
    {
        var cofre = new CofreDeReproducao(Monitor(Opcoes()), NullLogger<CofreDeReproducao>.Instance, new RelogioFixo(Agora));
        CopiaDaSombra copia = Copia(OperacoesSoap.ConsultarNfse);

        string id = await cofre.GuardarAsync(copia, "trace", CancellationToken.None);

        string arquivo = Assert.Single(Directory.GetFiles(_pasta));
        Assert.Equal(Path.Combine(_pasta, id + CofreDeReproducao.Extensao), arquivo);
        Assert.StartsWith("20261019T150000Z-", id, StringComparison.Ordinal);
        AssertSemDadosPessoais(await File.ReadAllTextAsync(arquivo));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(arquivo));
        }

        ConteudoDeReproducao conteudo = Decifrar(arquivo);
        Assert.Equal(copia.Requisicao, conteudo.Requisicao);
        Assert.Null(conteudo.Resposta);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(copia.Resposta)), conteudo.Sha256Resposta);
    }

    [Fact]
    public async Task Cancelamento_guarda_tambem_a_resposta_do_legado_para_a_reavaliacao()
    {
        var cofre = new CofreDeReproducao(Monitor(Opcoes()), NullLogger<CofreDeReproducao>.Instance);
        CopiaDaSombra copia = Copia(OperacoesSoap.CancelarNfse);

        await cofre.GuardarAsync(copia, "trace", CancellationToken.None);

        Assert.Equal(copia.Resposta, Decifrar(Assert.Single(Directory.GetFiles(_pasta))).Resposta);
    }

    [Fact]
    public async Task Configuracao_recarregada_invalida_nao_grava_nem_lanca()
    {
        OpcoesReproducao opcoes = Opcoes();
        opcoes.Diretorio = null;
        opcoes.Chave = "curta";
        var cofre = new CofreDeReproducao(Monitor(opcoes), NullLogger<CofreDeReproducao>.Instance);

        Assert.Equal("-", await cofre.GuardarAsync(Copia(OperacoesSoap.ConsultarNfse), "trace", CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_pasta));
    }

    [Fact]
    public async Task Acima_do_limite_por_segundo_nao_grava()
    {
        OpcoesReproducao opcoes = Opcoes();
        opcoes.PorSegundo = 1;
        var cofre = new CofreDeReproducao(Monitor(opcoes), NullLogger<CofreDeReproducao>.Instance);

        Assert.NotEqual("-", await cofre.GuardarAsync(Copia(OperacoesSoap.ConsultarNfse), "trace", CancellationToken.None));
        Assert.Equal("-", await cofre.GuardarAsync(Copia(OperacoesSoap.ConsultarNfse), "trace", CancellationToken.None));
        Assert.Single(Directory.GetFiles(_pasta));
    }

    [Fact]
    public void Limpeza_apaga_o_que_passou_da_retencao_e_temporarios_orfaos()
    {
        string Criar(DateTimeOffset quando, string extensao)
        {
            string caminho = Path.Combine(_pasta, CofreDeReproducao.NomeDoArquivo(quando, Guid.NewGuid()) + extensao);
            File.WriteAllText(caminho, "{}");
            return caminho;
        }

        string velho = Criar(Agora.AddDays(-15).AddSeconds(-1), CofreDeReproducao.Extensao);
        string recente = Criar(Agora.AddDays(-14), CofreDeReproducao.Extensao);
        string orfao = Criar(Agora.AddHours(-2), ".tmp");
        string gravando = Criar(Agora.AddMinutes(-1), ".tmp");
        string alheio = Path.Combine(_pasta, "19990101T000000Z-outra-coisa.txt");
        File.WriteAllText(alheio, "não é nosso");

        int apagados = new LimpezaDaReproducao(Monitor(Opcoes()), NullLogger<LimpezaDaReproducao>.Instance, new RelogioFixo(Agora)).Limpar();

        Assert.Equal(2, apagados);
        Assert.False(File.Exists(velho));
        Assert.False(File.Exists(orfao));
        Assert.True(File.Exists(recente));
        Assert.True(File.Exists(gravando));
        Assert.True(File.Exists(alheio));
    }

    [Fact]
    public async Task Ferramenta_decifra_com_chave_rotacionada_e_grava_o_pedido_so_onde_mandaram()
    {
        string arquivo = await GravarAsync(OperacoesSoap.ConsultarNfse, "k-antiga", ChaveAntiga);
        string saida = Path.Combine(_pasta, "claro");
        var terminal = new StringWriter();

        int codigo = await FerramentaDeReproducao.ExecutarAsync(
            ["decifrar", arquivo, "--saida", saida], Ambiente(comAntiga: true), new Rede(_ => throw new InvalidOperationException()), terminal, CancellationToken.None);

        Assert.Equal(0, codigo);
        Assert.Equal(Exemplos.Bytes("04-consultar-nfse.xml"), await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(saida))));
        AssertSemDadosPessoais(terminal.ToString());
    }

    [Fact]
    public async Task Ferramenta_sem_a_chave_do_arquivo_falha_sem_decifrar()
    {
        string arquivo = await GravarAsync(OperacoesSoap.ConsultarNfse, "k-antiga", ChaveAntiga);
        var terminal = new StringWriter();

        int codigo = await FerramentaDeReproducao.ExecutarAsync(
            ["reexecutar", arquivo, "--legado", "http://legado.test", "--novo", "http://novo.test"], Ambiente(comAntiga: false),
            new Rede(_ => throw new InvalidOperationException("não deveria chamar")), terminal, CancellationToken.None);

        Assert.Equal(2, codigo);
        Assert.Contains("sem a chave k-antiga", terminal.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 0, "resultado: igual")]
    [InlineData(true, 1, "resultado: divergente")]
    public async Task Ferramenta_reexecuta_a_leitura_nos_dois_lados_sem_mostrar_conteudo(bool divergir, int esperado, string linha)
    {
        string arquivo = await GravarAsync(OperacoesSoap.ConsultarNfse, "k1", ChaveAtual);
        byte[] resposta = Exemplos.Bytes("04-consultar-nfse.resposta.xml");
        byte[] outra = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(resposta).Replace("&lt;Numero&gt;23&lt;", "&lt;Numero&gt;99&lt;", StringComparison.Ordinal));
        var rede = new Rede(r => Corpo(r.RequestUri!.Host == "novo.test" && divergir ? outra : resposta));
        var terminal = new StringWriter();

        int codigo = await FerramentaDeReproducao.ExecutarAsync(
            ["reexecutar", arquivo, "--legado", "http://legado.test", "--novo", "http://novo.test/"], Ambiente(), rede, terminal, CancellationToken.None);

        Assert.Equal(esperado, codigo);
        Assert.Contains(linha, terminal.ToString(), StringComparison.Ordinal);
        Assert.Contains("legado hoje igual ao que o cliente recebeu", terminal.ToString(), StringComparison.Ordinal);
        Assert.Equal(["http://legado.test/NfseWS.asmx", "http://novo.test/NfseWS.asmx"], rede.Recebidas.Select(r => r.Url));
        Assert.All(rede.Recebidas, r => Assert.Equal(Exemplos.Bytes("04-consultar-nfse.xml"), r.Corpo));
        Assert.All(rede.Recebidas, r => Assert.Equal(Exemplos.SoapAction(OperacoesSoap.ConsultarNfse), r.SoapAction));
        AssertSemDadosPessoais(terminal.ToString());
    }

    [Fact]
    public async Task Ferramenta_nunca_reenvia_CancelarNfse_ao_soap_e_so_reavalia_no_interno()
    {
        string arquivo = await GravarAsync(OperacoesSoap.CancelarNfse, "k1", ChaveAtual);
        var rede = new Rede(_ => Corpo(Encoding.UTF8.GetBytes(
            "{\"resultado\":\"divergente\",\"classe\":\"corrida\",\"decisaoLegado\":\"sucesso\",\"decisaoNovo\":\"E053\",\"cnpjConfere\":true}")));
        var terminal = new StringWriter();

        // Com --legado e --novo e sem --novo-interno: recusa, sem nenhuma chamada.
        int recusado = await FerramentaDeReproducao.ExecutarAsync(
            ["reexecutar", arquivo, "--legado", "http://legado.test", "--novo", "http://novo.test"], Ambiente(), rede, terminal, CancellationToken.None);
        Assert.Equal(2, recusado);
        Assert.Empty(rede.Recebidas);

        int codigo = await FerramentaDeReproducao.ExecutarAsync(
            ["reexecutar", arquivo, "--novo-interno", "http://novo.test:8081"], Ambiente(), rede, terminal, CancellationToken.None);

        Assert.Equal(1, codigo);
        (string url, string soapAction, byte[] corpo) = Assert.Single(rede.Recebidas);
        Assert.Equal("http://novo.test:8081/interno/sombra/cancelamento", url);
        Assert.Empty(soapAction);
        using JsonDocument pedido = JsonDocument.Parse(corpo);
        Assert.Equal(Encoding.UTF8.GetString(Exemplos.Bytes("06-cancelar-nfse.xml")), pedido.RootElement.GetProperty("envelope").GetString());
        Assert.Equal(Encoding.UTF8.GetString(Exemplos.Bytes("06-cancelar-nfse.resposta.xml")), pedido.RootElement.GetProperty("respostaLegado").GetString());
        Assert.Contains("resultado divergente classe corrida decisão legado sucesso novo E053 cnpjConfere True", terminal.ToString(), StringComparison.Ordinal);
        AssertSemDadosPessoais(terminal.ToString());
    }

    [Fact]
    public async Task Ferramenta_sem_argumentos_mostra_o_uso()
    {
        var terminal = new StringWriter();

        Assert.Equal(2, await FerramentaDeReproducao.ExecutarAsync([], Ambiente(), new Rede(_ => throw new InvalidOperationException()), terminal, CancellationToken.None));
        Assert.Contains("reproduzir decifrar", terminal.ToString(), StringComparison.Ordinal);
    }

    private static ConteudoDeReproducao Conteudo(string operacao) =>
        new(Exemplos.SoapAction(operacao), "text/xml; charset=utf-8", Exemplos.Bytes(Pedido(operacao)), 200, "abc", null, false);

    private static string Pedido(string operacao) => operacao == OperacoesSoap.CancelarNfse ? "06-cancelar-nfse.xml" : "04-consultar-nfse.xml";

    private static CopiaDaSombra Copia(string operacao) => new(
        operacao,
        Exemplos.SoapAction(operacao),
        "text/xml; charset=utf-8",
        Exemplos.Bytes(Pedido(operacao)),
        200,
        Exemplos.Bytes(operacao == OperacoesSoap.CancelarNfse ? "06-cancelar-nfse.resposta.xml" : "04-consultar-nfse.resposta.xml"),
        Stopwatch.GetTimestamp(),
        default);

    private OpcoesReproducao Opcoes() => new()
    {
        Habilitada = true,
        Diretorio = _pasta,
        ChaveId = "k1",
        Chave = Convert.ToBase64String(ChaveAtual),
        PorSegundo = 1000,
    };

    private async Task<string> GravarAsync(string operacao, string chaveId, byte[] chave)
    {
        OpcoesReproducao opcoes = Opcoes();
        opcoes.ChaveId = chaveId;
        opcoes.Chave = Convert.ToBase64String(chave);
        string id = await new CofreDeReproducao(Monitor(opcoes), NullLogger<CofreDeReproducao>.Instance)
            .GuardarAsync(Copia(operacao), "0af7651916cd43dd8448eb211c80319c", CancellationToken.None);
        return Path.Combine(_pasta, id + CofreDeReproducao.Extensao);
    }

    private static IConfiguration Ambiente(bool comAntiga = false)
    {
        var valores = new Dictionary<string, string?>
        {
            ["Sombra:Reproducao:ChaveId"] = "k1",
            ["Sombra:Reproducao:Chave"] = Convert.ToBase64String(ChaveAtual),
        };
        if (comAntiga)
        {
            valores["Sombra:Reproducao:ChavesAnteriores:k-antiga"] = Convert.ToBase64String(ChaveAntiga);
        }

        return new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
    }

    private static ConteudoDeReproducao Decifrar(string arquivo) => EnvelopeCifrado.Decifrar(
        JsonSerializer.Deserialize<RegistroDeReproducao>(File.ReadAllText(arquivo), EnvelopeCifrado.Json)!,
        new Dictionary<string, byte[]> { ["k1"] = ChaveAtual });

    private static List<ValidationResult> Validar(OpcoesReproducao opcoes)
    {
        var erros = new List<ValidationResult>();
        Validator.TryValidateObject(opcoes, new ValidationContext(opcoes), erros, validateAllProperties: true);
        return erros;
    }

    private static MonitorFixo<OpcoesReproducao> Monitor(OpcoesReproducao valor) => new MonitorFixo<OpcoesReproducao>(valor);

    private static HttpResponseMessage Corpo(byte[] corpo) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(corpo) };

    private static void AssertSemDadosPessoais(string texto)
    {
        foreach (string dado in DadosPessoais)
        {
            Assert.DoesNotContain(dado, texto, StringComparison.Ordinal);
        }
    }

    private sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => agora;
    }

    private sealed class MonitorFixo<T>(T valor) : IOptionsMonitor<T>
    {
        public T CurrentValue => valor;

        public T Get(string? name) => valor;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>Alvos falsos da ferramenta: grava URL, SOAPAction e corpo de cada chamada.</summary>
    private sealed class Rede(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(string Url, string SoapAction, byte[] Corpo)> Recebidas { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] corpo = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Recebidas.Add((request.RequestUri!.ToString(), string.Join(',', request.Headers.TryGetValues("SOAPAction", out var v) ? v : []), corpo));
            return responder(request);
        }
    }
}
