using Nfse.Dominio.Comum;

namespace Nfse.Dominio.Notas;

/// <summary>Período de consulta por data de emissão, inclusivo nas duas pontas, com no máximo 31 dias (manual).</summary>
public sealed record PeriodoDeEmissao
{
    public const int MaximoDeDias = 31;

    private PeriodoDeEmissao(DateOnly inicio, DateOnly fim)
    {
        Inicio = inicio;
        Fim = fim;
    }

    public DateOnly Inicio { get; }

    public DateOnly Fim { get; }

    public int Dias => Fim.DayNumber - Inicio.DayNumber + 1;

    public static Resultado<PeriodoDeEmissao> Criar(DateOnly inicio, DateOnly fim)
    {
        if (inicio > fim)
            return new PeriodoInvalido("a data inicial é posterior à final");
        if (fim.DayNumber - inicio.DayNumber + 1 > MaximoDeDias)
            return new PeriodoInvalido($"o período excede {MaximoDeDias} dias");

        return new PeriodoDeEmissao(inicio, fim);
    }
}
