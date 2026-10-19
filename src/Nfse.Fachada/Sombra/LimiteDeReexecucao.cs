using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nfse.Fachada.Sombra;

/// <summary>
/// Balde de fichas por operação para a reexecução de divergências (ADR-0009, padrão 1/s): em <c>ServicoTomado</c>
/// cada reexecução do legado é um full scan. Capacidade de uma ficha por unidade da taxa (no mínimo 1).
/// </summary>
internal sealed class LimiteDeReexecucao(Func<double> porSegundo)
{
    private readonly ConcurrentDictionary<string, Balde> _baldes = new(StringComparer.Ordinal);

    public bool TentarConsumir(string operacao)
    {
        double taxa = porSegundo();
        return taxa > 0 && _baldes.GetOrAdd(operacao, _ => new Balde(taxa)).TentarConsumir(taxa);
    }

    private sealed class Balde(double fichasIniciais)
    {
        private readonly Lock _trava = new();
        private double _fichas = Math.Max(1, fichasIniciais);
        private long _ultimo = Stopwatch.GetTimestamp();

        public bool TentarConsumir(double taxa)
        {
            lock (_trava)
            {
                long agora = Stopwatch.GetTimestamp();
                double capacidade = Math.Max(1, taxa);
                _fichas = Math.Min(capacidade, _fichas + (Stopwatch.GetElapsedTime(_ultimo, agora).TotalSeconds * taxa));
                _ultimo = agora;
                if (_fichas < 1)
                {
                    return false;
                }

                _fichas -= 1;
                return true;
            }
        }
    }
}
