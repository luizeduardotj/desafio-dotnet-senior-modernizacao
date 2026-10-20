using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nfse.Fachada.Flags;
using Nfse.Fachada.Rollback;
using Nfse.Fachada.Roteamento;
using Nfse.Fachada.Testes.Integracao;

namespace Nfse.Fachada.Testes.Rollback;

/// <summary>ADR-0014: gatilhos, mínimo de amostras, desarme e direção única, com relógio injetado.</summary>
public sealed class RollbackAutomaticoTests : IDisposable
{
    private const string Op = OperacoesSoap.ConsultarNfse;
    private static readonly TimeSpan Rapido = TimeSpan.FromMilliseconds(50);

    private readonly RelogioDeTeste _relogio = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _servicos = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly FlagsDeMigracao _flags = PassThroughTests.Flags("novo", 100);

    public RollbackAutomaticoTests()
    {
        foreach (FlagsDaOperacao op in _flags.Operacoes.Values)
        {
            op.RollbackAutomatico = new FlagsDoRollbackAutomatico { Ligado = true, Rearme = "r1" };
        }
    }

    private FlagsDaOperacao Flags => _flags.Da(Op);

    public void Dispose() => _servicos.Dispose();

    private RollbackAutomatico Criar(Action<OpcoesRollbackAutomatico>? ajustar = null)
    {
        var opcoes = new OpcoesRollbackAutomatico();
        ajustar?.Invoke(opcoes);
        return new RollbackAutomatico(
            _relogio, new MonitorFixo<OpcoesRollbackAutomatico>(opcoes), new FlagsFixas(_flags),
            _servicos.GetRequiredService<IMeterFactory>(), NullLogger<RollbackAutomatico>.Instance);
    }

    private static Action<OpcoesRollbackAutomatico> ComReferencia(double? taxaDeFault = null, TimeSpan? p99 = null) =>
        o => o.Referencias[Op] = new ReferenciaDoLegado { TaxaDeFault = taxaDeFault, P99 = p99 };

    private void Novo(RollbackAutomatico rollback, int vezes, bool fault = false, bool fallback = false, TimeSpan? duracao = null)
    {
        for (int i = 0; i < vezes; i++)
        {
            rollback.Observar(Op, Flags, new Observacao(Destino.Novo, fault, fallback, duracao ?? Rapido));
        }
    }

    private void Legado(RollbackAutomatico rollback, int vezes, bool fault = false, TimeSpan? duracao = null)
    {
        for (int i = 0; i < vezes; i++)
        {
            rollback.Observar(Op, Flags, new Observacao(Destino.Legado, fault, Fallback: false, duracao ?? Rapido));
        }
    }

    [Fact]
    public void Nao_dispara_abaixo_do_minimo_de_amostras_e_dispara_no_minimo()
    {
        using RollbackAutomatico rollback = Criar(ComReferencia(taxaDeFault: 0.001));

        Novo(rollback, 99, fault: true);
        Assert.False(rollback.Disparado(Op, Flags));

        Novo(rollback, 1, fault: true);
        Assert.True(rollback.Disparado(Op, Flags));
    }

    [Theory]
    // Referência do M0 em 0,1% de fault, limite 0,2%: LI(1/100) = 0,08%, LI(2/100) = 0,28% (z = 3,29).
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Fault_dispara_no_limite_de_Wilson_contra_a_referencia(int faults, bool dispara)
    {
        using RollbackAutomatico rollback = Criar(ComReferencia(taxaDeFault: 0.001));

        Novo(rollback, 100 - faults);
        Novo(rollback, faults, fault: true);

        Assert.Equal(dispara, rollback.Disparado(Op, Flags));
    }

    [Theory]
    // Legado com 0 fault em 10.000 na mesma janela: LS 0,108%, limite 0,208%.
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Fault_compara_com_o_legado_da_mesma_janela_sem_referencia(int faults, bool dispara)
    {
        using RollbackAutomatico rollback = Criar();
        Legado(rollback, 10_000);

        Novo(rollback, 100 - faults);
        Novo(rollback, faults, fault: true);

        Assert.Equal(dispara, rollback.Disparado(Op, Flags));
    }

