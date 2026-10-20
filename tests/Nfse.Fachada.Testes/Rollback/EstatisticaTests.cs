using Nfse.Fachada.Rollback;

namespace Nfse.Fachada.Testes.Rollback;

/// <summary>ADR-0014, seção 2: os limites de confiança dos gatilhos.</summary>
public sealed class EstatisticaTests
{
    [Theory]
    // Tabela do plano de migração, §5.3 (z = 1,96): N mínimo para LI ≥ alvo com 0, 1 e 2 divergências.
    [InlineData(381, 0, 0.99)]
    [InlineData(563, 1, 0.99)]
    [InlineData(726, 2, 0.99)]
    [InlineData(3838, 0, 0.999)]
    public void Wilson_confere_com_a_tabela_do_plano(long n, long divergencias, double alvo)
    {
        Assert.True(Estatistica.LimiteInferiorDeWilson(n - divergencias, n, 1.96) >= alvo);
        Assert.True(Estatistica.LimiteInferiorDeWilson(n - 1 - divergencias, n - 1, 1.96) < alvo);
    }

    [Fact]
    public void Limites_de_Wilson_cercam_a_taxa_observada()
    {
        double li = Estatistica.LimiteInferiorDeWilson(5, 100, 3.29);
        double ls = Estatistica.LimiteSuperiorDeWilson(5, 100, 3.29);

        Assert.InRange(0.05, li, ls);
        Assert.Equal(0, Estatistica.LimiteInferiorDeWilson(0, 100, 3.29), 12);
    }

    [Fact]
    public void Posicoes_do_p99_com_1000_amostras_e_z_de_99_9()
    {
        // 990 ∓ 3,29·√9,9 = 979,6 e 1000,4 (limitado a n).
        Assert.Equal(979, Estatistica.PosicaoInferiorDoQuantil(1000, 0.99, 3.29));
        Assert.Equal(1000, Estatistica.PosicaoSuperiorDoQuantil(1000, 0.99, 3.29));
        Assert.Equal(1, Estatistica.PosicaoInferiorDoQuantil(1, 0.99, 3.29));
    }

    [Theory]
    [InlineData(0.0005)]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(4.9)]
    public void Cada_latencia_cai_num_balde_de_5_por_cento_que_a_contem(double segundos)
    {
        int indice = BaldesDeLatencia.Indice(TimeSpan.FromSeconds(segundos));

        Assert.InRange(segundos, BaldesDeLatencia.Piso(indice), BaldesDeLatencia.Teto(indice));
        if (indice > 0)
        {
            Assert.Equal(1.05, BaldesDeLatencia.Teto(indice) / BaldesDeLatencia.Piso(indice), 9);
        }
    }

    [Fact]
    public void Ultimo_balde_nao_tem_teto()
    {
        int indice = BaldesDeLatencia.Indice(TimeSpan.FromMinutes(10));

        Assert.Equal(BaldesDeLatencia.Quantidade - 1, indice);
        Assert.True(double.IsPositiveInfinity(BaldesDeLatencia.Teto(indice)));
    }
}
