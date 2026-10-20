namespace Nfse.Fachada.Rollback;

/// <summary>
/// Janela deslizante de uma operação e de um lado (novo ou legado), em baldes de tempo de duração fixa. O instante é
/// sempre passado por quem chama (relógio injetado): a janela não lê relógio nenhum. Não é thread-safe; quem chama
/// segura o lock da operação.
/// </summary>
internal sealed class JanelaDeObservacao
{
    private readonly long _ticksPorBalde;
    private readonly Balde[] _baldes;

    public JanelaDeObservacao(TimeSpan janela, int baldes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baldes);
        _ticksPorBalde = Math.Max(1, janela.Ticks / baldes);
        _baldes = new Balde[baldes];
        for (int i = 0; i < baldes; i++)
        {
            _baldes[i] = new Balde();
        }
    }

    public void Registrar(DateTimeOffset agora, bool fault, bool fallback, TimeSpan duracao)
    {
        long indice = agora.UtcTicks / _ticksPorBalde;
        Balde balde = _baldes[indice % _baldes.Length];
        if (balde.Indice != indice)
        {
            balde.Zerar(indice);
        }

        balde.Total++;
        balde.Faults += fault ? 1 : 0;
        balde.Fallbacks += fallback ? 1 : 0;
        balde.Latencia[BaldesDeLatencia.Indice(duracao)]++;
    }

    /// <summary>Soma dos baldes ainda dentro da janela em <paramref name="agora"/>.</summary>
    public Totais Somar(DateTimeOffset agora)
    {
        long atual = agora.UtcTicks / _ticksPorBalde;
        long total = 0, faults = 0, fallbacks = 0;
        long[] latencia = new long[BaldesDeLatencia.Quantidade];
        foreach (Balde balde in _baldes)
        {
            if (balde.Indice > atual - _baldes.Length && balde.Indice <= atual)
            {
                total += balde.Total;
                faults += balde.Faults;
                fallbacks += balde.Fallbacks;
                for (int i = 0; i < latencia.Length; i++)
                {
                    latencia[i] += balde.Latencia[i];
                }
            }
        }

        return new Totais(total, faults, fallbacks, latencia);
    }

    public void Zerar()
    {
        foreach (Balde balde in _baldes)
        {
            balde.Zerar(long.MinValue);
        }
    }

    private sealed class Balde
    {
        public long Indice { get; private set; } = long.MinValue;

        public long Total { get; set; }

        public long Faults { get; set; }

        public long Fallbacks { get; set; }

        public long[] Latencia { get; } = new long[BaldesDeLatencia.Quantidade];

        public void Zerar(long indice)
        {
            Indice = indice;
            Total = 0;
            Faults = 0;
            Fallbacks = 0;
            Array.Clear(Latencia);
        }
    }
}

/// <param name="N">Requisições na janela.</param>
/// <param name="Faults">Respostas não 2xx entregues ao cliente por este lado.</param>
/// <param name="Fallbacks">Tentativas no novo que foram ao legado (só no lado novo).</param>
/// <param name="Latencia">Contagem por balde de <see cref="BaldesDeLatencia"/>.</param>
#pragma warning disable CA1819 // contagens por balde, só leitura interna
internal readonly record struct Totais(long N, long Faults, long Fallbacks, long[] Latencia);
#pragma warning restore CA1819
