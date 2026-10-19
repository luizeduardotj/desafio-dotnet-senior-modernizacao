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
    private const string Raiz = "CancelarNfseResposta";
    private const string MotivoPadrao = "Cancelamento solicitado pelo contribuinte";

    public async Task<string> ExecutarAsync(string entrada, CancellationToken cancellationToken)
    {
        XmlDocument documento;
        try
        {
            documento = CompatibilidadeLegado.Carregar(entrada);
        }
        catch (XmlException)
        {
            return SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E001);
        }

        XmlElement? raiz = documento.DocumentElement;
        if (raiz is null || raiz.Name != "CancelarNfseEnvio")
        {
            return SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E001);
        }

        XmlNode? inf = raiz.SelectSingleNode("Pedido/InfPedidoCancelamento");
        if (inf is null ||
            !long.TryParse(CompatibilidadeLegado.Texto(inf, "IdentificacaoNfse/Numero"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long numero))
        {
            return SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E001);
        }

        // K04: o CNPJ do pedido só é ecoado. K05: CodigoCancelamento ausente vira "1" e só é ecoado.
        string? cnpj = CompatibilidadeLegado.Texto(inf, "IdentificacaoNfse/Cnpj");
        string? inscricao = CompatibilidadeLegado.Texto(inf, "IdentificacaoNfse/InscricaoMunicipal");
        string codigoCancelamento = CompatibilidadeLegado.Texto(inf, "CodigoCancelamento") ?? "1";
        // K06: motivo vazio vira o texto padrão; o PL/SQL trunca em 255 caracteres.
        string? motivo = CompatibilidadeLegado.Texto(inf, "Motivo");
        if (string.IsNullOrEmpty(motivo))
        {
            motivo = MotivoPadrao;
        }

        Resultado<RespostaDoCancelamento> resultado = await cancelamento
            .CancelarAsync(inscricao ?? "", numero, motivo, cancellationToken).ConfigureAwait(false);

        if (resultado.Erro is PrestadorNaoEncontrado)
        {
            return SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E010);
        }

        RespostaDoCancelamento resposta = resultado.Valor;
        string? codigoDeErro = resposta.Decisao switch
        {
            DecisaoDeCancelamento.NotaNaoEncontrada => CatalogoDeRetornos.E050,
            DecisaoDeCancelamento.NotaSubstituida => CatalogoDeRetornos.E051,
            DecisaoDeCancelamento.PrazoExpirado => CatalogoDeRetornos.E052,
            DecisaoDeCancelamento.GuiaPaga => CatalogoDeRetornos.E053,
            _ => null,
        };

        if (codigoDeErro is not null)
        {
            return SerializadorLegado.RespostaErro(Raiz, codigoDeErro);
        }

        // K01: já cancelada devolve o DataHora original. K09: o instante vem da mesma transação do cancelamento.
        DateTime dataHora = resposta.DataCancelamento
            ?? throw new InvalidOperationException("Cancelamento sem DT_CANCELAMENTO.");

        // K10: eco do pedido, sem protocolo.
        return "<" + Raiz + "><Cancelamento><Confirmacao><Pedido><InfPedidoCancelamento>" +
               "<IdentificacaoNfse><Numero>" + numero.ToString(CultureInfo.InvariantCulture) + "</Numero><Cnpj>" +
               SerializadorLegado.Esc(cnpj) + "</Cnpj>" +
               "<InscricaoMunicipal>" + SerializadorLegado.Esc(inscricao) + "</InscricaoMunicipal></IdentificacaoNfse>" +
               "<CodigoCancelamento>" + SerializadorLegado.Esc(codigoCancelamento) + "</CodigoCancelamento>" +
               "</InfPedidoCancelamento></Pedido><DataHora>" + SerializadorLegado.FmtData(dataHora) +
               "</DataHora></Confirmacao></Cancelamento></" + Raiz + ">";
    }
}
