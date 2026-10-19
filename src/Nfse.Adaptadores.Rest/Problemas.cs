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

    public static IResult NfseNaoEncontrada() =>
        Results.Problem(type: Base + "nfse-nao-encontrada", title: "NFS-e não encontrada para o prestador.", statusCode: StatusCodes.Status404NotFound);

    public static IResult NfseSubstituida() =>
        Results.Problem(type: Base + "nfse-substituida", title: "NFS-e substituída não pode ser cancelada.", statusCode: StatusCodes.Status409Conflict);

    public static IResult PrazoExpirado() =>
        Results.Problem(type: Base + "prazo-de-cancelamento-expirado", title: "O cancelamento é permitido até o dia 10 do mês seguinte à competência.",
            statusCode: StatusCodes.Status422UnprocessableEntity);

    public static IResult GuiaPaga() =>
        Results.Problem(type: Base + "guia-paga", title: "A guia da competência já foi paga.", statusCode: StatusCodes.Status422UnprocessableEntity);
}