    [Fact]
    public void Sem_legado_na_janela_e_sem_referencia_o_fault_nao_e_avaliado()
    {
        using RollbackAutomatico rollback = Criar();

        Novo(rollback, 500, fault: true);

        Assert.False(rollback.Disparado(Op, Flags));
    }

    [Theory]
    // LI(12/100) = 4,90%, LI(13/100) = 5,50%, limite 5%.
    [InlineData(12, false)]
    [InlineData(13, true)]
    public void Fallback_dispara_no_limite(int fallbacks, bool dispara)
    {
        using RollbackAutomatico rollback = Criar();

        Novo(rollback, 100 - fallbacks);
        Novo(rollback, fallbacks, fallback: true);

        Assert.Equal(dispara, rollback.Disparado(Op, Flags));
    }

    [Fact]
    public void P99_so_e_comparado_com_o_minimo_de_amostras()
    {
        using RollbackAutomatico rollback = Criar(ComReferencia(p99: TimeSpan.FromMilliseconds(100)));

        Novo(rollback, 999, duracao: TimeSpan.FromMilliseconds(500));
        Assert.False(rollback.Disparado(Op, Flags));

        Novo(rollback, 1, duracao: TimeSpan.FromMilliseconds(500));
        Assert.True(rollback.Disparado(Op, Flags));
    }

    [Fact]
    public void P99_abaixo_de_1_2_vez_o_do_legado_nao_dispara()
    {
        using RollbackAutomatico rollback = Criar();
        Legado(rollback, 2000, duracao: TimeSpan.FromMilliseconds(100));

        Novo(rollback, 2000, duracao: TimeSpan.FromMilliseconds(110));
        Assert.False(rollback.Disparado(Op, Flags));

        Novo(rollback, 2000, duracao: TimeSpan.FromMilliseconds(300));
        Assert.True(rollback.Disparado(Op, Flags));
    }

    [Fact]
    public void Dual_run_dispara_na_primeira_requisicao()
    {
        using RollbackAutomatico rollback = Criar();

        rollback.Observar(Op, Flags, new Observacao(Destino.Novo, Fault: false, Fallback: false, Rapido, RegraDivergente: true));

        Assert.True(rollback.Disparado(Op, Flags));
        EstadoDoRollback estado = rollback.Estado(_flags).Single(e => e.Operacao == Op);
        Assert.Equal(("regra_divergente", "r1"), (estado.Gatilho, estado.Rearme));
    }

    [Fact]
    public void Desligado_nao_observa_nem_dispara()
    {
        Flags.RollbackAutomatico.Ligado = false;
        using RollbackAutomatico rollback = Criar(ComReferencia(taxaDeFault: 0.001));

        Novo(rollback, 200, fault: true);
        rollback.Observar(Op, Flags, new Observacao(Destino.Novo, Fault: false, Fallback: false, Rapido, RegraDivergente: true));

        Assert.False(rollback.Disparado(Op, Flags));
    }

    [Fact]
    public void So_a_troca_do_Rearme_desarma_e_a_janela_recomeca_vazia()
    {
        using RollbackAutomatico rollback = Criar(ComReferencia(taxaDeFault: 0.001));
        Novo(rollback, 100, fault: true);

        // Desligar o mecanismo não devolve o tráfego ao novo.
        Flags.RollbackAutomatico.Ligado = false;
        Assert.True(rollback.Disparado(Op, Flags));

        Flags.RollbackAutomatico.Ligado = true;
        Flags.RollbackAutomatico.Rearme = "r2";
        Assert.False(rollback.Disparado(Op, Flags));

        // Os 100 faults de antes não contam mais: 99 novos ficam abaixo do mínimo.
        Novo(rollback, 99, fault: true);
        Assert.False(rollback.Disparado(Op, Flags));
        Novo(rollback, 1, fault: true);
        Assert.True(rollback.Disparado(Op, Flags));
        Assert.Equal("r2", rollback.Estado(_flags).Single(e => e.Operacao == Op).Rearme);
    }

    [Fact]
    public void Flags_indisponiveis_mantem_o_disparo()
    {
        using RollbackAutomatico rollback = Criar(ComReferencia(taxaDeFault: 0.001));
        Novo(rollback, 100, fault: true);

        Assert.True(rollback.Disparado(Op, flags: null));
    }

