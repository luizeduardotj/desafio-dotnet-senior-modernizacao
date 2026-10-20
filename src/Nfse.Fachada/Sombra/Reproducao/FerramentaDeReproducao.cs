using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Sombra.Reproducao;

/// <summary>
/// <c>dotnet Nfse.Fachada.dll reproduzir ...</c>: decifra um arquivo de reprodução da sombra e reexecuta o caso contra
/// os dois alvos (ADR-0009, fase 2). Roda na mesma imagem da fachada, num job ou pod do papel de operação, com a chave
/// por variável de ambiente. A saída nunca traz o pedido nem as respostas: só status, SHA-256 e caminhos XPath.
/// </summary>
/// <remarks>
/// <c>CancelarNfse</c> nunca é reenviado ao <c>/NfseWS.asmx</c> de nenhum dos lados: cancelaria de novo. Ele é
/// reavaliado sem escrita no endpoint interno do novo (seção 3), com a resposta do legado guardada no arquivo.
/// </remarks>
public static class FerramentaDeReproducao
{
    public const string Comando = "reproduzir";

    private const int Igual = 0;
    private const int Divergente = 1;
    private const int Uso = 2;
    private const int Falha = 3;

    private const string Ajuda = """
        uso:
          reproduzir decifrar <arquivo> --saida <diretório>
          reproduzir reexecutar <arquivo> --legado <url> --novo <url>          (leituras)
          reproduzir reexecutar <arquivo> --novo-interno <url>                  (CancelarNfse: só avalia, sem escrita)
        chaves: Sombra__Reproducao__ChaveId e Sombra__Reproducao__Chave (atual) e
                Sombra__Reproducao__ChavesAnteriores__<id> (rotacionadas, ainda na retenção)
        saída: 0 igual, 1 divergente, 2 uso ou chave, 3 falha de rede
        """;

    public static async Task<int> ExecutarAsync(string[] args, TextWriter saida, CancellationToken ct)
    {
        IConfiguration ambiente = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        // await, e não return da Task: com return, o using descartaria o handler antes de a requisição sair.
        using var rede = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        return await ExecutarAsync(args, ambiente, rede, saida, ct).ConfigureAwait(false);
    }

