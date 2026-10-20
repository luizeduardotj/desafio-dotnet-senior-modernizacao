using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Options;
using Nfse.Fachada.Admin;
using Nfse.Fachada.Configuracao;
using Nfse.Fachada.Flags;
using Nfse.Fachada.Observabilidade;
using Nfse.Fachada.Rollback;
using Nfse.Fachada.Roteamento;
using Nfse.Fachada.Sombra;
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
    PublicadorDaSombra sombra,
    RollbackAutomatico rollback,
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
        FlagsDeMigracao? flags = null;
        FlagsDaOperacao? flagsDaOperacao = null;
        string? fonteDasFlags = null;
        ReadOnlyMemory<byte>? corpo = null;

        if (!OperacoesSoap.EstaEmEscopo(operacao))
        {
            // Fora do escopo: só o header decide; o corpo atravessa em streaming, sem leitura, sem retry.
            decisao = new(Destino.Legado, MotivoDeRoteamento.ForaDoEscopo);
        }
        else
        {
            flags = fonteDeFlags.Atual();
            fonteDasFlags = fonteDeFlags.Nome;
            bool killSwitchAdministrativo = killSwitch.Ativo(operacao);
            flagsDaOperacao = flags?.Da(operacao);
            modo = flagsDaOperacao is null ? "indisponivel" : Telemetria.NomeDoModo(flagsDaOperacao.Modo);

            string? chave = null;
            if (DecisorDeDestino.RequerChave(operacao, flags, killSwitchAdministrativo))
            {
                corpo = await CorpoDaRequisicao
                    .BufferizarAsync(ctx, OpcoesFachada.LimiteDoCorpo, ctx.RequestAborted).ConfigureAwait(false);
                chave = corpo is { } bytes ? ExtratorDeChave.Extrair(operacao, bytes.Span) : null;
            }

            decisao = DecisorDeDestino.Decidir(operacao, chave, flags, killSwitchAdministrativo);
            if (decisao.Destino == Destino.Novo && novo.CurrentValue.Url is null)
            {
                decisao = new(Destino.Legado, MotivoDeRoteamento.NovoNaoConfigurado);
            }

            // ADR-0014: depois do decisor e só na direção do legado.
            decisao = RollbackAutomatico.Aplicar(decisao, rollback.Disparado(operacao, flagsDaOperacao));
        }

        TimeSpan noDestino;
        ResultadoNoNovo? noNovo = null;
        if (decisao.Destino == Destino.Novo && corpo is not null && PublicadorDaSombra.CopiavelInvertida(operacao, flags))
        {
            // Sombra invertida (só leituras): o novo responde, o tee copia, e os workers repetem no legado. Com fallback
            // quem respondeu foi o legado, e não há o que copiar.
            using CapturaDaResposta.Instalada captura = CapturaDaResposta.Instalar(ctx, OpcoesSombra.LimiteDaResposta);
            ResultadoNoNovo resultado = await EncaminharAoNovoAsync(ctx, operacao, nome, decisao).ConfigureAwait(false);
            (decisao, noDestino, noNovo) = (resultado.Decisao, resultado.NoDestino, resultado);
            if (decisao.Destino == Destino.Novo)
            {
                sombra.AvaliarDepoisDoNovo(ctx, operacao, flags!.Da(operacao).Sombra.Amostragem, corpo, captura.Captura, inicio);
            }
        }
        else if (decisao.Destino == Destino.Novo)
        {
            ResultadoNoNovo resultado = await EncaminharAoNovoAsync(ctx, operacao, nome, decisao).ConfigureAwait(false);
            (decisao, noDestino, noNovo) = (resultado.Decisao, resultado.NoDestino, resultado);
        }
        else if (sombra.Copiavel(operacao, decisao))
        {
            // Sombra (ADR-0009): o cliente recebe o legado como sempre; o corpo é copiado no caminho (tee) e só depois
            // da resposta se sorteia a amostragem e se publica a cópia. O novo é chamado pelos workers. No cancelamento o
            // legado é chamado só esta vez, sem retry, e o novo só avalia a decisão, sem escrever (seção 3).
            using (CapturaDaResposta.Instalada captura = corpo is null ? default : CapturaDaResposta.Instalar(ctx, OpcoesSombra.LimiteDaResposta))
            {
                noDestino = await EncaminharAoLegadoAsync(ctx, nome).ConfigureAwait(false);
                sombra.AvaliarDepoisDoLegado(ctx, operacao, flags!.Da(operacao).Sombra.Amostragem, corpo, captura.Captura, inicio);
            }
        }
        else
        {
            noDestino = await EncaminharAoLegadoAsync(ctx, nome).ConfigureAwait(false);
        }

        if (flagsDaOperacao is not null && !ctx.RequestAborted.IsCancellationRequested)
        {
            rollback.Observar(operacao, flagsDaOperacao, Observar(decisao, ctx.Response.StatusCode, noDestino, noNovo));
        }

        TimeSpan total = Stopwatch.GetElapsedTime(inicio);
        Telemetria.MarcarSpan(nome, modo, decisao, fonteDasFlags);
        Telemetria.ContarRequisicao(nome, modo, decisao.Destino, ctx.Response.StatusCode);
        Telemetria.MedirOverhead(nome, total - noDestino);
        Log.Roteada(logger, nome, decisao.Destino, decisao.Motivo, ctx.Response.StatusCode, (long)total.TotalMilliseconds);
    }

    /// <summary>O que o rollback automático vê da requisição: o mesmo status e a mesma duração das métricas.</summary>
    private static Observacao Observar(Decisao decisao, int status, TimeSpan noDestino, ResultadoNoNovo? noNovo)
    {
        bool fault = Telemetria.Resultado(status) == "fault";
        return noNovo switch
        {
            { Decisao.Motivo: MotivoDeRoteamento.Fallback } n => new(Destino.Novo, Fault: false, Fallback: true, n.NoNovo),
            { } n => new(Destino.Novo, fault, Fallback: false, n.NoNovo, n.RegraDivergente),
            null => new(Destino.Legado, fault, Fallback: false, noDestino),
        };
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

    /// <param name="Decisao">A decisão final (fallback muda o destino).</param>
    /// <param name="NoDestino">Tempo gasto nos destinos, somando o eventual legado.</param>
    /// <param name="NoNovo">Tempo só na chamada ao novo.</param>
    /// <param name="RegraDivergente">O novo marcou o dual-run como divergente (ADR-0014).</param>
    private readonly record struct ResultadoNoNovo(Decisao Decisao, TimeSpan NoDestino, TimeSpan NoNovo, bool RegraDivergente);

    private async Task<ResultadoNoNovo> EncaminharAoNovoAsync(HttpContext ctx, string operacao, string nome, Decisao decisao)
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
            return new(decisao, noNovo, noNovo, transformador.RegraDivergente);
        }

        if (ctx.RequestAborted.IsCancellationRequested)
        {
            return new(decisao, noNovo, noNovo, transformador.RegraDivergente);
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
            return new(new(Destino.Legado, MotivoDeRoteamento.Fallback), noNovo + noLegado, noNovo, RegraDivergente: false);
        }

        Log.FalhaSemFallback(logger, nome, descricao);
        if (!ctx.Response.HasStarted && erro != ForwarderError.None)
        {
            // Mesmo Fault do legado em erro interno; o ERP pode reenviar porque o cancelamento é idempotente (K01).
            await FaultSoap.EscreverAsync(ctx, "soap:Server", "Erro interno: falha ao processar a requisição").ConfigureAwait(false);
        }

        return new(decisao, noNovo, noNovo, transformador.RegraDivergente);
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