    [Fact]
    public void Faults_que_sairam_da_janela_nao_contam()
    {
        using RollbackAutomatico rollback = Criar(ComReferencia(taxaDeFault: 0.001));
        Novo(rollback, 99, fault: true);

        _relogio.Avancar(TimeSpan.FromMinutes(15));
        Novo(rollback, 1, fault: true);

        Assert.False(rollback.Disparado(Op, Flags));
    }

    [Fact]
    public void Disparo_de_uma_operacao_nao_afeta_as_outras()
    {
        using RollbackAutomatico rollback = Criar();

        rollback.Observar(Op, Flags, new Observacao(Destino.Novo, Fault: false, Fallback: false, Rapido, RegraDivergente: true));

        Assert.False(rollback.Disparado(OperacoesSoap.CancelarNfse, _flags.Da(OperacoesSoap.CancelarNfse)));
    }

    public static TheoryData<Destino, MotivoDeRoteamento, bool, bool> TodasAsDecisoes()
    {
        var dados = new TheoryData<Destino, MotivoDeRoteamento, bool, bool>();
        foreach (Destino destino in Enum.GetValues<Destino>())
        {
            foreach (MotivoDeRoteamento motivo in Enum.GetValues<MotivoDeRoteamento>())
            {
                foreach (bool elegivel in new[] { false, true })
                {
                    foreach (bool disparado in new[] { false, true })
                    {
                        dados.Add(destino, motivo, elegivel, disparado);
                    }
                }
            }
        }

        return dados;
    }

    [Theory]
    [MemberData(nameof(TodasAsDecisoes))]
    public void So_move_para_o_legado(Destino destino, MotivoDeRoteamento motivo, bool elegivel, bool disparado)
    {
        var entrada = new Decisao(destino, motivo, elegivel);

        Decisao saida = RollbackAutomatico.Aplicar(entrada, disparado);

        if (disparado && destino == Destino.Novo)
        {
            Assert.Equal(new Decisao(Destino.Legado, MotivoDeRoteamento.RollbackAutomatico), saida);
        }
        else
        {
            // Inclui a sombra (legado, elegível): continua copiando.
            Assert.Equal(entrada, saida);
        }
    }

    [Fact]
    public void Disparo_conta_na_metrica_e_aparece_no_gauge()
    {
        var fabrica = _servicos.GetRequiredService<IMeterFactory>();
        var disparos = new List<(long Valor, string Gatilho)>();
        var ativos = new Dictionary<string, long>();
        using var ouvinte = new MeterListener();
        ouvinte.InstrumentPublished = (instrumento, l) =>
        {
            if (instrumento.Meter.Scope == fabrica && instrumento.Name.StartsWith("rollback_automatico_", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrumento);
            }
        };
        ouvinte.SetMeasurementEventCallback<long>((instrumento, valor, tags, _) =>
        {
            Dictionary<string, object?> t = tags.ToArray().ToDictionary(x => x.Key, x => x.Value);
            if (instrumento.Name == "rollback_automatico_disparos_total")
            {
                disparos.Add((valor, (string)t["gatilho"]!));
            }
            else
            {
                ativos[(string)t["operacao"]!] = valor;
            }
        });
        ouvinte.Start();
        using RollbackAutomatico rollback = Criar();

        rollback.Observar(Op, Flags, new Observacao(Destino.Novo, Fault: false, Fallback: false, Rapido, RegraDivergente: true));
        ouvinte.RecordObservableInstruments();

        Assert.Equal((1L, "regra_divergente"), Assert.Single(disparos));
        Assert.Equal(1, ativos[Op]);
        Assert.Equal(0, ativos[OperacoesSoap.CancelarNfse]);
    }
}

internal sealed class RelogioDeTeste(DateTimeOffset inicio) : TimeProvider
{
    private DateTimeOffset _agora = inicio;

    public override DateTimeOffset GetUtcNow() => _agora;

    public void Avancar(TimeSpan quanto) => _agora += quanto;
}

internal sealed class MonitorFixo<T>(T valor) : IOptionsMonitor<T>
{
    public T CurrentValue => valor;

    public T Get(string? name) => valor;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
