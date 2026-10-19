using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.Options;

namespace Nfse.Fachada.Configuracao;

public enum Plano
{
    /// <summary>Porta pública (<c>Fachada:Porta</c>): SOAP, pass-through ao legado e health.</summary>
    Dados,

    /// <summary>Porta interna (<c>Admin:Porta</c>): endpoints administrativos.</summary>
    Admin,
}

/// <summary>Metadado de endpoint: só casa com requisições que chegaram pela porta do plano.</summary>
public sealed record PortaDoEndpoint(Plano Plano);

public static class PortaDoEndpointExtensions
{
    public static TBuilder SomenteNaPorta<TBuilder>(this TBuilder builder, Plano plano)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new PortaDoEndpoint(plano));
}

/// <summary>
/// Separa os planos no roteamento pela porta local da conexão. Não usa <c>RequireHost</c>: o header Host vem do
/// cliente (dá para pedir <c>Host: x:8081</c> na porta pública) e, atrás do ingress, não traz a porta do Kestrel.
/// Endpoint de outro plano deixa de ser candidato; sem candidato, 404.
/// </summary>
internal sealed class PortaDoEndpointMatcherPolicy(IOptionsMonitor<OpcoesFachada> fachada, IOptionsMonitor<OpcoesAdmin> admin)
    : MatcherPolicy, IEndpointSelectorPolicy
{
    public override int Order => 0;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) =>
        endpoints.Any(e => e.Metadata.GetMetadata<PortaDoEndpoint>() is not null);

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        int porta = httpContext.Connection.LocalPort;
        for (int i = 0; i < candidates.Count; i++)
        {
            PortaDoEndpoint? exigida = candidates[i].Endpoint.Metadata.GetMetadata<PortaDoEndpoint>();
            if (exigida is not null && candidates.IsValidCandidate(i) && porta != PortaDo(exigida.Plano))
            {
                candidates.SetValidity(i, false);
            }
        }

        return Task.CompletedTask;
    }

    private int PortaDo(Plano plano) => plano == Plano.Admin ? admin.CurrentValue.Porta : fachada.CurrentValue.Porta;
}
