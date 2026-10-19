using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi;
using Nfse.Aplicacao.Cancelamento;
using Nfse.Aplicacao.Consultas;
using Nfse.Dominio.Cancelamento;
using Nfse.Dominio.Comum;
using Nfse.Dominio.Documentos;
using Nfse.Dominio.Notas;

namespace Nfse.Adaptadores.Rest;

/// <summary>API REST v1 (ADR-0010). Autenticação obrigatória; o prestador precisa estar na claim <c>inscricoes</c>.</summary>
public static class EndpointsRest
{
    public const string ClaimInscricoes = "inscricoes";
    public const string ClaimDocumento = "documento";

    public static IEndpointRouteBuilder MapNfseRest(this IEndpointRouteBuilder app)
    {
        // 401 sai do desafio da autenticação (sem corpo), e o UseStatusCodePages escreve o ProblemDetails.
        RouteGroupBuilder v1 = app.MapGroup("/api/v1").RequireAuthorization().WithTags("NFS-e")
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        v1.MapGet("/prestadores/{inscricao}/nfse", ListarEmitidasAsync)
          .WithName("ListarNotasEmitidas")
          .WithSummary("Notas emitidas pelo prestador no período (inclusivo, até 31 dias), por ordem de emissão.")
          .Produces<PaginaDto>()
          .ProducesValidationProblem()
          .ProducesProblem(StatusCodes.Status403Forbidden)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .LimiteDocumentadoComoInteiro();

        v1.MapPost("/prestadores/{inscricao}/nfse/{numero:long}/cancelamento", CancelarAsync)
          .WithName("CancelarNota")
          .WithSummary("Cancela a NFS-e. Repetir o pedido é seguro: a nota já cancelada devolve o cancelamento existente.")
          .Accepts<PedidoDeCancelamentoDto>("application/json")
          .Produces<CancelamentoEfetuadoDto>()
          .ProducesValidationProblem()
          .ProducesProblem(StatusCodes.Status403Forbidden)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .ProducesProblem(StatusCodes.Status409Conflict)
          .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // O documento do tomador vem do token, nunca da URL: CPF não vai para access log nem para url.path (ADR-0012).
        v1.MapGet("/tomador/nfse", ListarTomadasAsync)
          .WithName("ListarNotasTomadas")
          .WithSummary("Notas recebidas pelo titular do token, de todos os prestadores, no período (inclusivo, até 31 dias).")
          .Produces<PaginaDto>()
          .ProducesValidationProblem()
          .ProducesProblem(StatusCodes.Status403Forbidden)
          .LimiteDocumentadoComoInteiro();

        return app;
    }

