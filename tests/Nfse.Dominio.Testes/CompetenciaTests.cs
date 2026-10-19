using Nfse.Dominio.Notas;

namespace Nfse.Dominio.Testes;

public class CompetenciaTests
{
    [Theory]
    [InlineData(2026, 10, "2026-11-11T00:00:00")]
    [InlineData(2026, 12, "2027-01-11T00:00:00")]
    [InlineData(2026, 1, "2026-02-11T00:00:00")]
    public void Fim_do_prazo_e_o_dia_11_do_mes_seguinte_a_meia_noite(int ano, int mes, string esperado)
    {
        var competencia = new Competencia(ano, mes);

        Assert.Equal(DateTime.Parse(esperado, System.Globalization.CultureInfo.InvariantCulture), competencia.FimDoPrazoDeCancelamento);
    }

    [Fact]
    public void Competencia_vem_do_mes_da_data()
    {
        Assert.Equal(new Competencia(2026, 7), Competencia.De(new DateOnly(2026, 7, 31)));
    }

    [Theory]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    [InlineData(0, 1)]
    public void Rejeita_mes_ou_ano_invalido(int ano, int mes)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new Competencia(ano, mes));
    }
}
