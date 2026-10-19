using Nfse.Fachada.Flags;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Testes;

public class DecisorDeDestinoTests
{
    private const string Op = OperacoesSoap.ConsultarNfse;
    private const string Chave = "10000001";

    // Balde de "ConsultarNfse|10000001" (HashDeRoteamentoTests).
    private const int BaldeDaChave = 45;

    [Theory]
    [InlineData(ModoDeMigracao.Legado)]
    [InlineData(ModoDeMigracao.Sombra)]
    [InlineData(ModoDeMigracao.Novo)]
    public void Flags_indisponiveis_vao_ao_legado(ModoDeMigracao _) =>
        Assert.Equal(new Decisao(Destino.Legado, MotivoDeRoteamento.FlagsIndisponiveis), DecisorDeDestino.Decidir(Op, Chave, null, false));

    public static TheoryData<string, bool, bool, bool> KillSwitches => new()
    {
        // nome, administrativo, global nas flags, da operação nas flags
        { "administrativo", true, false, false },
        { "global nas flags", false, true, false },
        { "da operação nas flags", false, false, true },
        { "todos", true, true, true },
    };

    [Theory]
    [MemberData(nameof(KillSwitches))]
    public void Kill_switch_vence_tudo(string caso, bool administrativo, bool global, bool daOperacao)
    {
        foreach (ModoDeMigracao modo in Enum.GetValues<ModoDeMigracao>())
        {
            FlagsDeMigracao flags = Flags(modo, percentual: 100, inclusao: [Chave], killSwitchGlobal: global, killSwitchDaOperacao: daOperacao);
            foreach (string? chave in new[] { Chave, null })
            {
                Decisao decisao = DecisorDeDestino.Decidir(Op, chave, flags, administrativo);
                Assert.True(decisao == new Decisao(Destino.Legado, MotivoDeRoteamento.KillSwitch), $"{caso}, {modo}, chave {chave is not null}: {decisao}");
            }
        }
    }

