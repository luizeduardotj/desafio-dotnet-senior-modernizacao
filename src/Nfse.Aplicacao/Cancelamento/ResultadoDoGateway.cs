using Nfse.Dominio.Cancelamento;

namespace Nfse.Aplicacao.Cancelamento;

/// <summary>O que o wrapper devolveu, já traduzido para o domínio pelo adaptador de dados.</summary>
/// <param name="Decisao">Decisão do PL/SQL (autoridade).</param>
/// <param name="EstadoAntes">Snapshot lido sob lock antes da regra; <see langword="null"/> se a nota não existe.</param>
/// <param name="AgoraNoBanco"><c>SYSDATE</c> lido sob lock; <see langword="null"/> se a nota não existe.</param>
/// <param name="DataCancelamento"><c>DT_CANCELAMENTO</c> depois da regra, na mesma transação.</param>
public sealed record ResultadoDoGateway(
    DecisaoDeCancelamento Decisao,
    EstadoParaCancelamento? EstadoAntes,
    DateTime? AgoraNoBanco,
    DateTime? DataCancelamento);

/// <summary>Resposta do caso de uso: a decisão e, quando a nota está cancelada, quando isso aconteceu.</summary>
public sealed record RespostaDoCancelamento(DecisaoDeCancelamento Decisao, DateTime? DataCancelamento)
{
    public bool Cancelada => Decisao is DecisaoDeCancelamento.Permitido or DecisaoDeCancelamento.JaCancelada;
}
