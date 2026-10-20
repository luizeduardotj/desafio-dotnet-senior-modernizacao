using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nfse.Adaptadores.Soap.Legado;
using Nfse.Adaptadores.Soap.Operacoes;
using Nfse.Aplicacao.Cancelamento;

namespace Nfse.Adaptadores.Soap;

/// <summary>Pedido da fachada para avaliar, sem escrever, um cancelamento que o legado já atendeu (ADR-0009 §3).</summary>
public sealed record PedidoDeSombraDoCancelamento(string Envelope, string RespostaLegado, int StatusLegado, long IdadeMs);

/// <param name="CnpjConfere">
/// O CNPJ do pedido é o do cadastro da IM (C18/K04, ADR-0004)? A fachada conta em <c>soap_cnpj_inconsistente_total</c>.
/// <see langword="null"/> quando não há o que conferir. Nunca leva o documento.
/// </param>
public sealed record ResultadoDeSombraDoCancelamento(
    string Resultado, string? Classe, string? DecisaoLegado, string? DecisaoNovo, bool? CnpjConfere = null);

/// <summary>
/// <c>POST /interno/sombra/cancelamento</c>. Só responde na porta interna; o caso de uso lê pelo usuário só leitura.
/// Compara a decisão e, quando ela coincide, também o XML de negócio que o novo devolveria (com o <c>DataHora</c> do
/// legado, já que o novo não escreve).
/// </summary>
public static partial class EndpointSombraDoCancelamento
{
    public const string Caminho = "/interno/sombra/cancelamento";

    public static RouteHandlerBuilder MapSombraDoCancelamento(this IEndpointRouteBuilder app) =>
        app.MapPost(Caminho, AvaliarAsync);

    private static async Task<IResult> AvaliarAsync(PedidoDeSombraDoCancelamento pedido, IServiceProvider servicos, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pedido);

        // Sem a connection string do usuário só leitura a sombra do cancelamento fica desligada (ADR-0002).
        if (servicos.GetService(typeof(SombraDoCancelamento)) is not SombraDoCancelamento sombra)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        string? legado = OutputXml(pedido.RespostaLegado);
        if (pedido.StatusLegado != StatusCodes.Status200OK || legado is null)
        {
            return Responder(null, "indeterminado", "Fault", null);
        }

        string entrada;
        try
        {
            entrada = LerInputXml(pedido.Envelope);
        }
        catch (XmlException)
        {
            return Responder(null, "indeterminado", CodigoOuSucesso(legado), null);
        }

        if (!PedidoDeCancelamentoSoap.TentarLer(entrada, out PedidoDeCancelamentoSoap? cancelamento))
        {
            return Comparar(legado, SerializadorLegado.RespostaErro(CancelarNfseSoap.Raiz, CatalogoDeRetornos.E001), "E001");
        }

        (RespostaObservada observada, DateTime? dataHora) = Observar(legado);
        AvaliacaoDaSombra avaliacao = await sombra.AvaliarAsync(
            cancelamento.InscricaoMunicipal ?? "", cancelamento.Numero, cancelamento.Cnpj, observada, dataHora, TimeSpan.FromMilliseconds(pedido.IdadeMs), cancellationToken)
            .ConfigureAwait(false);

        if (!avaliacao.PrestadorEncontrado)
        {
            return Comparar(legado, SerializadorLegado.RespostaErro(CancelarNfseSoap.Raiz, CatalogoDeRetornos.E010), "E010");
        }

        if (avaliacao.Classe is { } classe || avaliacao.DecisaoNovo is not { } decisaoNovo)
        {
            return Responder(avaliacao, Nome(avaliacao.Classe ?? ClasseDeSombra.Indeterminado), null, null);
        }

        // Mesma decisão: confere também o XML que o novo devolveria.
        string esperado = CancelarNfseSoap.CodigoDeErro(decisaoNovo) is { } codigo
            ? SerializadorLegado.RespostaErro(CancelarNfseSoap.Raiz, codigo)
            : CancelarNfseSoap.Confirmacao(cancelamento, dataHora ?? DateTime.MinValue);
        return esperado == legado
            ? Responder(avaliacao, null, null, null)
            : Responder(avaliacao, "nao_classificada", null, null);
    }

    private static IResult Comparar(string legado, string novo, string decisaoNovo) =>
        Results.Ok(new ResultadoDeSombraDoCancelamento(
            legado == novo ? "igual" : "divergente", legado == novo ? null : "nao_classificada", CodigoOuSucesso(legado), decisaoNovo));

    private static IResult Responder(AvaliacaoDaSombra? avaliacao, string? classe, string? decisaoLegado, string? decisaoNovo) =>
        Results.Ok(new ResultadoDeSombraDoCancelamento(
            classe is null ? "igual" : "divergente",
            classe,
            decisaoLegado ?? avaliacao?.DecisaoLegado?.ToString(),
            decisaoNovo ?? avaliacao?.DecisaoNovo?.ToString(),
            avaliacao?.DocumentoConfere));

    private static (RespostaObservada, DateTime?) Observar(string legado)
    {
        if (CodigoMatch().Match(legado) is { Success: true } m)
        {
            return (m.Groups[1].Value switch
            {
                "E050" => RespostaObservada.NotaNaoEncontrada,
                "E051" => RespostaObservada.NotaSubstituida,
                "E052" => RespostaObservada.PrazoExpirado,
                "E053" => RespostaObservada.GuiaPaga,
                _ => RespostaObservada.Fault,
            }, null);
        }

        if (DataHoraMatch().Match(legado) is { Success: true } d &&
            DateTime.TryParseExact(d.Groups[1].Value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dataHora))
        {
            return (RespostaObservada.Cancelou, dataHora);
        }

        return (RespostaObservada.Fault, null);
    }

    private static string CodigoOuSucesso(string legado) =>
        CodigoMatch().Match(legado) is { Success: true } m ? m.Groups[1].Value : "Sucesso";

    private static string Nome(ClasseDeSombra classe) => classe switch
    {
        ClasseDeSombra.Corrida => "corrida",
        ClasseDeSombra.FronteiraRelogio => "fronteira_relogio",
        ClasseDeSombra.Indeterminado => "indeterminado",
        _ => "nao_classificada",
    };

    /// <summary>XML de negócio dentro do <c>outputXML</c> da resposta do legado, ou <see langword="null"/>.</summary>
    private static string? OutputXml(string envelope)
    {
        try
        {
            using var leitor = XmlReader.Create(new StringReader(envelope), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (leitor.Read())
            {
                if (leitor.NodeType == XmlNodeType.Element && leitor.LocalName == "outputXML")
                {
                    return leitor.ReadElementContentAsString();
                }
            }
        }
        catch (XmlException)
        {
        }

        return null;
    }

    private static string LerInputXml(string envelope)
    {
        using var leitor = XmlReader.Create(new StringReader(envelope), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        while (leitor.Read())
        {
            if (leitor.NodeType == XmlNodeType.Element && leitor.LocalName == "inputXML")
            {
                return leitor.ReadElementContentAsString();
            }
        }

        return "";
    }

    [GeneratedRegex("<Codigo>(E\\d{3})</Codigo>")]
    private static partial Regex CodigoMatch();

    [GeneratedRegex("<DataHora>([^<]+)</DataHora>")]
    private static partial Regex DataHoraMatch();
}
