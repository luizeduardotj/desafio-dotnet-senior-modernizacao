using Nfse.Dominio.Comum;

namespace Nfse.Aplicacao.Consultas;

public sealed record PrestadorNaoEncontrado() : ErroDeDominio("Prestador não encontrado ou inativo.");

public sealed record LimiteInvalido(int Maximo) : ErroDeDominio($"O limite deve estar entre 1 e {Maximo}.");
