using Nfse.Dominio.Comum;
using Nfse.Dominio.Notas;

namespace Nfse.Dominio.Testes;

public class PeriodoDeEmissaoTests
{
    [Fact]
    public void Aceita_31_dias_inclusivos()
    {
        var resultado = PeriodoDeEmissao.Criar(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        Assert.True(resultado.Sucesso);
        Assert.Equal(31, resultado.Valor.Dias);
    }

    [Fact]
    public void Rejeita_32_dias()
    {
        // o legado aceita 32 dias de calendário (C03); o domínio segue o manual
        var resultado = PeriodoDeEmissao.Criar(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1));

        Assert.IsType<PeriodoInvalido>(resultado.Erro);
    }

    [Fact]
    public void Aceita_um_unico_dia()
    {
        // no legado, inicial = final sempre devolve E040 (C02)
        var resultado = PeriodoDeEmissao.Criar(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 9));

        Assert.True(resultado.Sucesso);
        Assert.Equal(1, resultado.Valor.Dias);
    }

    [Fact]
    public void Rejeita_inicio_depois_do_fim()
    {
        var resultado = PeriodoDeEmissao.Criar(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 9));

        Assert.IsType<PeriodoInvalido>(resultado.Erro);
    }
}
