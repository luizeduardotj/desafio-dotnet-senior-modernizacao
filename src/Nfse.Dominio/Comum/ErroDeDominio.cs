namespace Nfse.Dominio.Comum;

/// <summary>
/// Erros de regra de negócio. Não carregam códigos do legado (E0xx): a tradução é da camada
/// anticorrupção (ADR-0006) e do adaptador REST (ADR-0010).
/// </summary>
public abstract record ErroDeDominio(string Descricao);

public sealed record DocumentoInvalido(string Motivo) : ErroDeDominio($"Documento inválido: {Motivo}.");

public sealed record PeriodoInvalido(string Motivo) : ErroDeDominio($"Período inválido: {Motivo}.");
