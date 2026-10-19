namespace Nfse.Adaptadores.Rest;

/// <summary>Representação REST de uma NFS-e (ADR-0010): documentos como texto, instantes com offset, situação explícita.</summary>
public sealed record NfseDto(
    long Numero,
    string? CodigoVerificacao,
    DateTimeOffset DataEmissao,
    string? Competencia,
    string Situacao,
    ServicoDto Servico,
    PrestadorDto Prestador,
    TomadorDto Tomador,
    CancelamentoDto? Cancelamento,
    SubstituicaoDto? SubstituidaPor);

public sealed record ServicoDto(
    string? Item,
    string? Discriminacao,
    decimal? ValorServicos,
    decimal? ValorDeducoes,
    decimal? BaseCalculo,
    decimal? Aliquota,
    decimal? ValorIss,
    bool IssRetido);

public sealed record DocumentoDto(string Numero, string Tipo);

public sealed record PrestadorDto(string InscricaoMunicipal, string? Documento, string? RazaoSocial);

public sealed record TomadorDto(DocumentoDto? Documento, string? RazaoSocial);

public sealed record CancelamentoDto(DateTimeOffset DataHora);

public sealed record SubstituicaoDto(long Numero);

public sealed record PaginaDto(IReadOnlyList<NfseDto> Itens, string? ProximoCursor);

/// <summary>Corpo do pedido de cancelamento.</summary>
public sealed record PedidoDeCancelamentoDto(string? Motivo);

/// <summary>Cancelamento efetuado, ou já existente (o pedido é idempotente, K01).</summary>
public sealed record CancelamentoEfetuadoDto(long Numero, DateTimeOffset DataHora, bool JaEstavaCancelada);
