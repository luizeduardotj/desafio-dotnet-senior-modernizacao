using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nfse.Fachada.Observabilidade;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Sombra.Reproducao;

/// <summary>
/// Grava, nos workers da sombra, o pedido original cifrado de cada divergência (ADR-0009, seção 4, fase 2). O log da
/// divergência leva só o identificador do arquivo. Nada aqui passa pelo caminho do cliente, e nada sai em claro.
/// </summary>
internal sealed class CofreDeReproducao(
    IOptionsMonitor<OpcoesReproducao> opcoes,
    ILogger<CofreDeReproducao> logger,
    TimeProvider? relogio = null)
{
    public const string Extensao = ".nfse-repro.json";

    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;
    private readonly LimiteDeReexecucao _limite = new(() => opcoes.CurrentValue.PorSegundo);

    /// <returns>O identificador do arquivo (o nome, sem dado pessoal); <c>-</c> se desligado, limitado ou com erro.</returns>
    public async Task<string> GuardarAsync(CopiaDaSombra copia, string traceId, CancellationToken ct)
    {
        OpcoesReproducao o = opcoes.CurrentValue;
        if (!o.Habilitada)
        {
            return "-";
        }

        // A validação da partida não cobre uma configuração recarregada depois: sem diretório ou chave válidos, não grava,
        // e a divergência continua sendo registrada.
        if (o.Validate(new(o)).Any())
        {
            TelemetriaDaSombra.ContarReproducao(copia.Operacao, "erro");
            Log.ReproducaoNaoGuardada(logger, copia.Operacao, "ConfiguracaoInvalida");
            return "-";
        }

        if (!_limite.TentarConsumir(copia.Operacao))
        {
            TelemetriaDaSombra.ContarReproducao(copia.Operacao, "limitada");
            return "-";
        }

        DateTimeOffset agora = _relogio.GetUtcNow();
        string id = NomeDoArquivo(agora, Guid.NewGuid());
        bool cancelamento = string.Equals(copia.Operacao, OperacoesSoap.CancelarNfse, StringComparison.Ordinal);
        var conteudo = new ConteudoDeReproducao(
            copia.SoapAction,
            copia.ContentType,
            copia.Requisicao,
            copia.Status,
            ComparadorDaSombra.Sha256(copia.Resposta),
            cancelamento ? copia.Resposta : null,
            copia.Invertida);

        string? temporario = null;
        try
        {
            byte[] chave = o.BytesDaChave();
            RegistroDeReproducao registro;
            try
            {
                registro = EnvelopeCifrado.Cifrar(conteudo, id, copia.Operacao, traceId, agora, o.ChaveId!, chave);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(chave);
            }

            // Grava num temporário e renomeia: a limpeza e a ferramenta nunca veem um arquivo pela metade. Só o dono lê.
            Directory.CreateDirectory(o.Diretorio!);
            temporario = Path.Combine(o.Diretorio!, id + ".tmp");
            var modo = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows())
            {
                modo.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            FileStream arquivo = new(temporario, modo);
            await using (arquivo.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(arquivo, registro, EnvelopeCifrado.Json, ct).ConfigureAwait(false);
            }

            File.Move(temporario, Path.Combine(o.Diretorio!, id + Extensao));
            TelemetriaDaSombra.ContarReproducao(copia.Operacao, "guardada");
            return id;
        }
        catch (Exception erro) when (erro is IOException or UnauthorizedAccessException or CryptographicException or FormatException)
        {
            // Só o tipo: a mensagem de IO pode citar o caminho, e nunca há conteúdo nela, mas o padrão da sombra é esse.
            TelemetriaDaSombra.ContarReproducao(copia.Operacao, "erro");
            Log.ReproducaoNaoGuardada(logger, copia.Operacao, erro.GetType().Name);
            TentarApagar(temporario);
            return "-";
        }
    }

    /// <summary>Instante no nome, para a limpeza decidir sem abrir o arquivo; o GUID evita colisão entre réplicas.</summary>
    internal static string NomeDoArquivo(DateTimeOffset criadoEm, Guid guid) =>
        criadoEm.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + guid.ToString("N");

    /// <summary>Instante gravado no nome; <c>null</c> se o nome não segue o formato.</summary>
    internal static DateTimeOffset? CriadoEm(string nomeDoArquivo) =>
        nomeDoArquivo.Length >= 16 && DateTimeOffset.TryParseExact(
            nomeDoArquivo.AsSpan(0, 16), "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset instante)
            ? instante
            : null;

    private static void TentarApagar(string? caminho)
    {
        try
        {
            if (caminho is not null)
            {
                File.Delete(caminho);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
