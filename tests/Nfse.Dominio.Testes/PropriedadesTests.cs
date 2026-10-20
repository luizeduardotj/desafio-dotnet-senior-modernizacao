using CsCheck;
using Nfse.Dominio.Documentos;
using Nfse.Dominio.Notas;

namespace Nfse.Dominio.Testes;

/// <summary>
/// Testes de propriedade (ADR-0011, "Contrato REST"): em vez dos exemplos nos limites de <see cref="PeriodoDeEmissaoTests"/>
/// e <see cref="DocumentoTests"/>, milhares de entradas geradas. Uma falha imprime a semente para reproduzir.
/// </summary>
public class PropriedadesTests
{
    private static readonly Gen<DateOnly> Dia = Gen.DateOnly[new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31)];

    [Fact]
    public void Periodo_e_inclusivo_e_aceito_se_e_so_se_tiver_de_1_a_31_dias()
    {
        Gen.Select(Dia, Gen.Int[-40, 70]).Sample((inicio, deslocamento) =>
        {
            DateOnly fim = inicio.AddDays(deslocamento);
            var periodo = PeriodoDeEmissao.Criar(inicio, fim);

            bool esperado = deslocamento is >= 0 and <= 30;
            Assert.Equal(esperado, periodo.Sucesso);
            if (periodo.Sucesso)
            {
                // inclusivo nas duas pontas: de 01/10 a 01/10 é 1 dia; de 01/10 a 31/10, 31
                Assert.Equal(deslocamento + 1, periodo.Valor.Dias);
                Assert.InRange(periodo.Valor.Dias, 1, PeriodoDeEmissao.MaximoDeDias);
                Assert.Equal((inicio, fim), (periodo.Valor.Inicio, periodo.Valor.Fim));
            }
        });
    }

    [Fact]
    public void Periodo_de_um_mes_inteiro_cabe_em_31_dias()
    {
        Gen.Select(Gen.Int[2000, 2099], Gen.Int[1, 12]).Sample((ano, mes) =>
        {
            var inicio = new DateOnly(ano, mes, 1);
            var periodo = PeriodoDeEmissao.Criar(inicio, inicio.AddMonths(1).AddDays(-1));

            Assert.True(periodo.Sucesso);
            Assert.Equal(DateTime.DaysInMonth(ano, mes), periodo.Valor.Dias);
        });
    }

    /// <summary>CPF válido com 0 a 8 zeros à esquerda (o DV é calculado aqui, independente do domínio).</summary>
    private static readonly Gen<string> CpfComZeros =
        Gen.Select(Gen.Int[0, 8], Gen.Long[0, 999_999_999]).Select((zeros, numero) =>
        {
            string baseCpf = new string('0', zeros) + numero.ToString("D9", System.Globalization.CultureInfo.InvariantCulture)[zeros..];
            return ComDvCpf(baseCpf);
        }).Where(d => d.Distinct().Count() > 1);

    /// <summary>CNPJ válido com 0 a 11 zeros à esquerda.</summary>
    private static readonly Gen<string> CnpjComZeros =
        Gen.Select(Gen.Int[0, 11], Gen.Long[0, 999_999_999_999]).Select((zeros, numero) =>
        {
            string baseCnpj = new string('0', zeros) + numero.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)[zeros..];
            return ComDvCnpj(baseCnpj);
        }).Where(d => d.Distinct().Count() > 1);

    [Fact]
    public void Cpf_valido_preserva_os_zeros_a_esquerda()
    {
        CpfComZeros.Sample(cpf =>
        {
            var documento = Documento.Criar(cpf);

            Assert.True(documento.Sucesso, cpf);
            Assert.Equal(cpf, documento.Valor.Digitos);
            Assert.Equal(TipoDocumento.Cpf, documento.Valor.Tipo);
            Assert.Equal(documento.Valor, Documento.Reconstituir(cpf));
        });
    }

    [Fact]
    public void Cnpj_valido_preserva_os_zeros_a_esquerda()
    {
        CnpjComZeros.Sample(cnpj =>
        {
            var documento = Documento.Criar(cnpj);

            Assert.True(documento.Sucesso, cnpj);
            Assert.Equal(cnpj, documento.Valor.Digitos);
            Assert.Equal(TipoDocumento.Cnpj, documento.Valor.Tipo);
            Assert.Equal(documento.Valor, Documento.Reconstituir(cnpj));
        });
    }

    [Fact]
    public void Cpf_e_cnpj_de_mesmo_valor_numerico_sao_documentos_diferentes()
    {
        // C15 corrigido: o legado compara como número e confunde o CPF X com o CNPJ 000X; aqui são textos distintos.
        CpfComZeros.Sample(cpf =>
        {
            var comoCnpj = Documento.Criar("000" + cpf);
            if (comoCnpj.Sucesso)
            {
                Assert.NotEqual(Documento.Criar(cpf).Valor, comoCnpj.Valor);
            }
        });
    }

    private static string ComDvCpf(string b)
    {
        int Dv(string d, int n)
        {
            int soma = 0;
            for (int i = 0; i < n; i++)
            {
                soma += (d[i] - '0') * (n + 1 - i);
            }

            return soma * 10 % 11 % 10;
        }

        string com1 = b + Dv(b, 9);
        return com1 + Dv(com1, 10);
    }

    private static string ComDvCnpj(string b)
    {
        static int Dv(string d)
        {
            int soma = 0;
            for (int i = 0; i < d.Length; i++)
            {
                soma += (d[^(i + 1)] - '0') * (2 + (i % 8));
            }

            return soma % 11 < 2 ? 0 : 11 - (soma % 11);
        }

        string com1 = b + Dv(b);
        return com1 + Dv(com1);
    }
}
