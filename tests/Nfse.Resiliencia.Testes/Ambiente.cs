using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nfse.Resiliencia.Testes;

/// <summary>
/// O compose de resiliência visto de fora: fachada, serviço novo, API do Toxiproxy, pasta de flags e logs da fachada.
/// Os padrões batem com <c>tests/resiliencia/docker-compose.resiliencia.yml</c> e <c>rodar.sh</c>.
/// </summary>
public sealed partial class Ambiente : IAsyncLifetime
{
    public const string Proxy = "oracle";

    public Uri Fachada { get; } = Url("NFSE_RESILIENCIA_FACHADA", "http://127.0.0.1:18080");

    public Uri Novo { get; } = Url("NFSE_RESILIENCIA_NOVO", "http://127.0.0.1:18090");

    public Uri Toxiproxy { get; } = Url("NFSE_RESILIENCIA_TOXIPROXY", "http://127.0.0.1:18474");

    /// <summary>NFSE_OWNER direto no Oracle (sem Toxiproxy): segura o lock da nota e confere o estado.</summary>
    public string OracleDoLegado { get; } = Variavel(
        "NFSE_RESILIENCIA_ORACLE", "User Id=NFSE_OWNER;Password=nfse_dev_123;Data Source=127.0.0.1:11521/FREEPDB1");

    public string PastaDeFlags { get; } = Variavel(
        "NFSE_RESILIENCIA_FLAGS", Path.Combine(Repositorio.Raiz, "tests", "resiliencia", ".flags"));

    /// <summary>Argumentos do <c>docker compose</c> do cenário (project name e arquivos).</summary>
    public string Compose { get; } = Variavel(
        "NFSE_RESILIENCIA_COMPOSE", "-p nfse-resiliencia -f docker-compose.yml -f tests/resiliencia/docker-compose.resiliencia.yml");

