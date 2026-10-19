using Microsoft.Extensions.Options;
using Nfse.Fachada.Observabilidade;

namespace Nfse.Fachada.Sombra;

/// <summary>
/// Workers da sombra: <see cref="OpcoesSombra.Concorrencia"/> consumidores do canal, cada um com no máximo uma
/// comparação em curso. No desligamento, o que estiver no canal é abandonado (a sombra não tem efeito persistente).
/// </summary>
internal sealed class TrabalhadorDaSombra(
    CanalDaSombra canal,
    ComparadorDaSombra comparador,
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
                try
                {
                    await comparador.ProcessarAsync(copia, ct).ConfigureAwait(false);
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
