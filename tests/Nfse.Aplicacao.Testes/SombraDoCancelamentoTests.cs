using Nfse.Aplicacao.Cancelamento;
using Nfse.Aplicacao.Consultas;
using Nfse.Aplicacao.Portas;
using Nfse.Dominio.Cancelamento;
using Nfse.Dominio.Notas;

namespace Nfse.Aplicacao.Testes;

// Tabela de reconstrução da ADR-0009 §3, com relógio injetado (sem FIXED_DATE).
public class SombraDoCancelamentoTests
{
    private static readonly DateTime Agora = new(2026, 10, 9, 15, 0, 30);
    private static readonly TimeSpan Idade = TimeSpan.FromMilliseconds(400);
    private static readonly Competencia Outubro = new(2026, 10);
    private static readonly Competencia Julho = new(2026, 7);

    private sealed class Fakes(EstadoAtualDaNota estado, bool prestadorAtivo = true) : IContribuintes, ILeituraParaSombra
    {
        public Task<PrestadorLido?> ObterPorInscricaoAsync(string inscricaoMunicipal, CancellationToken cancellationToken) =>
            Task.FromResult<PrestadorLido?>(inscricaoMunicipal == "10000025" ? new PrestadorLido(25, "10000025", null, null, prestadorAtivo) : null);

        public Task<EstadoAtualDaNota> LerAsync(long prestadorId, long numero, CancellationToken cancellationToken) => Task.FromResult(estado);
    }

    private static Task<AvaliacaoDaSombra> Avaliar(EstadoAtualDaNota estado, RespostaObservada observada, DateTime? dataHora = null,
        string inscricao = "10000025", bool prestadorAtivo = true)
    {
        var fakes = new Fakes(estado, prestadorAtivo);
        return new SombraDoCancelamento(fakes, fakes).AvaliarAsync(inscricao, 1, observada, dataHora, Idade, CancellationToken.None);
    }

    private static EstadoAtualDaNota Estado(SituacaoNota? situacao, Competencia competencia, DateTime? cancelamento = null,
        bool guiaPaga = false, DateTime? guiaAtualizada = null, DateTime? agora = null) =>
        new(agora ?? Agora, situacao, situacao is null ? null : competencia, cancelamento, guiaPaga, guiaAtualizada);

    [Fact]
    public async Task Legado_acabou_de_cancelar_estado_anterior_normal()
    {
        DateTime cancelamento = Agora.AddSeconds(-1);
        AvaliacaoDaSombra a = await Avaliar(Estado(SituacaoNota.Cancelada, Outubro, cancelamento), RespostaObservada.Cancelou, cancelamento);

        Assert.True(a.Igual);
        Assert.Equal(DecisaoDeCancelamento.Permitido, a.DecisaoNovo);
    }

    [Fact]
    public async Task K01_legado_devolveu_cancelamento_antigo_estado_anterior_cancelada()
    {
        var antigo = new DateTime(2026, 7, 24, 12, 33, 0);
        AvaliacaoDaSombra a = await Avaliar(Estado(SituacaoNota.Cancelada, Julho, antigo), RespostaObservada.Cancelou, antigo);

        Assert.True(a.Igual);
        Assert.Equal(DecisaoDeCancelamento.JaCancelada, a.DecisaoLegado);
    }

    [Theory]
    [InlineData(RespostaObservada.PrazoExpirado, SituacaoNota.Normal, false, 7)]
    [InlineData(RespostaObservada.GuiaPaga, SituacaoNota.Normal, true, 10)]
    [InlineData(RespostaObservada.NotaSubstituida, SituacaoNota.Substituida, false, 10)]
    public async Task Negativas_do_legado_avaliadas_sobre_o_estado_atual(RespostaObservada observada, SituacaoNota situacao, bool guiaPaga, int mes)
    {
        AvaliacaoDaSombra a = await Avaliar(Estado(situacao, new Competencia(2026, mes), guiaPaga: guiaPaga), observada);

        Assert.True(a.Igual);
    }

    [Fact]
    public async Task Nota_inexistente()
    {
        AvaliacaoDaSombra a = await Avaliar(Estado(null, Outubro), RespostaObservada.NotaNaoEncontrada);

        Assert.True(a.Igual);
        Assert.Equal(DecisaoDeCancelamento.NotaNaoEncontrada, a.DecisaoNovo);
    }

    [Fact]
    public async Task Bug_do_novo_aparece_como_nao_classificada()
    {
        // o legado cancelou uma nota de julho, que a regra diz estar fora do prazo
        DateTime cancelamento = Agora;
        AvaliacaoDaSombra a = await Avaliar(Estado(SituacaoNota.Cancelada, Julho, cancelamento), RespostaObservada.Cancelou, cancelamento);

        Assert.False(a.Igual);
        Assert.Equal(ClasseDeSombra.NaoClassificada, a.Classe);
    }

    [Fact]
    public async Task Fronteira_do_prazo_e_fronteira_relogio()
    {
        // fim do prazo de setembro: 11/10 00:00. T0 a 1 s dele, e o legado ainda cancelou.
        DateTime agora = new(2026, 10, 11, 0, 0, 0, 600);
        DateTime cancelamento = new(2026, 10, 10, 23, 59, 59);
        AvaliacaoDaSombra a = await Avaliar(Estado(SituacaoNota.Cancelada, new Competencia(2026, 9), cancelamento, agora: agora),
            RespostaObservada.Cancelou, cancelamento);

        Assert.Equal(ClasseDeSombra.FronteiraRelogio, a.Classe);
    }

    [Fact]
    public async Task Guia_paga_depois_da_resposta_do_legado_e_corrida()
    {
        DateTime cancelamento = Agora.AddSeconds(-1);
        AvaliacaoDaSombra a = await Avaliar(
            Estado(SituacaoNota.Cancelada, Outubro, cancelamento, guiaPaga: true, guiaAtualizada: Agora),
            RespostaObservada.Cancelou, cancelamento);

        Assert.Equal(ClasseDeSombra.Corrida, a.Classe);
    }

    [Fact]
    public async Task Fault_do_legado_e_indeterminado()
    {
        AvaliacaoDaSombra a = await Avaliar(Estado(SituacaoNota.Normal, Outubro), RespostaObservada.Fault);

        Assert.Equal(ClasseDeSombra.Indeterminado, a.Classe);
    }

    [Fact]
    public async Task Legado_diz_que_cancelou_mas_a_nota_nao_esta_cancelada_e_indeterminado()
    {
        AvaliacaoDaSombra a = await Avaliar(Estado(SituacaoNota.Normal, Outubro), RespostaObservada.Cancelou, Agora);

        Assert.Equal(ClasseDeSombra.Indeterminado, a.Classe);
    }

    [Theory]
    [InlineData("99999999", true)]
    [InlineData("10000025", false)]
    public async Task Prestador_inexistente_ou_inativo(string inscricao, bool ativo)
    {
        AvaliacaoDaSombra a = await Avaliar(Estado(SituacaoNota.Normal, Outubro), RespostaObservada.PrazoExpirado, inscricao: inscricao, prestadorAtivo: ativo);

        Assert.False(a.PrestadorEncontrado);
    }
}
