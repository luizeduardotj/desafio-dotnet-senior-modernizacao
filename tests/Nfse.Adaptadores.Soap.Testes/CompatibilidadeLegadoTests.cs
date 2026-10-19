using System.Xml;
using Nfse.Adaptadores.Soap.Legado;

namespace Nfse.Adaptadores.Soap.Testes;

public class CompatibilidadeLegadoTests
{
    private static XmlElement Periodo(string inicio, string fim) =>
        CompatibilidadeLegado.Carregar(
            $"<X><PeriodoEmissao><DataInicial>{inicio}</DataInicial><DataFinal>{fim}</DataFinal></PeriodoEmissao></X>").DocumentElement!;

    [Theory]
    [InlineData("2026-09-01", "2026-10-02", true)]   // C03: 31 dias de diferença (32 de calendário) é aceito
    [InlineData("2026-09-01", "2026-10-03", false)]  // C03: 32 de diferença é E030
    [InlineData("2026-10-31", "2026-10-01", false)]
    [InlineData("10/01/2026", "10/31/2026", true)]   // C04: M/d/yyyy aceito
    [InlineData("01/10/2026", "2026-10-31", false)]  // C04: "01/10" é 10 de janeiro: mais de 31 dias
    [InlineData("abc", "2026-10-31", false)]
    public void C03_C04_leitura_do_periodo(string inicio, string fim, bool valido)
    {
        Assert.Equal(valido, CompatibilidadeLegado.LerPeriodo(Periodo(inicio, fim), out _, out _));
    }

    [Fact]
    public void C04_offset_e_convertido_para_sao_paulo_antes_de_truncar()
    {
        // 23:30 em UTC-05:00 é 01:30 do dia seguinte em São Paulo
        Assert.True(CompatibilidadeLegado.LerPeriodo(Periodo("2026-10-01T23:30:00-05:00", "2026-10-31"), out DateTime inicio, out _));
        Assert.Equal(new DateTime(2026, 10, 2), inicio);
    }

    [Theory]
    [InlineData("<Pagina>3</Pagina>", 3)]
    [InlineData("<Pagina>abc</Pagina>", 1)]
    [InlineData("<Pagina>0</Pagina>", 1)]
    [InlineData("<Pagina>-5</Pagina>", 1)]
    [InlineData("", 1)]
    public void C05_pagina_invalida_vira_1(string elemento, int esperado)
    {
        var raiz = CompatibilidadeLegado.Carregar($"<X>{elemento}</X>").DocumentElement!;

        Assert.Equal(esperado, CompatibilidadeLegado.LerPagina(raiz));
    }

    [Fact]
    public void C27_faixa_com_overflow_de_int_igual_ao_legado()
    {
        // capturado no legado: Pagina=85899346 devolve as 5 primeiras notas (tests/paridade/casos/107)
        (int deslocamento, int limite) = CompatibilidadeLegado.Faixa(85899346);

        Assert.Equal(5, limite);
        Assert.True(deslocamento < 0);
    }

    [Theory]
    [InlineData("01.234.567/0001-95", true)]
    [InlineData("0123456700019A5", true)]   // C12: letra removida
    [InlineData("52998224725", true)]
    [InlineData("11111111111111", false)]
    [InlineData("123", false)]
    public void C12_validacao_remove_nao_digitos(string documento, bool valido)
    {
        Assert.Equal(valido, CompatibilidadeLegado.ValidarCpfOuCnpj(documento));
    }

    [Fact]
    public void C28_dtd_interna_e_recusada()
    {
        Assert.Throws<XmlException>(() =>
            CompatibilidadeLegado.Carregar("<!DOCTYPE X [<!ENTITY a \"1\">]><X>&a;</X>"));
    }
}
