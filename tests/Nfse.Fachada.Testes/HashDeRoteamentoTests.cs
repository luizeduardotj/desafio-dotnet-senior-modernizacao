using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Testes;

public class HashDeRoteamentoTests
{
    // Valores fixos: o mesmo prestador cai no mesmo lado em qualquer processo, réplica ou versão da fachada.
    // Conferidos contra uma implementação independente de xxHash64 (seed 0), fora do .NET.
    // Se este teste quebrar, o roteamento do canário mudou de lado para parte dos prestadores (ADR-0008, ADR-0009).
    [Theory]
    [InlineData(OperacoesSoap.ConsultarNfse, "10000001", 15077412841873698445UL, 45)]
    [InlineData(OperacoesSoap.CancelarNfse, "10000025", 290263273731930248UL, 48)]
    [InlineData(OperacoesSoap.ConsultarNfseServicoTomado, "01234567000195", 10781216166910589776UL, 76)]
    public void Hash_e_estavel_entre_processos(string operacao, string chave, ulong hash, int balde)
    {
        Assert.Equal(hash, HashDeRoteamento.Hash(operacao, chave));
        Assert.Equal(balde, HashDeRoteamento.Balde(operacao, chave));
    }

    [Fact]
    public void Mesma_chave_cai_em_baldes_independentes_por_operacao()
    {
        int diferentes = Enumerable.Range(0, 200)
            .Count(i => HashDeRoteamento.Balde(OperacoesSoap.ConsultarNfse, $"{10000000 + i}")
                        != HashDeRoteamento.Balde(OperacoesSoap.CancelarNfse, $"{10000000 + i}"));
        Assert.True(diferentes > 150, $"só {diferentes} de 200 mudaram de balde");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(50)]
    public void Distribuicao_aproxima_o_percentual(int percentual)
    {
        const int Total = 20_000;
        int abaixo = Enumerable.Range(0, Total)
            .Count(i => HashDeRoteamento.Balde(OperacoesSoap.ConsultarNfse, $"{10000000 + i}") < percentual);

        double observado = 100.0 * abaixo / Total;
        Assert.InRange(observado, percentual * 0.85 - 0.3, percentual * 1.15 + 0.3);
    }
}
