using Nfse.Dominio.Notas;

namespace Nfse.Aplicacao.Consultas;

/// <summary>
/// Filtro da listagem de notas emitidas por um prestador. As datas são instantes no relógio do banco:
/// <see cref="InicioInclusivo"/> ≤ emissão &lt; <see cref="FimExclusivo"/>.
/// </summary>
/// <param name="TomadorDigitos">Documento do tomador, só dígitos, comparado como texto.</param>
/// <param name="CompetenciaAte">Limite superior opcional da competência (o SOAP usa para reproduzir C13).</param>
public sealed record FiltroDeNotasEmitidas(
    long PrestadorId,
    DateTime InicioInclusivo,
    DateTime FimExclusivo,
    string? TomadorDigitos = null,
    DateTime? CompetenciaAte = null,
    SituacaoNota? Situacao = null);

/// <summary>Posição de uma nota na ordenação <c>DT_EMISSAO, ID</c> (paginação por keyset).</summary>
public readonly record struct PosicaoNaListagem(DateTime DataEmissao, long Id);

public sealed record PaginaDeNotas(IReadOnlyList<NotaFiscalLida> Itens, PosicaoNaListagem? Proxima);
