namespace Nfse.Dominio.Cancelamento;

/// <summary>
/// Regra de cancelamento como função pura. Espelha <c>PKG_NFSE.cancelar_nfse</c>, que continua sendo a autoridade
/// (ADR-0003); esta função serve à sombra e ao dual-run. A ordem das checagens é a do PL/SQL (K02):
/// inexistente → já cancelada → substituída → prazo → guia paga.
/// </summary>
public static class PoliticaDeCancelamento
{
    /// <param name="estado">Estado da nota, ou <see langword="null"/> se ela não existe para o prestador.</param>
    /// <param name="agora">Instante no relógio do banco (<c>SYSDATE</c>); nunca o relógio do processo.</param>
    public static DecisaoDeCancelamento Avaliar(EstadoParaCancelamento? estado, DateTime agora)
    {
        if (estado is null)
            return DecisaoDeCancelamento.NotaNaoEncontrada;

        // K01: já cancelada é sucesso, inclusive fora do prazo, porque é avaliada antes dele.
        if (estado.Situacao == Notas.SituacaoNota.Cancelada)
            return DecisaoDeCancelamento.JaCancelada;

        if (estado.Situacao == Notas.SituacaoNota.Substituida)
            return DecisaoDeCancelamento.NotaSubstituida;

        // K03: NOT (SYSDATE < ADD_MONTHS(COMPETENCIA, 1) + 10).
        if (agora >= estado.Competencia.FimDoPrazoDeCancelamento)
            return DecisaoDeCancelamento.PrazoExpirado;

        // K02: a guia paga só é avaliada dentro do prazo.
        if (estado.GuiaDaCompetenciaPaga)
            return DecisaoDeCancelamento.GuiaPaga;

        return DecisaoDeCancelamento.Permitido;
    }
}