    internal static async Task<int> ExecutarAsync(
        string[] args, IConfiguration configuracao, HttpMessageHandler rede, TextWriter saida, CancellationToken ct, TimeProvider? relogio = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(saida);
        if (args.Length < 2 || args[0] is not ("decifrar" or "reexecutar") || LerOpcoes(args[2..]) is not { } opcoes)
        {
            await saida.WriteLineAsync(Ajuda).ConfigureAwait(false);
            return Uso;
        }

        RegistroDeReproducao registro;
        ConteudoDeReproducao conteudo;
        try
        {
            string json = await File.ReadAllTextAsync(args[1], ct).ConfigureAwait(false);
            registro = JsonSerializer.Deserialize<RegistroDeReproducao>(json, EnvelopeCifrado.Json)
                ?? throw new JsonException("vazio");
            conteudo = EnvelopeCifrado.Decifrar(registro, Chaves(configuracao));
        }
        catch (Exception erro) when (erro is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            await saida.WriteLineAsync($"não foi possível decifrar: {erro.GetType().Name}: {erro.Message}").ConfigureAwait(false);
            return Uso;
        }

        await saida.WriteLineAsync(
            $"{registro.Id}: {registro.Operacao}, gravado em {DateTimeOffset.FromUnixTimeSeconds(registro.CriadoEmUnix):u}, " +
            $"trace {registro.TraceId}, chave {registro.ChaveId}, {(conteudo.Invertida ? "sombra invertida (o novo respondeu)" : "sombra (o legado respondeu)")}")
            .ConfigureAwait(false);

        if (args[0] == "decifrar")
        {
            return await DecifrarAsync(registro, conteudo, opcoes, saida, ct).ConfigureAwait(false);
        }

        using var cliente = new HttpClient(rede, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        try
        {
            return string.Equals(registro.Operacao, OperacoesSoap.CancelarNfse, StringComparison.Ordinal)
                ? await ReavaliarCancelamentoAsync(cliente, registro, conteudo, opcoes, saida, relogio ?? TimeProvider.System, ct).ConfigureAwait(false)
                : await ReexecutarLeituraAsync(cliente, conteudo, opcoes, saida, ct).ConfigureAwait(false);
        }
        catch (Exception erro) when (erro is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            await saida.WriteLineAsync($"falha de rede: {erro.GetType().Name}").ConfigureAwait(false);
            return Falha;
        }
    }

    /// <summary>Grava o pedido (e, no cancelamento, a resposta do legado) em claro, só no diretório pedido e só para o dono.</summary>
    private static async Task<int> DecifrarAsync(
        RegistroDeReproducao registro, ConteudoDeReproducao conteudo, Dictionary<string, string> opcoes, TextWriter saida, CancellationToken ct)
    {
        if (!opcoes.TryGetValue("--saida", out string? diretorio))
        {
            await saida.WriteLineAsync(Ajuda).ConfigureAwait(false);
            return Uso;
        }

        Directory.CreateDirectory(diretorio);
        string requisicao = Path.Combine(diretorio, registro.Id + ".requisicao.xml");
        await GravarSoDoDonoAsync(requisicao, conteudo.Requisicao, ct).ConfigureAwait(false);
        await saida.WriteLineAsync($"pedido em {requisicao} (SOAPAction {conteudo.SoapAction})").ConfigureAwait(false);
        if (conteudo.Resposta is { } resposta)
        {
            string caminho = Path.Combine(diretorio, registro.Id + ".resposta.xml");
            await GravarSoDoDonoAsync(caminho, resposta, ct).ConfigureAwait(false);
            await saida.WriteLineAsync($"resposta do legado em {caminho} (status {conteudo.Status})").ConfigureAwait(false);
        }

        await saida.WriteLineAsync("ATENÇÃO: dado pessoal em claro. Use só no ambiente restrito e apague ao terminar (ADR-0012).").ConfigureAwait(false);
        return Igual;
    }

    /// <summary>Leitura: o mesmo POST nos dois lados, comparado em bytes, como o worker da sombra faz.</summary>
    private static async Task<int> ReexecutarLeituraAsync(
        HttpClient cliente, ConteudoDeReproducao conteudo, Dictionary<string, string> opcoes, TextWriter saida, CancellationToken ct)
    {
        if (!opcoes.TryGetValue("--legado", out string? legado) || !opcoes.TryGetValue("--novo", out string? novo))
        {
            await saida.WriteLineAsync(Ajuda).ConfigureAwait(false);
            return Uso;
        }

        (int Status, byte[] Corpo) l = await EnviarSoapAsync(cliente, legado, conteudo, ct).ConfigureAwait(false);
        (int Status, byte[] Corpo) n = await EnviarSoapAsync(cliente, novo, conteudo, ct).ConfigureAwait(false);

        // O registro guarda o SHA-256 do que o cliente recebeu: do novo na sombra invertida, do legado na direta.
        (string ladoDoCliente, byte[] agora) = conteudo.Invertida ? ("novo", n.Corpo) : ("legado", l.Corpo);
        string hashAgora = ComparadorDaSombra.Sha256(agora);
        await saida.WriteLineAsync($"legado: status {l.Status} sha256 {ComparadorDaSombra.Sha256(l.Corpo)}").ConfigureAwait(false);
        await saida.WriteLineAsync($"novo:   status {n.Status} sha256 {ComparadorDaSombra.Sha256(n.Corpo)}").ConfigureAwait(false);
        await saida.WriteLineAsync(
            $"{ladoDoCliente} hoje {(hashAgora == conteudo.Sha256Resposta && (conteudo.Invertida ? n.Status : l.Status) == conteudo.Status ? "igual" : "diferente")} " +
            $"ao que o cliente recebeu (status {conteudo.Status} sha256 {conteudo.Sha256Resposta})").ConfigureAwait(false);

        bool igual = l.Status == n.Status && l.Corpo.AsSpan().SequenceEqual(n.Corpo);
        await saida.WriteLineAsync($"resultado: {(igual ? "igual" : "divergente")}").ConfigureAwait(false);
        if (!igual)
        {
            await saida.WriteLineAsync("caminhos: " + string.Join(' ', DiferencaXml.Caminhos(l.Corpo, n.Corpo))).ConfigureAwait(false);
        }

        return igual ? Igual : Divergente;
    }

    /// <summary>
    /// Cancelamento: nunca vai ao SOAP de nenhum lado. O novo reavalia a decisão sem escrever, com a resposta guardada do
    /// legado. A idade é o tempo desde a gravação: o novo reconstrói <c>T0</c> perto do instante original, mas o estado
    /// da nota pode ter mudado desde então (outro cancelamento, guia paga), o que aparece como <c>corrida</c>.
    /// </summary>
    private static async Task<int> ReavaliarCancelamentoAsync(
        HttpClient cliente, RegistroDeReproducao registro, ConteudoDeReproducao conteudo, Dictionary<string, string> opcoes, TextWriter saida,
        TimeProvider relogio, CancellationToken ct)
    {
        if (!opcoes.TryGetValue("--novo-interno", out string? interno) || conteudo.Resposta is null)
        {
            await saida.WriteLineAsync("CancelarNfse só é reavaliado no endpoint interno do novo (--novo-interno); nunca é reenviado ao SOAP.").ConfigureAwait(false);
            return Uso;
        }

        // Idade do registro pelo relógio de quem roda a ferramenta, fora do serviço: o novo reconstrói o T0 com o SYSDATE do
        // banco menos essa idade (ADR-0009), então só um relógio muito adiantado ou atrasado aqui desloca a reavaliação.
        long idadeMs = (long)(relogio.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(registro.CriadoEmUnix)).TotalMilliseconds;
        var pedido = new
        {
            envelope = Encoding.UTF8.GetString(conteudo.Requisicao),
            respostaLegado = Encoding.UTF8.GetString(conteudo.Resposta),
            statusLegado = conteudo.Status,
            idadeMs = Math.Max(0, idadeMs),
        };
        using var conteudoHttp = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(pedido, EnvelopeCifrado.Json));
        conteudoHttp.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
        using HttpResponseMessage resposta = await cliente.PostAsync(
            new Uri(interno.TrimEnd('/') + AvaliadorDoCancelamento.Caminho), conteudoHttp, ct).ConfigureAwait(false);
        byte[] corpo = await resposta.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (!resposta.IsSuccessStatusCode)
        {
            await saida.WriteLineAsync($"novo interno: http {(int)resposta.StatusCode}").ConfigureAwait(false);
            return Falha;
        }

        using JsonDocument avaliacao = JsonDocument.Parse(corpo);
        string Campo(string nome) =>
            avaliacao.RootElement.TryGetProperty(nome, out JsonElement v) && v.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False
                ? Curto(v.ToString())
                : "-";
        string resultado = Campo("resultado");
        await saida.WriteLineAsync(
            $"novo interno: resultado {resultado} classe {Campo("classe")} decisão legado {Campo("decisaoLegado")} novo {Campo("decisaoNovo")} " +
            $"cnpjConfere {Campo("cnpjConfere")}").ConfigureAwait(false);
        return resultado == "igual" ? Igual : Divergente;
    }