    /// <summary>Sem timeout próprio: quem mede é o teste, e um cliente que desistisse antes esconderia a espera.</summary>
    public HttpClient Http { get; } = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(30) })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public Medicoes Medicoes { get; } = new();

    private string? _containerDaFachada;
    private string? _containerDoNovo;

    public async Task InitializeAsync()
    {
        _containerDaFachada = (await DockerAsync($"compose {Compose} ps -q fachada")).Trim();
        _containerDoNovo = (await DockerAsync($"compose {Compose} ps -q nfse-novo")).Trim();
        if (_containerDaFachada.Length == 0 || _containerDoNovo.Length == 0)
        {
            throw new InvalidOperationException("fachada ou nfse-novo não estão de pé no compose de resiliência");
        }
    }

    public Task DisposeAsync()
    {
        Medicoes.Gravar();
        Http.Dispose();
        return Task.CompletedTask;
    }

    // ---- Toxiproxy (API REST, https://github.com/Shopify/toxiproxy#http-api) ----

    /// <summary>Tira todos os toxics e religa o proxy.</summary>
    public async Task OracleNormalAsync()
    {
        using HttpResponseMessage r = await Http.PostAsync(new Uri(Toxiproxy, "/reset"), null);
        r.EnsureSuccessStatusCode();
    }

    /// <summary>Atraso em cada pacote do Oracle para o novo (sentido downstream), também nas conexões já abertas.</summary>
    public Task OracleLentoAsync(TimeSpan atraso) => ToxicAsync(new
    {
        name = "latencia",
        type = "latency",
        stream = "downstream",
        attributes = new { latency = (int)atraso.TotalMilliseconds, jitter = 0 },
    });

    /// <summary>Partição de rede: os dados param de passar e as conexões ficam penduradas, sem erro (toxic <c>timeout</c> 0).</summary>
    public Task OracleSemRespostaAsync() => ToxicAsync(new
    {
        name = "sem_resposta",
        type = "timeout",
        stream = "upstream",
        attributes = new { timeout = 0 },
    });

    /// <summary>Proxy desligado: conexões existentes caem e as novas são recusadas.</summary>
    public async Task OracleForaDoArAsync()
    {
        using HttpResponseMessage r = await Http.PostAsJsonAsync(new Uri(Toxiproxy, $"/proxies/{Proxy}"), new { enabled = false });
        r.EnsureSuccessStatusCode();
    }

    private async Task ToxicAsync(object toxic)
    {
        using HttpResponseMessage r = await Http.PostAsJsonAsync(new Uri(Toxiproxy, $"/proxies/{Proxy}/toxics"), toxic);
        r.EnsureSuccessStatusCode();
    }

    // ---- Flags da fachada (arquivo observado; o compose de resiliência monta a pasta) ----

    /// <summary>Reescreve o flags.json e espera a fachada aplicar: a sonda da operação tem de sair com o motivo esperado.</summary>
    public async Task FlagsAsync(string consultarNfse, string cancelarNfse, int amostragem = 0)
    {
        string Operacao(string modo) => JsonSerializer.Serialize(new
        {
            Modo = modo,
            Percentual = modo == "novo" ? 100 : 0,
            KillSwitch = false,
            Inclusao = Array.Empty<string>(),
            Exclusao = Array.Empty<string>(),
            Sombra = new { Amostragem = modo == "sombra" ? amostragem : 0 },
        });

        string json =
            "{\"Migracao\":{\"KillSwitch\":false,\"Operacoes\":{" +
            $"\"ConsultarNfse\":{Operacao(consultarNfse)}," +
            $"\"ConsultarNfseServicoTomado\":{Operacao("legado")}," +
            $"\"CancelarNfse\":{Operacao(cancelarNfse)}" +
            "}}}";

        // Troca atômica: o watcher por polling nunca lê o arquivo pela metade.
        string temporario = Path.Combine(PastaDeFlags, ".flags.json.tmp");
        await File.WriteAllTextAsync(temporario, json);
        File.Move(temporario, Path.Combine(PastaDeFlags, "flags.json"), overwrite: true);

        await AguardarRoteamentoAsync(Soap.ConsultarNfse(), MotivoEsperado(consultarNfse));
        await AguardarRoteamentoAsync(Soap.CancelarInexistente(), MotivoEsperado(cancelarNfse));
    }

    private static string MotivoEsperado(string modo) => modo switch
    {
        "novo" => "-> Novo (Percentual)",
        "sombra" => "-> Legado (Sombra)",
        _ => "-> Legado (ModoLegado)",
    };

    private async Task AguardarRoteamentoAsync(Soap sonda, string esperado)
    {
        var limite = Stopwatch.StartNew();
        while (true)
        {
            Resposta r = await EnviarAsync(sonda);
            if ((await RoteamentoAsync(r)).Contains(esperado, StringComparison.Ordinal))
            {
                return;
            }

            if (limite.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException($"a fachada não aplicou as flags em 30 s (esperado \"{esperado}\")");
            }

            await Task.Delay(500);
        }
    }

    // ---- Requisições ----

    /// <summary>POST SOAP pela fachada com um <c>traceparent</c> próprio, para achar a linha de roteamento no log.</summary>
    public async Task<Resposta> EnviarAsync(Soap soap)
    {
        string traceId = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        using var conteudo = new StringContent(soap.Envelope, Encoding.UTF8, "text/xml");
        using var pedido = new HttpRequestMessage(HttpMethod.Post, new Uri(Fachada, "/NfseWS.asmx")) { Content = conteudo };
        pedido.Headers.Add("SOAPAction", $"\"http://notacarioca.rio.gov.br/{soap.Operacao}\"");
        pedido.Headers.Add("traceparent", $"00-{traceId}-{traceId[..16]}-01");

        DateTimeOffset enviadaEm = DateTimeOffset.UtcNow;
        var cronometro = Stopwatch.StartNew();
        using HttpResponseMessage resposta = await Http.SendAsync(pedido);
        string corpo = await resposta.Content.ReadAsStringAsync();
        return new Resposta(resposta.StatusCode, corpo, cronometro.Elapsed, traceId, enviadaEm);
    }

    public async Task<HttpStatusCode> ProntidaoDoNovoAsync()
    {
        using HttpResponseMessage r = await Http.GetAsync(new Uri(Novo, "/health/ready"));
        return r.StatusCode;
    }

    public async Task<HttpStatusCode> VidaDoNovoAsync()
    {
        using HttpResponseMessage r = await Http.GetAsync(new Uri(Novo, "/health/live"));
        return r.StatusCode;
    }

    /// <summary>Espera o <c>/health/ready</c> do novo chegar ao status pedido; devolve quanto levou.</summary>
    public async Task<TimeSpan> AguardarProntidaoAsync(HttpStatusCode esperado, TimeSpan limite)
    {
        var cronometro = Stopwatch.StartNew();
        while (await ProntidaoDoNovoAsync() != esperado)
        {
            if (cronometro.Elapsed > limite)
            {
                throw new TimeoutException($"/health/ready do novo não chegou a {(int)esperado} em {limite.TotalSeconds} s");
            }

            await Task.Delay(250);
        }

        return cronometro.Elapsed;
    }

    // ---- Logs da fachada (JSON no console, com TraceId nos scopes) ----

    /// <summary>A linha "SOAP ... -> destino (motivo)" da requisição, achada pelo traceId.</summary>
    public async Task<string> RoteamentoAsync(Resposta resposta)
    {
        for (int tentativa = 0; tentativa < 20; tentativa++)
        {
            string? linha = (await LogsDaFachadaAsync(resposta.EnviadaEm))
                .FirstOrDefault(l => l.Contains(resposta.TraceId, StringComparison.Ordinal) && l.Contains("EncaminhadorSoap", StringComparison.Ordinal)
                    && l.Contains("\"EventId\":1,", StringComparison.Ordinal));
            if (linha is not null)
            {
                return linha;
            }

            await Task.Delay(250);
        }

        throw new InvalidOperationException($"sem linha de roteamento no log da fachada para o trace {resposta.TraceId}");
    }

    public async Task<IReadOnlyList<string>> LogsDaFachadaAsync(DateTimeOffset desde)
    {
        string saida = await DockerAsync($"logs --since {desde.AddSeconds(-1).ToUnixTimeSeconds()} {_containerDaFachada}");
        // O JSON do console escapa '>' (a seta de "SOAP X -> Novo") e os acentos: volta ao texto da mensagem.
        return Escapes().Replace(saida, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary><c>StartedAt</c> e <c>RestartCount</c> do container do novo: provam que a recuperação foi sem restart.</summary>
    public async Task<string> EstadoDoContainerDoNovoAsync() =>
        (await DockerAsync($"inspect --format {{{{.State.StartedAt}}}}/{{{{.RestartCount}}}} {_containerDoNovo}")).Trim();

    private static async Task<string> DockerAsync(string argumentos)
    {
        var inicio = new ProcessStartInfo("docker", argumentos)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Repositorio.Raiz,
        };
        using Process processo = Process.Start(inicio) ?? throw new InvalidOperationException("docker não iniciou");
        Task<string> saida = processo.StandardOutput.ReadToEndAsync();
        Task<string> erro = processo.StandardError.ReadToEndAsync();
        await processo.WaitForExitAsync();
        if (processo.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {argumentos}: {await erro}");
        }

        // docker logs escreve o stderr do container no stderr: os dois juntos.
        return await saida + await erro;
    }

    [GeneratedRegex(@"\\u([0-9A-Fa-f]{4})")]
    private static partial Regex Escapes();

    private static Uri Url(string variavel, string padrao) => new(Variavel(variavel, padrao));

    private static string Variavel(string nome, string padrao) =>
        Environment.GetEnvironmentVariable(nome) is { Length: > 0 } valor ? valor : padrao;
}

/// <param name="TraceId">Do <c>traceparent</c> enviado; a fachada o continua no log.</param>
public sealed record Resposta(HttpStatusCode Status, string Corpo, TimeSpan Duracao, string TraceId, DateTimeOffset EnviadaEm);

[CollectionDefinition(Nome, DisableParallelization = true)]
public sealed class ColecaoDeResiliencia : ICollectionFixture<Ambiente>
{
    public const string Nome = "resiliencia";
}

internal static class Repositorio
{
    public static string Raiz { get; } = Encontrar();

    private static string Encontrar()
    {
        var pasta = new DirectoryInfo(AppContext.BaseDirectory);
        while (pasta is not null && !File.Exists(Path.Combine(pasta.FullName, "Nfse.Moderno.slnx")))
        {
            pasta = pasta.Parent;
        }

        return pasta?.FullName ?? throw new InvalidOperationException("raiz do repositório não encontrada");
    }
}
