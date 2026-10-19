using System.Data.Common;
using Oracle.ManagedDataAccess.Client;

namespace Nfse.Infra.Oracle;

/// <summary>
/// Conexões do usuário <c>NFSE_MODERNO_APP</c> (ADR-0002). O pool é do ODP.NET; a connection string define o máximo.
/// </summary>
public sealed class FabricaDeConexoes(string connectionString)
{
    public async Task<DbConnection> AbrirAsync(CancellationToken cancellationToken)
    {
        var conexao = new OracleConnection(connectionString);
        try
        {
            await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);
            return conexao;
        }
        catch
        {
            await conexao.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
