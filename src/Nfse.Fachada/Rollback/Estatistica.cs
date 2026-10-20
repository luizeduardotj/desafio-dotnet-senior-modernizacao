namespace Nfse.Fachada.Rollback;

/// <summary>Limites de confiança usados pelos gatilhos (ADR-0014, seção 2). Funções puras.</summary>
internal static class Estatistica
{
    /// <summary>Limite inferior de Wilson de <paramref name="k"/> em <paramref name="n"/> (plano, §5.3).</summary>
    public static double LimiteInferiorDeWilson(long k, long n, double z) => Wilson(k, n, z, -1);

    public static double LimiteSuperiorDeWilson(long k, long n, double z) => Wilson(k, n, z, +1);

    /// <summary>
    /// Posição (1..n) do limite inferior do quantil <paramref name="q"/>: <c>⌊n·q − z·√(n·q·(1−q))⌋</c>. A posição de
    /// um quantil amostral é binomial; o limite usa a aproximação normal.
    /// </summary>
    public static long PosicaoInferiorDoQuantil(long n, double q, double z) =>
        Math.Clamp((long)Math.Floor((n * q) - (z * Math.Sqrt(n * q * (1 - q)))), 1, n);

    /// <summary>Posição (1..n) do limite superior do quantil: <c>⌈n·q + z·√(n·q·(1−q))⌉</c>.</summary>
    public static long PosicaoSuperiorDoQuantil(long n, double q, double z) =>
        Math.Clamp((long)Math.Ceiling((n * q) + (z * Math.Sqrt(n * q * (1 - q)))), 1, n);

    private static double Wilson(long k, long n, double z, int sinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        double p = (double)k / n;
        double z2 = z * z;
        double centro = p + (z2 / (2.0 * n));
        double margem = z * Math.Sqrt((p * (1 - p) / n) + (z2 / (4.0 * n * n)));
        return (centro + (sinal * margem)) / (1 + (z2 / n));
    }
}

/// <summary>
/// Baldes geométricos de latência, de 5% em 5%, de 1 ms a 120 s. O balde <c>i ≥ 1</c> cobre
/// <c>(1 ms · 1,05^(i−1), 1 ms · 1,05^i]</c>; o 0 cobre até 1 ms e o último não tem teto.
/// </summary>
internal static class BaldesDeLatencia
{
    private const double Minimo = 0.001;
    private const double Razao = 1.05;
    private static readonly double LogRazao = Math.Log(Razao);

    public static readonly int Quantidade = (int)Math.Ceiling(Math.Log(120 / Minimo) / LogRazao) + 1;

    public static int Indice(TimeSpan duracao)
    {
        double segundos = duracao.TotalSeconds;
        if (segundos <= Minimo)
        {
            return 0;
        }

        return Math.Min(Quantidade - 1, (int)Math.Ceiling(Math.Log(segundos / Minimo) / LogRazao));
    }

    /// <summary>Piso do balde, em segundos.</summary>
    public static double Piso(int indice) => indice == 0 ? 0 : Minimo * Math.Pow(Razao, indice - 1);

    /// <summary>Teto do balde, em segundos; infinito no último.</summary>
    public static double Teto(int indice) => indice >= Quantidade - 1 ? double.PositiveInfinity : Minimo * Math.Pow(Razao, indice);

    /// <summary>Índice do balde que contém a observação na posição <paramref name="posicao"/> (1..n), em ordem crescente.</summary>
    public static int BaldeDaPosicao(long[] contagens, long posicao)
    {
        long acumulado = 0;
        for (int i = 0; i < contagens.Length; i++)
        {
            acumulado += contagens[i];
            if (acumulado >= posicao)
            {
                return i;
            }
        }

        return contagens.Length - 1;
    }
}
