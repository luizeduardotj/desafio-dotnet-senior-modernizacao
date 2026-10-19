namespace Nfse.Dominio.Cancelamento;

public enum DecisaoDeCancelamento
{
    /// <summary>A nota pode ser cancelada agora.</summary>
    Permitido,

    /// <summary>A nota já estava cancelada: o pedido é atendido sem nova escrita (idempotente, K01).</summary>
    JaCancelada,

    NotaNaoEncontrada,
    NotaSubstituida,
    PrazoExpirado,
    GuiaPaga,
}
