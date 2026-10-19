using System.Buffers;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Nfse.Fachada.Configuracao;
using Nfse.Fachada.Observabilidade;

namespace Nfse.Fachada.Sombra;

/// <summary>
/// Executa uma cópia de leitura no novo, compara com a resposta do legado e classifica a divergência (ADR-0009, seções 2 e 4).
/// Roda nos workers, fora do caminho do cliente, com timeout próprio.
/// </summary>
internal sealed class ComparadorDaSombra(
    ClientesDaSombra clientes,
    IOptionsMonitor<OpcoesSombra> opcoes,
    IOptionsMonitor<OpcoesLegado> legado,
    IOptionsMonitor<OpcoesNovo> novo,
    ILogger<ComparadorDaSombra> logger)
{
    private readonly LimiteDeReexecucao _limite = new(() => opcoes.CurrentValue.ReexecucoesPorSegundo);

    public async Task ProcessarAsync(CopiaDaSombra copia, CancellationToken ct)
    {
        string operacao = copia.Operacao;

        using Activity? span = TelemetriaDaSombra.IniciarSpan(copia);
        string traceId = TelemetriaDaSombra.TraceIdDaOrigem(copia);

        try
        {
            Uri? urlDoNovo = novo.CurrentValue.Url;
            if (urlDoNovo is null)
            {
                TelemetriaDaSombra.ContarDescarte(operacao, "novo_nao_configurado");
                return;
            }

            Resultado doNovo = await ChamarAsync(clientes.Novo, urlDoNovo, copia, ct).ConfigureAwait(false);
            if (doNovo.Erro is { } erro)
            {
                Registrar(span, operacao, ClasseDeDivergencia.ErroNovo);
                Log.SombraErroNoNovo(logger, operacao, erro, traceId);
                return;
            }

            bool igual = copia.StatusLegado == doNovo.Status && copia.RespostaLegado.AsSpan().SequenceEqual(doNovo.Corpo);
            TelemetriaDaSombra.ContarComparacao(operacao, igual);
            span?.SetTag("sombra.resultado", igual ? "igual" : "divergente");
            if (igual)
            {
                Log.SombraIgual(logger, operacao, traceId);
                return;
            }

            (ClasseDeDivergencia classe, string? regra) =
                ClassificadorDeDivergencia.SemReexecucao(copia.Requisicao, copia.StatusLegado, copia.RespostaLegado, doNovo.Status, doNovo.Corpo)
                ?? (await ReexecutarAsync(copia, urlDoNovo, ct).ConfigureAwait(false), null);

            Registrar(span, operacao, classe);
            Log.SombraDivergente(
                logger, operacao, ClassificadorDeDivergencia.Nome(classe), regra ?? "-", traceId,
                copia.StatusLegado, doNovo.Status, Sha256(copia.RespostaLegado), Sha256(doNovo.Corpo),
                string.Join(' ', DiferencaXml.Caminhos(copia.RespostaLegado, doNovo.Corpo)),
                Pseudonimizador.Requisicao(copia.Requisicao, opcoes.CurrentValue.ChaveHmac) ?? "omitida");
        }
        finally
        {
            TelemetriaDaSombra.MedirAtraso(operacao, copia.T0);
        }
    }

    /// <summary>
    /// Corrida de dados (ADR-0009, seção 2): uma nota emitida ou cancelada entre as duas leituras. Legado e novo
    /// são lidos de novo, em paralelo; se coincidirem, a divergência era <c>corrida</c>.
    /// </summary>
    private async Task<ClasseDeDivergencia> ReexecutarAsync(CopiaDaSombra copia, Uri urlDoNovo, CancellationToken ct)
    {
        if (!_limite.TentarConsumir(copia.Operacao))
        {
            TelemetriaDaSombra.ContarReexecucao(copia.Operacao, "limitada");
            return ClasseDeDivergencia.NaoClassificada;
        }

        Task<Resultado> doLegado = ChamarAsync(clientes.Legado, legado.CurrentValue.Url!, copia, ct);
        Task<Resultado> doNovo = ChamarAsync(clientes.Novo, urlDoNovo, copia, ct);
        Resultado l = await doLegado.ConfigureAwait(false);
        Resultado n = await doNovo.ConfigureAwait(false);

        string resultado = l.Erro is not null || n.Erro is not null ? "erro"
            : l.Status == n.Status && l.Corpo.AsSpan().SequenceEqual(n.Corpo) ? "coincidiu"
            : "divergiu";
        TelemetriaDaSombra.ContarReexecucao(copia.Operacao, resultado);
        return resultado == "coincidiu" ? ClasseDeDivergencia.Corrida : ClasseDeDivergencia.NaoClassificada;
    }

    /// <summary>O mesmo <c>POST /NfseWS.asmx</c>, com o mesmo <c>SOAPAction</c> e corpo, como o YARP montaria.</summary>
    private async Task<Resultado> ChamarAsync(HttpMessageInvoker cliente, Uri url, CopiaDaSombra copia, CancellationToken ct)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(opcoes.CurrentValue.Timeout);

        using var requisicao = new HttpRequestMessage(HttpMethod.Post, url.ToString().TrimEnd('/') + "/NfseWS.asmx")
        {
            Content = new ByteArrayContent(copia.Requisicao),
        };
        if (copia.ContentType is not null)
        {
            requisicao.Content.Headers.TryAddWithoutValidation("Content-Type", copia.ContentType);
        }

        requisicao.Headers.TryAddWithoutValidation("SOAPAction", copia.SoapAction);

        try
        {
            using HttpResponseMessage resposta = await cliente.SendAsync(requisicao, limite.Token).ConfigureAwait(false);
            byte[] corpo = await LerAsync(resposta.Content, limite.Token).ConfigureAwait(false);
            return new((int)resposta.StatusCode, corpo, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(0, [], "timeout");
        }
        catch (HttpRequestException)
        {
            return new(0, [], "transporte");
        }
        catch (IOException)
        {
            return new(0, [], "transporte");
        }
    }

    /// <summary>Até um byte além do limite: uma resposta maior que a do legado (≤ 1 MiB) já não pode ser igual.</summary>
    internal static async Task<byte[]> LerAsync(HttpContent conteudo, CancellationToken ct)
    {
        Stream corpo = await conteudo.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (corpo.ConfigureAwait(false))
        {
            using var copia = new MemoryStream();
            byte[] bloco = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                int lidos;
                while (copia.Length <= OpcoesSombra.LimiteDaResposta
                    && (lidos = await corpo.ReadAsync(bloco.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    copia.Write(bloco, 0, (int)Math.Min(lidos, OpcoesSombra.LimiteDaResposta + 1 - copia.Length));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bloco);
            }

            return copia.ToArray();
        }
    }

    internal static void Registrar(Activity? span, string operacao, ClasseDeDivergencia classe)
    {
        TelemetriaDaSombra.ContarDivergencia(operacao, classe);
        span?.SetTag("sombra.classe", ClassificadorDeDivergencia.Nome(classe));
    }

    internal static string Sha256(byte[] corpo) => Convert.ToHexStringLower(SHA256.HashData(corpo));

    /// <param name="Erro"><c>timeout</c> ou <c>transporte</c>; <c>null</c> quando houve resposta HTTP.</param>
    private readonly record struct Resultado(int Status, byte[] Corpo, string? Erro);
}
