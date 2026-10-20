using System.Data.Common;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nfse.Infra.Oracle;
using Oracle.ManagedDataAccess.Client;

namespace Nfse.Host;

/// <summary>
/// <c>/health/ready</c>: o Oracle responde a um <c>SELECT 1</c> pelo pool do <c>NFSE_MODERNO_APP</c>. Sem o banco o novo
/// não atende nenhuma operação; fora do Service, a fachada leva as leituras ao legado (ADR-0008). O <c>/health/live</c>
/// não olha o banco, para o Kubernetes não reiniciar o processo por causa de uma dependência.
/// </summary>
internal sealed class ProntidaoDoOracle(FabricaDeConexoes conexoes) : IHealthCheck
{
    // Abaixo do timeoutSeconds 2 do readinessProbe (deploy/k8s/base/nfse-novo) e dos 3 s do HEALTHCHECK da imagem.
    // Numa partição de rede o OpenAsync do ODP.NET não volta antes do Connection Timeout (15 s): quem limita é o WaitAsync.
    private static readonly TimeSpan Limite = TimeSpan.FromMilliseconds(1500);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        try
        {
            await ConsultarAsync(cancellationToken).WaitAsync(Limite, cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (TimeoutException)
        {
            return HealthCheckResult.Unhealthy($"Oracle sem resposta em {Limite.TotalMilliseconds} ms");
        }
        catch (OracleException ex)
        {
            // Só o código: a mensagem do ODP.NET pode trazer o endereço do banco.
            return HealthCheckResult.Unhealthy($"Oracle indisponível (ORA-{ex.Number:00000})");
        }
        catch (DbException ex)
        {
            return HealthCheckResult.Unhealthy($"Oracle indisponível ({ex.GetType().Name})");
        }
    }

    private async Task ConsultarAsync(CancellationToken cancellationToken)
    {
        await using DbConnection conexao = await conexoes.AbrirAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand comando = conexao.CreateCommand();
        comando.CommandText = "SELECT 1 FROM DUAL";
        comando.CommandTimeout = 1;
        await comando.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}
