using Dapper;
using Nfse.Aplicacao.Consultas;
using Nfse.Aplicacao.Portas;

namespace Nfse.Infra.Oracle;

public sealed class Contribuintes(FabricaDeConexoes conexoes) : IContribuintes
{
    private const string Sql =
        "SELECT ID, INSCRICAO_MUNICIPAL, DOCUMENTO, RAZAO_SOCIAL, ATIVO FROM NFSE_MODERNO.V_CONTRIBUINTE " +
        "WHERE INSCRICAO_MUNICIPAL = :im";

    public async Task<PrestadorLido?> ObterPorInscricaoAsync(string inscricaoMunicipal, CancellationToken cancellationToken)
    {
        await using var conexao = await conexoes.AbrirAsync(cancellationToken).ConfigureAwait(false);
        var linha = await conexao.QuerySingleOrDefaultAsync<LinhaContribuinte>(
            new CommandDefinition(Sql, new { im = inscricaoMunicipal }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return linha is null
            ? null
            : new PrestadorLido((long)linha.ID, linha.INSCRICAO_MUNICIPAL, linha.DOCUMENTO, linha.RAZAO_SOCIAL, linha.ATIVO == "S");
    }

#pragma warning disable CA1812, CA1707 // instanciada pelo Dapper; nomes iguais às colunas
    private sealed class LinhaContribuinte
    {
        public decimal ID { get; set; }
        public string INSCRICAO_MUNICIPAL { get; set; } = "";
        public string? DOCUMENTO { get; set; }
        public string? RAZAO_SOCIAL { get; set; }
        public string? ATIVO { get; set; }
    }
#pragma warning restore CA1812, CA1707
}
