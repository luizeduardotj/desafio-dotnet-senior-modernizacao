using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Nfse.Fachada.Configuracao;
using Nfse.Fachada.Observabilidade;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Admin;

/// <summary>
/// <c>POST</c>/<c>DELETE /admin/kill-switch[?operacao=]</c> e <c>GET</c> do estado. Só respondem na porta interna
/// (<c>Admin:Porta</c>, pelo roteamento) e com o header <see cref="HeaderDoToken"/> igual ao segredo <c>Admin:Token</c>.
/// </summary>
public static class EndpointsAdministrativos
{
    public const string HeaderDoToken = "X-Admin-Token";

    public static IEndpointRouteBuilder MapEndpointsAdministrativos(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder admin = app.MapGroup("/admin").SomenteNaPorta(Plano.Admin).AddEndpointFilter(SomentePortaInternaComToken);

        admin.MapGet("/kill-switch", (KillSwitchAdministrativo kill) => Results.Ok(kill.Estado()));
        admin.MapPost("/kill-switch", (string? operacao, KillSwitchAdministrativo kill, ILogger<KillSwitchAdministrativo> logger) =>
            Alterar(operacao, ativar: true, kill, logger));
        admin.MapDelete("/kill-switch", (string? operacao, KillSwitchAdministrativo kill, ILogger<KillSwitchAdministrativo> logger) =>
            Alterar(operacao, ativar: false, kill, logger));

        return app;
    }

    private static IResult Alterar(string? operacao, bool ativar, KillSwitchAdministrativo kill, ILogger<KillSwitchAdministrativo> logger)
    {
        if (operacao is not null && !OperacoesSoap.EstaEmEscopo(operacao))
        {
            return Results.BadRequest(new { erro = "operacao deve ser uma de: " + string.Join(", ", OperacoesSoap.EmEscopo) });
        }

        if (ativar)
        {
            kill.Ativar(operacao);
        }
        else
        {
            kill.Desativar(operacao);
        }

        Log.KillSwitchAlterado(logger, ativar, operacao ?? "global");
        return Results.Ok(kill.Estado());
    }

    private static async ValueTask<object?> SomentePortaInternaComToken(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        HttpContext http = ctx.HttpContext;
        OpcoesAdmin opcoes = http.RequestServices.GetRequiredService<IOptions<OpcoesAdmin>>().Value;

        // Defesa em profundidade: o roteamento já restringe o grupo à porta interna.
        if (http.Connection.LocalPort != opcoes.Porta)
        {
            return Results.NotFound();
        }

        if (string.IsNullOrEmpty(opcoes.Token) || !TokenConfere(http.Request.Headers[HeaderDoToken].ToString(), opcoes.Token))
        {
            return Results.Unauthorized();
        }

        return await next(ctx).ConfigureAwait(false);
    }

    private static bool TokenConfere(string recebido, string esperado) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(recebido)),
            SHA256.HashData(Encoding.UTF8.GetBytes(esperado)));
}
