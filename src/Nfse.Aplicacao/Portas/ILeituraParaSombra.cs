using Nfse.Aplicacao.Cancelamento;

namespace Nfse.Aplicacao.Portas;

/// <summary>
/// Leitura do estado atual da nota e da guia para a sombra do cancelamento, sempre pelo usuário
/// <c>NFSE_MODERNO_RO</c>: a garantia de que a sombra não escreve vem do banco (ADR-0009).
/// </summary>
public interface ILeituraParaSombra
{
    Task<EstadoAtualDaNota> LerAsync(long prestadorId, long numero, CancellationToken cancellationToken);
}
