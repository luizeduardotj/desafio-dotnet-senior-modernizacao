using Nfse.Aplicacao.Consultas;

namespace Nfse.Aplicacao.Portas;

/// <summary>Leitura de NFS-e pelas views do schema <c>NFSE_MODERNO</c> (ADR-0002). Sempre ordenada por emissão e id.</summary>
public interface ILeituraDeNotas
{
    /// <summary>
    /// Faixa por posição: linhas de <paramref name="deslocamento"/> + 1 até <paramref name="limite"/> (base 1).
    /// É a paginação por página do contrato SOAP; quem chama calcula a faixa.
    /// </summary>
    Task<IReadOnlyList<NotaFiscalLida>> ListarEmitidasPorFaixaAsync(
        FiltroDeNotasEmitidas filtro, int deslocamento, int limite, CancellationToken cancellationToken);

    /// <summary>Até <paramref name="limite"/> notas depois de <paramref name="apos"/> (keyset; ADR-0010).</summary>
    Task<IReadOnlyList<NotaFiscalLida>> ListarEmitidasAposAsync(
        FiltroDeNotasEmitidas filtro, PosicaoNaListagem? apos, int limite, CancellationToken cancellationToken);
}
