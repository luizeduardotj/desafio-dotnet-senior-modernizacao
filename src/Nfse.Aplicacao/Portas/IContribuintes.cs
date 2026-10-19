using Nfse.Aplicacao.Consultas;

namespace Nfse.Aplicacao.Portas;

public interface IContribuintes
{
    Task<PrestadorLido?> ObterPorInscricaoAsync(string inscricaoMunicipal, CancellationToken cancellationToken);
}
