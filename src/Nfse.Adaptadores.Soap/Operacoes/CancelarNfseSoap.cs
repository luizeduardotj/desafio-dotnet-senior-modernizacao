using System.Globalization;
using System.Xml;
using Nfse.Adaptadores.Soap.Legado;
using Nfse.Aplicacao.Cancelamento;
using Nfse.Aplicacao.Consultas;
using Nfse.Dominio.Cancelamento;
using Nfse.Dominio.Comum;

namespace Nfse.Adaptadores.Soap.Operacoes;

/// <summary>
/// <c>CancelarNfse</c> no contrato do legado (<c>CancelamentoNfseServico.Cancelar</c>): E001 → E010 → regra do PL/SQL
/// (E050 → já cancelada → E051 → E052 → E053, K02).
/// </summary>
internal sealed class CancelarNfseSoap(CancelamentoDeNota cancelamento)
{
    public const string Raiz = "CancelarNfseResposta";

    public async Task<string> ExecutarAsync(string entrada, CancellationToken cancellationToken)
    {
        if (!PedidoDeCancelamentoSoap.TentarLer(entrada, out PedidoDeCancelamentoSoap? pedido))
        {
            return SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E001);
        }

        Resultado<RespostaDoCancelamento> resultado = await cancelamento
            .CancelarAsync(pedido.InscricaoMunicipal ?? "", pedido.Numero, pedido.Motivo, cancellationToken).ConfigureAwait(false);

        if (resultado.Erro is PrestadorNaoEncontrado)
        {
            return SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E010);
        }

        RespostaDoCancelamento resposta = resultado.Valor;
        if (CodigoDeErro(resposta.Decisao) is { } codigo)
        {
            return SerializadorLegado.RespostaErro(Raiz, codigo);
        }

        // K01: já cancelada devolve o DataHora original. K09: o instante vem da mesma transação do cancelamento.
        return Confirmacao(pedido, resposta.DataCancelamento ?? throw new InvalidOperationException("Cancelamento sem DT_CANCELAMENTO."));
    }

    public static string? CodigoDeErro(DecisaoDeCancelamento decisao) => decisao switch
    {
        DecisaoDeCancelamento.NotaNaoEncontrada => CatalogoDeRetornos.E050,
        DecisaoDeCancelamento.NotaSubstituida => CatalogoDeRetornos.E051,
        DecisaoDeCancelamento.PrazoExpirado => CatalogoDeRetornos.E052,
        DecisaoDeCancelamento.GuiaPaga => CatalogoDeRetornos.E053,
        _ => null,
    };

    /// <summary>K10: eco do pedido, sem protocolo.</summary>
    public static string Confirmacao(PedidoDeCancelamentoSoap pedido, DateTime dataHora) =>
        "<" + Raiz + "><Cancelamento><Confirmacao><Pedido><InfPedidoCancelamento>" +
        "<IdentificacaoNfse><Numero>" + pedido.Numero.ToString(CultureInfo.InvariantCulture) + "</Numero><Cnpj>" +
        SerializadorLegado.Esc(pedido.Cnpj) + "</Cnpj>" +
        "<InscricaoMunicipal>" + SerializadorLegado.Esc(pedido.InscricaoMunicipal) + "</InscricaoMunicipal></IdentificacaoNfse>" +
        "<CodigoCancelamento>" + SerializadorLegado.Esc(pedido.CodigoCancelamento) + "</CodigoCancelamento>" +
        "</InfPedidoCancelamento></Pedido><DataHora>" + SerializadorLegado.FmtData(dataHora) +
        "</DataHora></Confirmacao></Cancelamento></" + Raiz + ">";
}

/// <summary>Pedido de cancelamento lido como o legado lê; falha de leitura é E001.</summary>
internal sealed record PedidoDeCancelamentoSoap(long Numero, string? Cnpj, string? InscricaoMunicipal, string CodigoCancelamento, string Motivo)
{
    private const string MotivoPadrao = "Cancelamento solicitado pelo contribuinte";

    public static bool TentarLer(string entrada, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PedidoDeCancelamentoSoap? pedido)
    {
        pedido = null;
        XmlDocument documento;
        try
        {
            documento = CompatibilidadeLegado.Carregar(entrada);
        }
        catch (XmlException)
        {
            return false;
        }

        XmlElement? raiz = documento.DocumentElement;
        if (raiz is null || raiz.Name != "CancelarNfseEnvio")
        {
            return false;
        }

        XmlNode? inf = raiz.SelectSingleNode("Pedido/InfPedidoCancelamento");
        if (inf is null ||
            !long.TryParse(CompatibilidadeLegado.Texto(inf, "IdentificacaoNfse/Numero"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long numero))
        {
            return false;
        }

        // K04: o CNPJ só é ecoado. K05: CodigoCancelamento ausente vira "1" e só é ecoado. K06: motivo vazio vira o padrão.
        string? motivo = CompatibilidadeLegado.Texto(inf, "Motivo");
        pedido = new PedidoDeCancelamentoSoap(
            numero,
            CompatibilidadeLegado.Texto(inf, "IdentificacaoNfse/Cnpj"),
            CompatibilidadeLegado.Texto(inf, "IdentificacaoNfse/InscricaoMunicipal"),
            CompatibilidadeLegado.Texto(inf, "CodigoCancelamento") ?? "1",
            string.IsNullOrEmpty(motivo) ? MotivoPadrao : motivo);
        return true;
    }
}
