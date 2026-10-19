using System.Diagnostics;
using Microsoft.Extensions.Options;
using Nfse.Fachada.Configuracao;
using Nfse.Fachada.Observabilidade;
using Nfse.Fachada.Roteamento;
using Yarp.ReverseProxy.Forwarder;

namespace Nfse.Fachada.Sombra;

/// <summary>
/// Lado da sombra no caminho do cliente: instala a captura da resposta e, depois que o legado respondeu, sorteia a
/// amostragem e publica a cópia no canal. Nada aqui espera o novo nem bloqueia (ADR-0009, seção 1).
/// </summary>
public sealed class PublicadorDaSombra(
    CanalDaSombra canal,
    IOptionsMonitor<OpcoesSombra> opcoes,
    IOptionsMonitor<OpcoesNovo> novo,
    ILogger<PublicadorDaSombra> logger)
{
    /// <summary>
    /// Só leituras: <c>CancelarNfse</c> nunca é copiado nesta etapa, mesmo em <c>sombra</c> e elegível, porque
    /// executá-lo no novo cancelaria duas vezes (a sombra do cancelamento é a avaliação sem escrita da ADR-0009, seção 3).
    /// </summary>
    public bool Copiavel(string operacao, Decisao decisao) =>
        decisao.ElegivelParaSombra && OperacoesSoap.EhLeitura(operacao) && novo.CurrentValue.Url is not null;

    /// <param name="operacao">Operação de leitura, já conferida por <see cref="Copiavel"/>.</param>
    /// <param name="amostragem"><c>Sombra.Amostragem</c> da operação (0..100).</param>
    /// <param name="requisicao">Corpo bufferizado; <c>null</c> se passou do limite.</param>
    /// <param name="captura">Captura da resposta do legado; <c>null</c> se não houve corpo para copiar.</param>
    /// <param name="t0">Recebimento da requisição.</param>
    internal void AvaliarDepoisDoLegado(HttpContext ctx, string operacao, int amostragem, ReadOnlyMemory<byte>? requisicao, CapturaDaResposta? captura, long t0)
    {
        byte[]? resposta = captura?.Bytes;

        // Chave rara (plano, §5.4): taxa 1, fora da rampa. Só dá para saber depois da resposta do legado.
        bool rara = resposta is not null && EmChaveRara(resposta);
        if (!rara && Random.Shared.Next(100) >= amostragem)
        {
            return;
        }

        string? motivo = null;
        if (ctx.Features.Get<IForwarderErrorFeature>() is not null || ctx.RequestAborted.IsCancellationRequested)
        {
            motivo = "falha_legado";
        }
        else if (requisicao is null)
        {
            motivo = "requisicao_grande";
        }
        else if (resposta is null)
        {
            motivo = "resposta_grande";
        }
        else
        {
            var copia = new CopiaDaSombra(
                operacao,
                ctx.Request.Headers["SOAPAction"].ToString(),
                ctx.Request.ContentType,
                requisicao.Value.ToArray(),
                ctx.Response.StatusCode,
                resposta,
                t0,
                Activity.Current?.Context ?? default);
            if (!canal.TentarPublicar(copia))
            {
                motivo = "canal_cheio";
            }
        }

        Activity.Current?.SetTag("sombra.copiada", motivo is null);
        if (motivo is not null)
        {
            TelemetriaDaSombra.ContarDescarte(operacao, motivo);
            Log.SombraDescartada(logger, operacao, motivo);
        }
    }

    private bool EmChaveRara(byte[] resposta)
    {
        IReadOnlyList<string> raras = opcoes.CurrentValue.ChavesRarasEfetivas;
        foreach (string codigo in XmlSoap.CodigosDeRetorno(resposta))
        {
            if (raras.Contains(codigo, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