    private static async Task<IResult> ListarTomadasAsync(
        string? dataInicial,
        string? dataFinal,
        string? situacao,
        string? limite,
        string? cursor,
        ClaimsPrincipal usuario,
        ConsultaDeNotasTomadas consulta,
        CancellationToken cancellationToken)
    {
        Resultado<Documento> titular = Documento.Criar(usuario.FindFirst(ClaimDocumento)?.Value);
        if (!titular.Sucesso)
        {
            return Problemas.SemPermissao();
        }

        var erros = new Dictionary<string, string[]>(StringComparer.Ordinal);
        PeriodoDeEmissao? periodo = LerPeriodo(dataInicial, dataFinal, erros);
        SituacaoNota? filtroSituacao = LerSituacao(situacao, erros);
        int tamanho = LerLimite(limite, erros);

        // O resumo do cursor usa um hash do documento, nunca o documento em claro.
        string filtros = string.Join('|', "tomador", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(titular.Valor.Digitos))), dataInicial, dataFinal, situacao);
        if (!Cursor.TentarDecodificar(cursor, filtros, out PosicaoNaListagem? apos))
        {
            erros[nameof(cursor)] = ["Cursor inválido ou de outra consulta."];
        }

        if (erros.Count > 0 || periodo is null)
        {
            return Problemas.ParametrosInvalidos(erros);
        }

        Resultado<PaginaDeNotas> pagina = await consulta.ListarAsync(
            titular.Valor, periodo, filtroSituacao, apos, tamanho, cancellationToken).ConfigureAwait(false);

        return pagina.Erro switch
        {
            null => Results.Ok(new PaginaDto(
                [.. pagina.Valor.Itens.Select(Converter)],
                pagina.Valor.Proxima is { } proxima ? Cursor.Codificar(proxima, filtros) : null)),
            var erro => Problemas.ParametrosInvalidos(new Dictionary<string, string[]> { [nameof(limite)] = [erro.Descricao] }),
        };
    }

    /// <summary>Limite da coluna <c>MOTIVO_CANCELAMENTO VARCHAR2(255)</c>, em bytes (K11).</summary>
    private const int MaximoDeBytesDoMotivo = 255;

    private static async Task<IResult> CancelarAsync(
        string inscricao,
        long numero,
        PedidoDeCancelamentoDto? pedido,
        ClaimsPrincipal usuario,
        CancelamentoDeNota cancelamento)
    {
        if (!Representa(usuario, inscricao))
        {
            return Problemas.SemPermissao();
        }

        string? motivo = pedido?.Motivo?.Trim();
        if (string.IsNullOrEmpty(motivo) || System.Text.Encoding.UTF8.GetByteCount(motivo) > MaximoDeBytesDoMotivo)
        {
            return Problemas.ParametrosInvalidos(new Dictionary<string, string[]>
            {
                ["motivo"] = [$"Obrigatório, com até {MaximoDeBytesDoMotivo} bytes em UTF-8."],
            });
        }

        // Sem o token da requisição: chegando ao banco, o cancelamento termina e comita; repetir é seguro (ADR-0009).
        Resultado<RespostaDoCancelamento> resultado = await cancelamento
            .CancelarAsync(inscricao, numero, motivo, CancellationToken.None).ConfigureAwait(false);

        if (resultado.Erro is PrestadorNaoEncontrado)
        {
            return Problemas.PrestadorNaoEncontrado();
        }

        RespostaDoCancelamento resposta = resultado.Valor;
        return resposta.Decisao switch
        {
            DecisaoDeCancelamento.NotaNaoEncontrada => Problemas.NfseNaoEncontrada(),
            DecisaoDeCancelamento.NotaSubstituida => Problemas.NfseSubstituida(),
            DecisaoDeCancelamento.PrazoExpirado => Problemas.PrazoExpirado(),
            DecisaoDeCancelamento.GuiaPaga => Problemas.GuiaPaga(),
            _ => Results.Ok(new CancelamentoEfetuadoDto(
                numero,
                HorarioDeSaoPaulo.ComOffset(resposta.DataCancelamento ?? throw new InvalidOperationException("Cancelamento sem data.")),
                resposta.Decisao == DecisaoDeCancelamento.JaCancelada)),
        };
    }

    private static PeriodoDeEmissao? LerPeriodo(string? dataInicial, string? dataFinal, Dictionary<string, string[]> erros)
    {
        DateOnly? inicio = LerData(dataInicial, nameof(dataInicial), erros);
        DateOnly? fim = LerData(dataFinal, nameof(dataFinal), erros);
        if (inicio is not { } i || fim is not { } f)
        {
            return null;
        }

        Resultado<PeriodoDeEmissao> resultado = PeriodoDeEmissao.Criar(i, f);
        if (resultado.Sucesso)
        {
            return resultado.Valor;
        }

        erros["periodo"] = [resultado.Erro!.Descricao];
        return null;
    }

    private static async Task<IResult> ListarEmitidasAsync(
        string inscricao,
        string? dataInicial,
        string? dataFinal,
        string? situacao,
        string? limite,
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
        PeriodoDeEmissao? periodo = LerPeriodo(dataInicial, dataFinal, erros);
        SituacaoNota? filtroSituacao = LerSituacao(situacao, erros);
        int tamanho = LerLimite(limite, erros);

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
            inscricao, periodo, filtroSituacao, apos, tamanho, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// <c>limite</c> chega como texto para que um valor não inteiro também seja <c>parametros-invalidos</c> com <c>errors</c>;
    /// com <c>int?</c>, a falha de binding do framework devolveria um 400 genérico (500 em Development) fora do catálogo.
    /// </summary>
    private static int LerLimite(string? texto, Dictionary<string, string[]> erros)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return ConsultaDeNotasEmitidas.LimitePadrao;
        }

        if (int.TryParse(texto, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int limite))
        {
            return limite; // a faixa é regra da consulta (LimiteInvalido)
        }

        erros["limite"] = [new LimiteInvalido(ConsultaDeNotasEmitidas.LimiteMaximo).Descricao];
        return ConsultaDeNotasEmitidas.LimitePadrao;
    }

    /// <summary>No OpenAPI, <c>limite</c> continua inteiro (o mesmo schema que o <c>int?</c> gerava), embora seja lido como texto.</summary>
    private static TBuilder LimiteDocumentadoComoInteiro<TBuilder>(this TBuilder endpoint)
        where TBuilder : IEndpointConventionBuilder =>
        endpoint.AddOpenApiOperationTransformer((operacao, _, _) =>
        {
            if (operacao.Parameters?.FirstOrDefault(p => p.Name == "limite") is OpenApiParameter parametro)
            {
                parametro.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer | JsonSchemaType.String,
                    Format = "int32",
                    Pattern = "^-?(?:0|[1-9]\\d*)$",
                };
            }

            return Task.CompletedTask;
        });

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
