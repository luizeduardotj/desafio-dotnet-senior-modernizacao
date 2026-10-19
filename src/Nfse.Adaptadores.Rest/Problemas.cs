using Microsoft.AspNetCore.Http;

namespace Nfse.Adaptadores.Rest;

/// <summary>Tipos de ProblemDetails (RFC 9457) da API v1, catálogo da ADR-0010.</summary>
internal static class Problemas
{
    private const string Base = "https://api.notacarioca.rio.gov.br/problemas/";

    public static IResult ParametrosInvalidos(IDictionary<string, string[]> erros) =>
        Results.ValidationProblem(erros, type: Base + "parametros-invalidos", title: "Parâmetros inválidos.");

    public static IResult SemPermissao() =>
        Results.Problem(type: Base + "sem-permissao", title: "Sem permissão para este prestador.", statusCode: StatusCodes.Status403Forbidden);

    public static IResult PrestadorNaoEncontrado() =>
        Results.Problem(type: Base + "prestador-nao-encontrado", title: "Prestador não encontrado ou inativo.", statusCode: StatusCodes.Status404NotFound);
}
