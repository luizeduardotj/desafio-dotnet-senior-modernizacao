using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nfse.Fachada.Configuracao;
using Nfse.Fachada.Observabilidade;
using Nfse.Fachada.Sombra.Reproducao;

namespace Nfse.Fachada.Sombra;

/// <summary>
/// Sombra do <c>CancelarNfse</c> (ADR-0009, seção 3): o legado já cancelou e respondeu ao cliente; o novo só avalia a
/// decisão, sem escrever, em <c>POST /interno/sombra/cancelamento</c> da porta interna. A fachada não repete nada,
/// nem no legado nem no <c>/NfseWS.asmx</c> do novo: seria escrita dupla. A classe vem do novo, que conhece a regra.
/// </summary>
internal sealed class AvaliadorDoCancelamento(
    ClientesDaSombra clientes,
    IOptionsMonitor<OpcoesSombra> opcoes,
    IOptionsMonitor<OpcoesNovo> novo,
    CofreDeReproducao cofre,
    ILogger<AvaliadorDoCancelamento> logger)
{
    public const string Caminho = "/interno/sombra/cancelamento";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task ProcessarAsync(CopiaDaSombra copia, CancellationToken ct)
    {
        string operacao = copia.Operacao;
        using Activity? span = TelemetriaDaSombra.IniciarSpan(copia);
        string traceId = TelemetriaDaSombra.TraceIdDaOrigem(copia);

        try
        {
            if (novo.CurrentValue.UrlInterna is not { } url)
            {
                TelemetriaDaSombra.ContarDescarte(operacao, "sem_url_interna");
                Log.SombraDescartada(logger, operacao, "sem_url_interna");
                return;
            }

            (Avaliacao? avaliacao, string? erro) = await AvaliarAsync(url, copia, ct).ConfigureAwait(false);
            if (avaliacao is not { } a)
            {
                ComparadorDaSombra.Registrar(span, operacao, ClasseDeDivergencia.ErroNovo);
                Log.SombraErroNoNovo(logger, operacao, erro!, traceId);
                return;
            }

            TelemetriaDaSombra.ContarConferenciaDeCnpj(operacao, a.CnpjConfere);
            TelemetriaDaSombra.ContarComparacao(operacao, a.Igual);
            span?.SetTag("sombra.resultado", a.Igual ? "igual" : "divergente");
            if (a.Igual)
            {
                Log.SombraIgual(logger, operacao, traceId);
                return;
            }

            ComparadorDaSombra.Registrar(span, operacao, a.Classe);
            string reproducao = await cofre.GuardarAsync(copia, traceId, ct).ConfigureAwait(false);
            Log.SombraCancelamentoDivergente(
                logger, operacao, ClassificadorDeDivergencia.Nome(a.Classe), traceId, a.DecisaoLegado, a.DecisaoNovo,
                copia.Status, ComparadorDaSombra.Sha256(copia.Resposta),
                Pseudonimizador.Requisicao(copia.Requisicao, opcoes.CurrentValue.ChaveHmac) ?? "omitida",
                reproducao);
        }
        finally
        {
            TelemetriaDaSombra.MedirAtraso(operacao, copia.T0);
        }
    }

    /// <returns>A avaliação do novo, ou o erro (<c>timeout</c>, <c>transporte</c>, <c>http_NNN</c>, <c>resposta_invalida</c>).</returns>
    private async Task<(Avaliacao? Avaliacao, string? Erro)> AvaliarAsync(Uri url, CopiaDaSombra copia, CancellationToken ct)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(opcoes.CurrentValue.Timeout);

        // A idade é medida aqui, no relógio monotônico da fachada: o novo soma ao SYSDATE para reconstruir T0 no relógio
        // do banco, sem comparar relógios de máquinas diferentes.
        var pedido = new PedidoDeAvaliacao(
            Encoding.UTF8.GetString(copia.Requisicao),
            Encoding.UTF8.GetString(copia.Resposta),
            copia.Status,
            (long)Stopwatch.GetElapsedTime(copia.T0).TotalMilliseconds);
        using var requisicao = new HttpRequestMessage(HttpMethod.Post, url.ToString().TrimEnd('/') + Caminho)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(pedido, Json)),
        };
        requisicao.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");

        try
        {
            using HttpResponseMessage resposta = await clientes.Novo.SendAsync(requisicao, limite.Token).ConfigureAwait(false);
            byte[] corpo = await ComparadorDaSombra.LerAsync(resposta.Content, limite.Token).ConfigureAwait(false);
            if ((int)resposta.StatusCode != StatusCodes.Status200OK)
            {
                return (null, $"http_{(int)resposta.StatusCode}");
            }

            return Avaliacao.Ler(corpo) is { } avaliacao ? (avaliacao, null) : (null, "resposta_invalida");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "timeout");
        }
        catch (HttpRequestException)
        {
            return (null, "transporte");
        }
        catch (IOException)
        {
            return (null, "transporte");
        }
    }

    /// <summary>Contrato do endpoint interno do novo. <c>IdadeMs</c>: do recebimento da requisição ao envio da cópia.</summary>
    private sealed record PedidoDeAvaliacao(string Envelope, string RespostaLegado, int StatusLegado, long IdadeMs);

    /// <param name="CnpjConfere">O CNPJ do pedido é o do cadastro da IM (C18/K04)? O novo já lê o cadastro; <c>null</c>, nada a conferir.</param>
    private sealed record RespostaDaAvaliacao(string? Resultado, string? Classe, string? DecisaoLegado, string? DecisaoNovo, bool? CnpjConfere);

    /// <param name="DecisaoLegado">Só um código curto (<c>E053</c>, <c>sucesso</c>): o resto vira <c>?</c> no log.</param>
    private readonly record struct Avaliacao(bool Igual, ClasseDeDivergencia Classe, string DecisaoLegado, string DecisaoNovo, bool? CnpjConfere)
    {
        /// <summary>
        /// <c>null</c> se o corpo não segue o contrato. Um resultado ou classe fora da lista não vira label (cardinalidade):
        /// conta como <c>erro_novo</c>.
        /// </summary>
        public static Avaliacao? Ler(byte[] corpo)
        {
            RespostaDaAvaliacao? r;
            try
            {
                r = JsonSerializer.Deserialize<RespostaDaAvaliacao>(corpo, Json);
            }
            catch (JsonException)
            {
                return null;
            }

            bool? igual = r?.Resultado switch
            {
                "igual" => true,
                "divergente" => false,
                _ => null,
            };
            ClasseDeDivergencia? classe = r?.Classe switch
            {
                null or "nao_classificada" => ClasseDeDivergencia.NaoClassificada,
                "corrida" => ClasseDeDivergencia.Corrida,
                "fronteira_relogio" => ClasseDeDivergencia.FronteiraRelogio,
                "indeterminado" => ClasseDeDivergencia.Indeterminado,
                "decisao_consciente" => ClasseDeDivergencia.DecisaoConsciente,
                _ => null,
            };
            if (igual is null || classe is null)
            {
                return null;
            }

            return new(igual.Value, classe.Value, Decisao(r!.DecisaoLegado), Decisao(r.DecisaoNovo), r.CnpjConfere);
        }

        private static string Decisao(string? valor) =>
            valor is { Length: > 0 and <= 32 } && valor.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                ? valor
                : "?";
    }
}
