using Nfse.Fachada.Rollback;

namespace Nfse.Fachada.Testes.Rollback;

/// <summary>ADR-0014, seção 2: janela deslizante de 15 min em baldes de 1 min, no instante passado por quem chama.</summary>
public sealed class JanelaDeObservacaoTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Soma_o_que_esta_na_janela_e_esquece_o_balde_que_saiu()
    {
        var janela = new JanelaDeObservacao(TimeSpan.FromMinutes(15), 15);
        janela.Registrar(T0, fault: true, fallback: false, TimeSpan.FromMilliseconds(10));
        janela.Registrar(T0.AddMinutes(14), fault: false, fallback: true, TimeSpan.FromMilliseconds(10));

        Totais aos14 = janela.Somar(T0.AddMinutes(14).AddSeconds(59));
        Totais aos15 = janela.Somar(T0.AddMinutes(15));

        Assert.Equal((2, 1, 1), (aos14.N, aos14.Faults, aos14.Fallbacks));
        Assert.Equal((1, 0, 1), (aos15.N, aos15.Faults, aos15.Fallbacks));
    }

    [Fact]
    public void Balde_reaproveitado_comeca_vazio()
    {
        var janela = new JanelaDeObservacao(TimeSpan.FromMinutes(15), 15);
        janela.Registrar(T0, fault: true, fallback: false, TimeSpan.FromMilliseconds(10));

        // Mesma posição do anel, 15 min depois.
        janela.Registrar(T0.AddMinutes(15), fault: false, fallback: false, TimeSpan.FromMilliseconds(10));

        Totais totais = janela.Somar(T0.AddMinutes(15));
        Assert.Equal((1, 0), (totais.N, totais.Faults));
    }

    [Fact]
    public void Instante_futuro_nao_entra_na_soma_de_agora()
    {
        var janela = new JanelaDeObservacao(TimeSpan.FromMinutes(15), 15);
        janela.Registrar(T0.AddMinutes(2), fault: true, fallback: false, TimeSpan.Zero);

        Assert.Equal(0, janela.Somar(T0).N);
    }

    [Fact]
    public void Zerar_esquece_tudo()
    {
        var janela = new JanelaDeObservacao(TimeSpan.FromMinutes(15), 15);
        janela.Registrar(T0, fault: true, fallback: false, TimeSpan.FromMilliseconds(10));

        janela.Zerar();

        Assert.Equal(0, janela.Somar(T0).N);
    }
}
