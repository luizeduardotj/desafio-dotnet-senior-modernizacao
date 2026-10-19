using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nfse.Aplicacao.Consultas;
using Nfse.Dominio.Comum;
using Nfse.Dominio.Documentos;
using Nfse.Dominio.Notas;

namespace Nfse.Adaptadores.Rest;

/// <summary>API REST v1 (ADR-0010). Autenticação obrigatória; o prestador precisa estar na claim <c>inscricoes</c>.</summary>
public static class EndpointsRest
{
    public const string ClaimInscricoes = "inscricoes";

    public static IEndpointRouteBuilder MapNfseRest(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder v1 = app.MapGroup("/api/v1").RequireAuthorization().WithTags("NFS-e");

        v1.MapGet("/prestadores/{inscricao}/nfse", ListarEmitidasAsync)
          .WithName("ListarNotasEmitidas")
          .WithSummary("Notas emitidas pelo prestador no período (inclusivo, até 31 dias), por ordem de emissão.")
          .Produces<PaginaDto>()
          .ProducesValidationProblem()
          .ProducesProblem(StatusCodes.Status403Forbidden)
          .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> ListarEmitidasAsync(
        string inscricao,
        string? dataInicial,
        string? dataFinal,
        string? situacao,
        int? limite,
        string? cursor,
        ClaimsPrincipal usuario,
        ConsultaDeNotasEmitidas consulta,
        CancellationToken cancellationToken)
    {
        if (!Representa(usuario, inscricao))
        {
            return Problemas.SemPermissao();
        }

        var erros = new Dictionary<string, string[]>(StringComparer.Ordinal);
        DateOnly? inicio = LerData(dataInicial, nameof(dataInicial), erros);
        DateOnly? fim = LerData(dataFinal, nameof(dataFinal), erros);
        SituacaoNota? filtroSituacao = LerSituacao(situacao, erros);

        PeriodoDeEmissao? periodo = null;
        if (inicio is { } i && fim is { } f)
        {
            Resultado<PeriodoDeEmissao> resultado = PeriodoDeEmissao.Criar(i, f);
            if (resultado.Sucesso)
            {
                periodo = resultado.Valor;
            }
            else
            {
                erros["periodo"] = [resultado.Erro!.Descricao];
            }
        }

        string filtros = string.Join('|', inscricao, dataInicial, dataFinal, situacao);
        if (!Cursor.TentarDecodificar(cursor, filtros, out PosicaoNaListagem? apos))
        {
            erros[nameof(cursor)] = ["Cursor inválido ou de outra consulta."];
        }

        if (erros.Count > 0 || periodo is null)
        {
            return Problemas.ParametrosInvalidos(erros);
        }

        Resultado<PaginaDeNotas> pagina = await consulta.ListarAsync(
            inscricao, periodo, filtroSituacao, apos, limite ?? ConsultaDeNotasEmitidas.LimitePadrao, cancellationToken).ConfigureAwait(false);

        return pagina.Erro switch
        {
            null => Results.Ok(new PaginaDto(
                [.. pagina.Valor.Itens.Select(Converter)],
                pagina.Valor.Proxima is { } proxima ? Cursor.Codificar(proxima, filtros) : null)),
            PrestadorNaoEncontrado => Problemas.PrestadorNaoEncontrado(),
            var erro => Problemas.ParametrosInvalidos(new Dictionary<string, string[]> { [nameof(limite)] = [erro.Descricao] }),
        };
    }

    private static bool Representa(ClaimsPrincipal usuario, string inscricao) =>
        usuario.FindAll(ClaimInscricoes)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(inscricao, StringComparer.Ordinal);

    private static DateOnly? LerData(string? texto, string campo, Dictionary<string, string[]> erros)
    {
        if (DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly data))
        {
            return data;
        }

        erros[campo] = ["Obrigatória, no formato yyyy-MM-dd."];
        return null;
    }

    private static SituacaoNota? LerSituacao(string? texto, Dictionary<string, string[]> erros)
    {
        switch (texto)
        {
            case null or "":
                return null;
            case "normal":
                return SituacaoNota.Normal;
            case "cancelada":
                return SituacaoNota.Cancelada;
            case "substituida":
                return SituacaoNota.Substituida;
            default:
                erros["situacao"] = ["Use normal, cancelada ou substituida."];
                return null;
        }
    }

    private static NfseDto Converter(NotaFiscalLida n) => new(
        n.Numero,
        n.CodigoVerificacao,
        HorarioDeSaoPaulo.ComOffset(n.DataEmissao),
        n.Competencia?.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        n.Situacao switch
        {
            SituacaoNota.Cancelada => "cancelada",
            SituacaoNota.Substituida => "substituida",
            _ => "normal",
        },
        new ServicoDto(n.ItemServico, n.Discriminacao, n.ValorServicos, n.ValorDeducoes, n.BaseCalculo, n.Aliquota, n.ValorIss, n.IssRetido),
        new PrestadorDto(n.Prestador.InscricaoMunicipal, n.Prestador.Documento, n.Prestador.RazaoSocial),
        new TomadorDto(
            n.TomadorDocumento is { } d ? new DocumentoDto(d.Digitos, d.Tipo == TipoDocumento.Cpf ? "cpf" : "cnpj") : null,
            n.TomadorRazaoSocial),
        n.Situacao == SituacaoNota.Cancelada && n.DataCancelamento is { } c ? new CancelamentoDto(HorarioDeSaoPaulo.ComOffset(c)) : null,
        n.Situacao == SituacaoNota.Substituida && n.NumeroSubstituidora is { } s ? new SubstituicaoDto(s) : null);
}
