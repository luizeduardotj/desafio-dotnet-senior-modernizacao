using Microsoft.Extensions.Options;
using Nfse.Fachada.Observabilidade;

namespace Nfse.Fachada.Sombra.Reproducao;

/// <summary>
/// Retenção curta do payload cifrado (ADR-0009: 15 dias): apaga, a cada <see cref="OpcoesReproducao.IntervaloDaLimpeza"/>,
/// os arquivos mais velhos que <see cref="OpcoesReproducao.Retencao"/>. Cada réplica limpa o diretório que enxerga;
/// apagar o que outra réplica já apagou não é erro. A rotação da chave completa a retenção: sem a chave, o arquivo
/// que escapar da limpeza continua ilegível.
/// </summary>
internal sealed class LimpezaDaReproducao(
    IOptionsMonitor<OpcoesReproducao> opcoes,
    ILogger<LimpezaDaReproducao> logger,
    TimeProvider? relogio = null) : BackgroundService
{
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Limpar();
            try
            {
                await Task.Delay(opcoes.CurrentValue.IntervaloDaLimpeza, _relogio, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <returns>Quantos arquivos foram apagados.</returns>
    internal int Limpar()
    {
        OpcoesReproducao o = opcoes.CurrentValue;
        if (!o.Habilitada || string.IsNullOrWhiteSpace(o.Diretorio) || !Directory.Exists(o.Diretorio))
        {
            return 0;
        }

        DateTimeOffset limite = _relogio.GetUtcNow() - o.Retencao;
        DateTimeOffset limiteDoTemporario = _relogio.GetUtcNow() - TimeSpan.FromHours(1);
        int apagados = 0;
        try
        {
            foreach (string caminho in Directory.EnumerateFiles(o.Diretorio))
            {
                string nome = Path.GetFileName(caminho);
                bool temporario = nome.EndsWith(".tmp", StringComparison.Ordinal);
                if (!temporario && !nome.EndsWith(CofreDeReproducao.Extensao, StringComparison.Ordinal))
                {
                    continue;
                }

                // Temporário órfão (réplica que caiu no meio da gravação) sai depois de uma hora.
                if (CofreDeReproducao.CriadoEm(nome) is { } criadoEm && criadoEm < (temporario ? limiteDoTemporario : limite))
                {
                    try
                    {
                        File.Delete(caminho);
                        apagados++;
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
        catch (Exception erro) when (erro is IOException or UnauthorizedAccessException)
        {
            Log.ReproducaoLimpezaFalhou(logger, erro.GetType().Name);
        }

        if (apagados > 0)
        {
            Log.ReproducaoLimpeza(logger, apagados);
        }

        return apagados;
    }
}