    [Fact]
    public void Kill_switch_de_outra_operacao_nao_afeta()
    {
        FlagsDeMigracao flags = Flags(ModoDeMigracao.Novo, percentual: 100);
        flags.Operacoes[OperacoesSoap.CancelarNfse] = new FlagsDaOperacao { KillSwitch = true };

        Assert.Equal(Destino.Novo, DecisorDeDestino.Decidir(Op, Chave, flags, false).Destino);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Modo_legado_ignora_listas_e_percentual(bool comChave) =>
        Assert.Equal(new Decisao(Destino.Legado, MotivoDeRoteamento.ModoLegado),
            DecisorDeDestino.Decidir(Op, comChave ? Chave : null, Flags(ModoDeMigracao.Legado, percentual: 100, inclusao: [Chave]), false));

    [Fact]
    public void Operacao_ausente_das_flags_fica_no_legado() =>
        Assert.Equal(new Decisao(Destino.Legado, MotivoDeRoteamento.ModoLegado),
            DecisorDeDestino.Decidir(Op, Chave, new FlagsDeMigracao(), false));

    [Theory]
    [InlineData(ModoDeMigracao.Sombra)]
    [InlineData(ModoDeMigracao.Novo)]
    public void Lista_de_exclusao_vence_inclusao_e_percentual(ModoDeMigracao modo) =>
        Assert.Equal(new Decisao(Destino.Legado, MotivoDeRoteamento.ListaDeExclusao),
            DecisorDeDestino.Decidir(Op, Chave, Flags(modo, percentual: 100, inclusao: [Chave], exclusao: [Chave]), false));

    [Theory]
    [InlineData(new string[0], Chave, true)]
    [InlineData(new[] { Chave }, Chave, true)]
    [InlineData(new[] { "99999999" }, Chave, false)]
    [InlineData(new string[0], null, true)]
    [InlineData(new[] { Chave }, null, false)]
    public void Sombra_responde_pelo_legado_e_marca_elegibilidade_pelas_listas(string[] inclusao, string? chave, bool elegivel)
    {
        // Percentual é ignorado em sombra: o cliente sempre recebe o legado.
        Decisao decisao = DecisorDeDestino.Decidir(Op, chave, Flags(ModoDeMigracao.Sombra, percentual: 100, inclusao: inclusao), false);
        Assert.Equal(new Decisao(Destino.Legado, MotivoDeRoteamento.Sombra, elegivel), decisao);
    }

    [Fact]
    public void Novo_lista_de_inclusao_vai_ao_novo_mesmo_com_percentual_zero() =>
        Assert.Equal(new Decisao(Destino.Novo, MotivoDeRoteamento.ListaDeInclusao),
            DecisorDeDestino.Decidir(Op, Chave, Flags(ModoDeMigracao.Novo, percentual: 0, inclusao: [Chave]), false));

    [Theory]
    [InlineData(0, Destino.Legado, MotivoDeRoteamento.ForaDoPercentual)]
    [InlineData(BaldeDaChave, Destino.Legado, MotivoDeRoteamento.ForaDoPercentual)]
    [InlineData(BaldeDaChave + 1, Destino.Novo, MotivoDeRoteamento.Percentual)]
    [InlineData(99, Destino.Novo, MotivoDeRoteamento.Percentual)]
    [InlineData(100, Destino.Novo, MotivoDeRoteamento.Percentual)]
    public void Novo_percentual_compara_o_balde_do_hash(int percentual, Destino destino, MotivoDeRoteamento motivo) =>
        Assert.Equal(new Decisao(destino, motivo),
            DecisorDeDestino.Decidir(Op, Chave, Flags(ModoDeMigracao.Novo, percentual: percentual, inclusao: ["outro"]), false));

    [Theory]
    [InlineData(0, Destino.Legado, MotivoDeRoteamento.ChaveAusente)]
    [InlineData(50, Destino.Legado, MotivoDeRoteamento.ChaveAusente)]
    [InlineData(99, Destino.Legado, MotivoDeRoteamento.ChaveAusente)]
    [InlineData(100, Destino.Novo, MotivoDeRoteamento.NovoTotalSemChave)]
    public void Novo_sem_chave_so_vai_ao_novo_em_100(int percentual, Destino destino, MotivoDeRoteamento motivo) =>
        Assert.Equal(new Decisao(destino, motivo),
            DecisorDeDestino.Decidir(Op, null, Flags(ModoDeMigracao.Novo, percentual: percentual, inclusao: [Chave]), false));

    [Fact]
    public void Decisao_por_percentual_e_estavel_para_a_mesma_chave()
    {
        FlagsDeMigracao flags = Flags(ModoDeMigracao.Novo, percentual: 30);
        Decisao primeira = DecisorDeDestino.Decidir(Op, "10000777", flags, false);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(primeira, DecisorDeDestino.Decidir(Op, "10000777", flags, false));
        }
    }

    [Fact]
    public void Requer_chave_so_quando_a_decisao_depende_dela()
    {
        Assert.False(DecisorDeDestino.RequerChave(Op, null, false));
        Assert.False(DecisorDeDestino.RequerChave(Op, Flags(ModoDeMigracao.Novo, 100), true));
        Assert.False(DecisorDeDestino.RequerChave(Op, Flags(ModoDeMigracao.Novo, 100, killSwitchGlobal: true), false));
        Assert.False(DecisorDeDestino.RequerChave(Op, Flags(ModoDeMigracao.Novo, 100, killSwitchDaOperacao: true), false));
        Assert.False(DecisorDeDestino.RequerChave(Op, Flags(ModoDeMigracao.Legado, 100), false));
        Assert.True(DecisorDeDestino.RequerChave(Op, Flags(ModoDeMigracao.Sombra, 0), false));
        Assert.True(DecisorDeDestino.RequerChave(Op, Flags(ModoDeMigracao.Novo, 0), false));
    }

    internal static FlagsDeMigracao Flags(
        ModoDeMigracao modo,
        int percentual = 0,
        string[]? inclusao = null,
        string[]? exclusao = null,
        bool killSwitchGlobal = false,
        bool killSwitchDaOperacao = false,
        string operacao = Op) => new()
    {
        KillSwitch = killSwitchGlobal,
        Operacoes =
        {
            [operacao] = new FlagsDaOperacao
            {
                Modo = modo,
                Percentual = percentual,
                Inclusao = inclusao ?? [],
                Exclusao = exclusao ?? [],
                KillSwitch = killSwitchDaOperacao,
            },
        },
    };
}
