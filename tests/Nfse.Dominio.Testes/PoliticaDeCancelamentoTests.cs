using Nfse.Dominio.Cancelamento;
using Nfse.Dominio.Notas;

namespace Nfse.Dominio.Testes;

// Tabela de casos espelhando db/03_pkg_nfse.sql (cancelar_nfse). Os mesmos casos rodam contra o PL/SQL
// nos testes de integração (ADR-0003, "Como verificar").
public class PoliticaDeCancelamentoTests
{
    private static readonly Competencia Outubro = new(2026, 10);
    private static readonly DateTime DentroDoPrazo = new(2026, 11, 10, 23, 59, 59);
    private static readonly DateTime ForaDoPrazo = new(2026, 11, 11, 0, 0, 0);

    public static TheoryData<SituacaoNota, bool, DateTime, DecisaoDeCancelamento> Casos => new()
    {
        { SituacaoNota.Normal, false, DentroDoPrazo, DecisaoDeCancelamento.Permitido },
        { SituacaoNota.Normal, false, ForaDoPrazo, DecisaoDeCancelamento.PrazoExpirado },        // K03: dia 11 00:00 já expirou
        { SituacaoNota.Normal, true, DentroDoPrazo, DecisaoDeCancelamento.GuiaPaga },
        { SituacaoNota.Normal, true, ForaDoPrazo, DecisaoDeCancelamento.PrazoExpirado },         // K02: prazo antes de guia paga
        { SituacaoNota.Cancelada, false, ForaDoPrazo, DecisaoDeCancelamento.JaCancelada },       // K01: idempotente fora do prazo
        { SituacaoNota.Cancelada, true, DentroDoPrazo, DecisaoDeCancelamento.JaCancelada },
        { SituacaoNota.Substituida, false, DentroDoPrazo, DecisaoDeCancelamento.NotaSubstituida },
        { SituacaoNota.Substituida, true, ForaDoPrazo, DecisaoDeCancelamento.NotaSubstituida },  // K02: substituída antes do prazo
    };

    [Theory]
    [MemberData(nameof(Casos))]
    public void K02_ordem_das_regras_igual_ao_plsql(SituacaoNota situacao, bool guiaPaga, DateTime agora, DecisaoDeCancelamento esperado)
    {
        var estado = new EstadoParaCancelamento(situacao, Outubro, guiaPaga);

        Assert.Equal(esperado, PoliticaDeCancelamento.Avaliar(estado, agora));
    }

    [Fact]
    public void Nota_inexistente()
    {
        Assert.Equal(DecisaoDeCancelamento.NotaNaoEncontrada, PoliticaDeCancelamento.Avaliar(null, DentroDoPrazo));
    }
}
