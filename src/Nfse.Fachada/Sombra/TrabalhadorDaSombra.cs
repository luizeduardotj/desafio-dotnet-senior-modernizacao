using Microsoft.Extensions.Options;
using Nfse.Fachada.Observabilidade;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Sombra;

/// <summary>
/// Workers da sombra: <see cref="OpcoesSombra.Concorrencia"/> consumidores do canal, cada um com no máximo uma
/// comparação em curso. Cada cópia segue o caminho da operação: leitura repetida no novo e comparada
/// (<see cref="ComparadorDaSombra"/>) ou cancelamento avaliado sem escrita (<see cref="AvaliadorDoCancelamento"/>). No desligamento, o que estiver no canal é abandonado (a sombra não tem efeito persistente).
/// </summary>
internal sealed class TrabalhadorDaSombra(
    CanalDaSombra canal,
    ComparadorDaSombra comparador,
    AvaliadorDoCancelamento cancelamento,
    IOptions<OpcoesSombra> opcoes,
    ILogger<TrabalhadorDaSombra> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, opcoes.Value.Concorrencia).Select(_ => ConsumirAsync(stoppingToken)));

    private async Task ConsumirAsync(CancellationToken ct)
    {
        try
        {
            await foreach (CopiaDaSombra copia in canal.Leitor.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (copia.Invertida && !OperacoesSoap.EhLeitura(copia.Operacao))
                {
                    // Terceira trava da sombra invertida: um cancelamento nunca é repetido (ADR-0009, seção 5).
                    TelemetriaDaSombra.ContarDescarte(copia.Operacao, "invertida_proibida");
                    Log.SombraDescartada(logger, copia.Operacao, "invertida_proibida");
                    continue;
                }

                try
                {
                    await (OperacoesSoap.EhLeitura(copia.Operacao)
                        ? comparador.ProcessarAsync(copia, ct)
                        : cancelamento.ProcessarAsync(copia, ct)).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // um erro inesperado numa cópia não pode parar o worker
                catch (Exception erro) when (erro is not OperationCanceledException || !ct.IsCancellationRequested)
#pragma warning restore CA1031
                {
                    // Só o tipo: a mensagem de uma exceção de XML ou HTTP pode trazer trecho do corpo.
                    Log.SombraFalhaInterna(logger, copia.Operacao, erro.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
