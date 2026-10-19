using Nfse.Dominio.Documentos;
using Nfse.Dominio.Notas;

namespace Nfse.Aplicacao.Consultas;

/// <summary>
/// Modelo de leitura de uma NFS-e (lado de consulta do CQRS, ADR-0007): projeção direta das views, sem agregado.
/// </summary>
/// <remarks>
/// Os campos são anuláveis onde a coluna do legado é anulável: a nota é exibida como foi gravada, e cada adaptador
/// decide o que fazer com um valor ausente.
/// </remarks>
public sealed record NotaFiscalLida(
    long Id,
    long Numero,
    string? CodigoVerificacao,
    DateTime DataEmissao,
    DateOnly? Competencia,
    string? ItemServico,
    string? Discriminacao,
    decimal? ValorServicos,
    decimal? ValorDeducoes,
    decimal? BaseCalculo,
    decimal? Aliquota,
    decimal? ValorIss,
    bool IssRetido,
    SituacaoNota Situacao,
    DateTime? DataCancelamento,
    long? NumeroSubstituidora,
    PrestadorLido Prestador,
    Documento? TomadorDocumento,
    string? TomadorRazaoSocial);

/// <summary>Prestador como está no cadastro do legado.</summary>
public sealed record PrestadorLido(long Id, string InscricaoMunicipal, string? Documento, string? RazaoSocial, bool Ativo);
