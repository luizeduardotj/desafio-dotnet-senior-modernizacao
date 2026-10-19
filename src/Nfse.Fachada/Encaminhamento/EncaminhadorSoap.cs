using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Options;
using Nfse.Fachada.Admin;
using Nfse.Fachada.Configuracao;
using Nfse.Fachada.Flags;
using Nfse.Fachada.Observabilidade;
using Nfse.Fachada.Roteamento;
using Yarp.ReverseProxy.Forwarder;

namespace Nfse.Fachada.Encaminhamento;

/// <summary>Encaminhamento de <c>/NfseWS.asmx</c> e do restante do tráfego ao legado ou ao serviço novo (ADR-0008).</summary>
public sealed class EncaminhadorSoap(
    IHttpForwarder forwarder,
    ClientesDeEncaminhamento clientes,
    IFonteDeFlags fonteDeFlags,
    KillSwitchAdministrativo killSwitch,
    IOptionsMonitor<OpcoesLegado> legado,
    IOptionsMonitor<OpcoesNovo> novo,
    ILogger<EncaminhadorSoap> logger)
{
    /// <summary>POST de <c>/NfseWS.asmx</c>: decide o destino pela operação do <c>SOAPAction</c>.</summary>
    public async Task ProcessarSoapAsync(HttpContext ctx)
    {
        long inicio = Stopwatch.GetTimestamp();
        string operacao = OperacoesSoap.ExtrairOperacao(ctx.Request.Headers["SOAPAction"].ToString());
        string nome = OperacoesSoap.NomeParaTelemetria(operacao);
        string modo = "legado"; // fora do escopo é sempre legado
        Decisao decisao;

        if (!OperacoesSoap.EstaEmEscopo(operacao))
        {
            // Fora do escopo: só o header decide; o corpo atravessa em streaming, sem leitura, sem retry.
            decisao = new(Destino.Legado, MotivoDeRoteamento.ForaDoEscopo);
        }
        else
        {
            FlagsDeMigracao? flags = fonteDeFlags.Atual();
            bool killSwitchAdministrativo = killSwitch.Ativo(operacao);
            modo = flags is null ? "indisponivel" : Telemetria.NomeDoModo(flags.Da(operacao).Modo);

            string? chave = null;
            if (DecisorDeDestino.RequerChave(operacao, flags, killSwitchAdministrativo))
            {
                ReadOnlyMemory<byte>? corpo = await CorpoDaRequisicao
                    .BufferizarAsync(ctx, OpcoesFachada.LimiteDoCorpo, ctx.RequestAborted).ConfigureAwait(false);
                chave = corpo is { } bytes ? ExtratorDeChave.Extrair(operacao, bytes.Span) : null;
            }

            decisao = DecisorDeDestino.Decidir(operacao, chave, flags, killSwitchAdministrativo);
            if (decisao.Destino == Destino.Novo && novo.CurrentValue.Url is null)
            {
                decisao = new(Destino.Legado, MotivoDeRoteamento.NovoNaoConfigurado);
            }

            // TODO(sombra, ADR-0009): com decisao.ElegivelParaSombra, sortear a amostragem depois da resposta do legado
            // e publicar a cópia num canal limitado. Por ora a elegibilidade só aparece no motivo e na métrica.
        }

        TimeSpan noDestino;
        if (decisao.Destino == Destino.Novo)
        {
            (decisao, noDestino) = await EncaminharAoNovoAsync(ctx, operacao, nome, decisao).ConfigureAwait(false);
        }
        else
        {
            noDestino = await EncaminharAoLegadoAsync(ctx, nome).ConfigureAwait(false);
        }

        TimeSpan total = Stopwatch.GetElapsedTime(inicio);
        Telemetria.MarcarSpan(nome, modo, decisao);
        Telemetria.ContarRequisicao(nome, modo, decisao.Destino, ctx.Response.StatusCode);
        Telemetria.MedirOverhead(nome, total - noDestino);
        Log.Roteada(logger, nome, decisao.Destino, decisao.Motivo, ctx.Response.StatusCode, (long)total.TotalMilliseconds);
    }

    /// <summary>Pass-through ao legado: WSDL, <c>/health</c> do legado e qualquer outro caminho.</summary>
    public Task EncaminharAoLegadoAsync(HttpContext ctx) => EncaminharAoLegadoAsync(ctx, "passthrough", medir: false);

    /// <returns>Tempo gasto na chamada ao legado (envio, espera e cópia da resposta ao cliente).</returns>
    private async Task<TimeSpan> EncaminharAoLegadoAsync(HttpContext ctx, string nome, bool medir = true)
    {
        OpcoesLegado opcoes = legado.CurrentValue;
        long inicio = Stopwatch.GetTimestamp();
        ForwarderError erro = await forwarder.SendAsync(
            ctx, opcoes.Url!.ToString(), clientes.Legado,
            new ForwarderRequestConfig { ActivityTimeout = opcoes.Timeout },
            HttpTransformer.Default).ConfigureAwait(false);
        TimeSpan duracao = Stopwatch.GetElapsedTime(inicio);

        if (medir)
        {
            Telemetria.MedirDuracao(nome, Destino.Legado, duracao);
        }

        if (erro != ForwarderError.None)
        {
            Log.FalhaNoLegado(logger, nome, erro.ToString());
        }

        return duracao;
    }

    /// <returns>A decisão final (fallback muda o destino) e o tempo gasto nos destinos, somando o eventual legado.</returns>
    private async Task<(Decisao Decisao, TimeSpan NoDestino)> EncaminharAoNovoAsync(HttpContext ctx, string operacao, string nome, Decisao decisao)
    {
        OpcoesNovo opcoes = novo.CurrentValue;
        bool leitura = OperacoesSoap.EhLeitura(operacao);
        var transformador = new TransformadorDoNovo(descartarIndisponibilidade: leitura);

        long inicio = Stopwatch.GetTimestamp();
        ForwarderError erro = await forwarder.SendAsync(
            ctx, opcoes.Url!.ToString(), clientes.Novo,
            new ForwarderRequestConfig { ActivityTimeout = leitura ? opcoes.TimeoutLeitura : opcoes.TimeoutCancelamento },
            transformador).ConfigureAwait(false);
        TimeSpan noNovo = Stopwatch.GetElapsedTime(inicio);
        Telemetria.MedirDuracao(nome, Destino.Novo, noNovo);

        if (erro == ForwarderError.None && transformador.StatusDescartado is null)
        {
            return (decisao, noNovo);
        }

        if (ctx.RequestAborted.IsCancellationRequested)
        {
            return (decisao, noNovo);
        }

        string descricao = transformador.StatusDescartado is int status ? $"HTTP {status}" : erro.ToString();
        Exception? excecao = ctx.Features.Get<IForwarderErrorFeature>()?.Exception;

        // Leitura: uma tentativa no legado (transporte, timeout, 502/503/504; um 500 é o Fault do novo e já foi ao
        // cliente). Cancelamento: só se a conexão nem chegou a ser aberta; erro numa conexão
        // reaproveitada do pool conta como enviado, porque não dá para saber se o corpo saiu.
        bool podeIrAoLegado = (leitura || ConexaoNaoAberta(excecao)) && !ctx.Response.HasStarted && ctx.Request.Body.CanSeek;
        if (podeIrAoLegado)
        {
            Log.FallbackParaLegado(logger, nome, descricao);
            Telemetria.ContarFallback(nome, CausaDoFallback(transformador.StatusDescartado, erro, excecao));
            ctx.Response.Clear();
            ctx.Features.Set<IForwarderErrorFeature>(null);
            ctx.Request.Body.Position = 0;
            TimeSpan noLegado = await EncaminharAoLegadoAsync(ctx, nome).ConfigureAwait(false);
            return (new(Destino.Legado, MotivoDeRoteamento.Fallback), noNovo + noLegado);
        }

        Log.FalhaSemFallback(logger, nome, descricao);
        if (!ctx.Response.HasStarted && erro != ForwarderError.None)
        {
            // Mesmo Fault do legado em erro interno; o ERP pode reenviar porque o cancelamento é idempotente (K01).
            await FaultSoap.EscreverAsync(ctx, "soap:Server", "Erro interno: falha ao processar a requisição").ConfigureAwait(false);
        }

        return (decisao, noNovo);
    }

    private static string CausaDoFallback(int? statusDescartado, ForwarderError erro, Exception? excecao) =>
        statusDescartado switch
        {
            StatusCodes.Status502BadGateway => "http_502",
            StatusCodes.Status503ServiceUnavailable => "http_503",
            StatusCodes.Status504GatewayTimeout => "http_504",
            _ when erro == ForwarderError.RequestTimedOut => "timeout",
            _ when ConexaoNaoAberta(excecao) => "conexao",
            _ => "transporte",
        };

    private static bool ConexaoNaoAberta(Exception? excecao)
    {
        for (Exception? e = excecao; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError })
            {
                return true;
            }
        }

        return false;
    }
}
