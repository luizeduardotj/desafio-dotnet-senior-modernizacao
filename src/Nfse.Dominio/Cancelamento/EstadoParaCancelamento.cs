using Nfse.Dominio.Notas;

namespace Nfse.Dominio.Cancelamento;

/// <summary>
/// O que a regra precisa saber da nota no instante da decisão. Em modo novo vem do wrapper, lido sob lock;
/// na sombra, reconstruído a partir do estado posterior (ADR-0009).
/// </summary>
public sealed record EstadoParaCancelamento(SituacaoNota Situacao, Competencia Competencia, bool GuiaDaCompetenciaPaga);
