using Nfse.Aplicacao.Cancelamento;

namespace Nfse.Aplicacao.Portas;

/// <summary>
/// Cancelamento pela autoridade de escrita, o PL/SQL do legado via <c>NFSE_MODERNO.PKG_CANCELAMENTO_API</c>
/// (ADR-0003). Uma chamada, uma transação, um commit.
/// </summary>
public interface IGatewayDeCancelamento
{
    Task<ResultadoDoGateway> CancelarAsync(long prestadorId, long numero, string motivo, CancellationToken cancellationToken);
}
