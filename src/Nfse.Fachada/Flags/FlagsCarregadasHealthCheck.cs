using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Nfse.Fachada.Flags;

/// <summary>Prontidão: a réplica só recebe tráfego depois de ler as flags (sem elas, roteia tudo ao legado).</summary>
public sealed class FlagsCarregadasHealthCheck(IFonteDeFlags fonte) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(fonte.Atual() is null
            ? HealthCheckResult.Unhealthy("flags de migração indisponíveis")
            : HealthCheckResult.Healthy());
}