    private static async Task<(int Status, byte[] Corpo)> EnviarSoapAsync(HttpClient cliente, string url, ConteudoDeReproducao conteudo, CancellationToken ct)
    {
        using var requisicao = new HttpRequestMessage(HttpMethod.Post, new Uri(url.TrimEnd('/') + "/NfseWS.asmx"))
        {
            Content = new ByteArrayContent(conteudo.Requisicao),
        };
        if (conteudo.ContentType is not null)
        {
            requisicao.Content.Headers.TryAddWithoutValidation("Content-Type", conteudo.ContentType);
        }

        requisicao.Headers.TryAddWithoutValidation("SOAPAction", conteudo.SoapAction);
        using HttpResponseMessage resposta = await cliente.SendAsync(requisicao, ct).ConfigureAwait(false);
        return ((int)resposta.StatusCode, await resposta.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Chave atual e anteriores, por identificador; uma chave malformada é ignorada (o arquivo dela não abre).</summary>
    internal static Dictionary<string, byte[]> Chaves(IConfiguration configuracao)
    {
        IConfigurationSection secao = configuracao.GetSection(OpcoesReproducao.Secao);
        var chaves = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (IConfigurationSection anterior in secao.GetSection(nameof(OpcoesReproducao.ChavesAnteriores)).GetChildren())
        {
            if (OpcoesReproducao.ChaveValida(anterior.Value))
            {
                chaves[anterior.Key] = Convert.FromBase64String(anterior.Value!);
            }
        }

        if (secao[nameof(OpcoesReproducao.ChaveId)] is { Length: > 0 } id && OpcoesReproducao.ChaveValida(secao[nameof(OpcoesReproducao.Chave)]))
        {
            chaves[id] = Convert.FromBase64String(secao[nameof(OpcoesReproducao.Chave)]!);
        }

        return chaves;
    }

    private static Dictionary<string, string>? LerOpcoes(string[] args)
    {
        var opcoes = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--saida" or "--legado" or "--novo" or "--novo-interno"))
            {
                return null;
            }

            opcoes[args[i]] = args[i + 1];
        }

        return opcoes;
    }

    /// <summary>Só códigos curtos chegam ao terminal, como no log da sombra.</summary>
    private static string Curto(string valor) =>
        valor is { Length: > 0 and <= 32 } && valor.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? valor : "?";

    private static async Task GravarSoDoDonoAsync(string caminho, byte[] conteudo, CancellationToken ct)
    {
        var modo = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
        {
            modo.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        FileStream arquivo = new(caminho, modo);
        await using (arquivo.ConfigureAwait(false))
        {
            await arquivo.WriteAsync(conteudo, ct).ConfigureAwait(false);
        }
    }
}
