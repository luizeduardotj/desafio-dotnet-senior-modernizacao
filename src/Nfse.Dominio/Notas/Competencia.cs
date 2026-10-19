namespace Nfse.Dominio.Notas;

/// <summary>Mês de referência da NFS-e (ano e mês). No legado é gravada como o dia 1 do mês.</summary>
public readonly record struct Competencia
{
    public Competencia(int ano, int mes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ano, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(mes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mes, 12);
        Ano = ano;
        Mes = mes;
    }

    public int Ano { get; }

    public int Mes { get; }

    public DateOnly PrimeiroDia => new(Ano, Mes, 1);

    public static Competencia De(DateOnly data) => new(data.Year, data.Month);

    /// <summary>
    /// Primeiro instante em que o cancelamento já não é permitido: dia 11 do mês seguinte, 00:00
    /// (equivalente a <c>ADD_MONTHS(COMPETENCIA, 1) + 10</c> do PL/SQL com competência no dia 1, K03).
    /// </summary>
    public DateTime FimDoPrazoDeCancelamento =>
        PrimeiroDia.AddMonths(1).AddDays(10).ToDateTime(TimeOnly.MinValue);

    public override string ToString() => $"{Ano:D4}-{Mes:D2}";
}
