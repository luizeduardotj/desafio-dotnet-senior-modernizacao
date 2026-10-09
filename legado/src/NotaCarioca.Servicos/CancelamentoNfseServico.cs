using System;
using System.Data;
using System.Xml;
using NotaCarioca.Dados;
using NotaCarioca.Dominio;
using NotaCarioca.Servicos.Modelos;

namespace NotaCarioca.Servicos
{
    public class CancelamentoNfseServico
    {
        private ContribuinteRepositorio _contribuintes = new ContribuinteRepositorio();
        private NfseRepositorio _nfse = new NfseRepositorio();

        public string Cancelar(string xml)
        {
            const string raizResp = "CancelarNfseResposta";
            XmlDocument doc;
            try { doc = LeitorLoteRps.Carregar(xml); }
            catch (XmlException) { return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E001); }
            XmlNode raiz = doc.DocumentElement;
            if (raiz == null || raiz.Name != "CancelarNfseEnvio")
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E001);

            XmlNode inf = raiz.SelectSingleNode("Pedido/InfPedidoCancelamento");
            if (inf == null)
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E001);

            long numero;
            if (!long.TryParse(LeitorLoteRps.Texto(inf, "IdentificacaoNfse/Numero"), out numero))
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E001);
            string cnpj = LeitorLoteRps.Texto(inf, "IdentificacaoNfse/Cnpj");
            string im = LeitorLoteRps.Texto(inf, "IdentificacaoNfse/InscricaoMunicipal");
            string codigoCancelamento = LeitorLoteRps.Texto(inf, "CodigoCancelamento") ?? "1";
            string motivo = LeitorLoteRps.Texto(inf, "Motivo");
            if (string.IsNullOrEmpty(motivo)) motivo = "Cancelamento solicitado pelo contribuinte";

            DataTable dtp = _contribuintes.ObterPorInscricao(im ?? "");
            if (dtp.Rows.Count == 0)
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E010);
            Prestador prestador = Prestador.DeLinha(dtp.Rows[0]);
            if (!prestador.Ativo)
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E010);

            string erro = _nfse.Cancelar(prestador.Id, numero, motivo);
            if (erro != null)
                return XmlNfseBuilder.RespostaErro(raizResp, erro);

            DataTable nota = _nfse.ObterPorNumero(prestador.Id, numero);
            DateTime dataHora = Convert.ToDateTime(nota.Rows[0]["DT_CANCELAMENTO"]);

            return "<" + raizResp + "><Cancelamento><Confirmacao><Pedido><InfPedidoCancelamento>" +
                   "<IdentificacaoNfse><Numero>" + numero + "</Numero><Cnpj>" + XmlNfseBuilder.Esc(cnpj) + "</Cnpj>" +
                   "<InscricaoMunicipal>" + XmlNfseBuilder.Esc(im) + "</InscricaoMunicipal></IdentificacaoNfse>" +
                   "<CodigoCancelamento>" + XmlNfseBuilder.Esc(codigoCancelamento) + "</CodigoCancelamento>" +
                   "</InfPedidoCancelamento></Pedido><DataHora>" + XmlNfseBuilder.FmtData(dataHora) +
                   "</DataHora></Confirmacao></Cancelamento></" + raizResp + ">";
        }
    }
}
