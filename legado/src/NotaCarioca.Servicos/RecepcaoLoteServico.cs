using System;
using System.Data;
using System.Xml;
using NotaCarioca.Dados;
using NotaCarioca.Dominio;
using NotaCarioca.Servicos.Modelos;

namespace NotaCarioca.Servicos
{
    public class RecepcaoLoteServico
    {
        private ContribuinteRepositorio _contribuintes = new ContribuinteRepositorio();
        private LoteRepositorio _lotes = new LoteRepositorio();

        public string Recepcionar(string xml)
        {
            XmlDocument doc;
            LoteLido cab;
            int qtd;
            try
            {
                doc = LeitorLoteRps.Carregar(xml);
                cab = LeitorLoteRps.LerCabecalho(doc);
                qtd = doc.SelectNodes("/EnviarLoteRpsEnvio/LoteRps/ListaRps/Rps").Count;
            }
            catch (XmlException)
            {
                return XmlNfseBuilder.RespostaErro("EnviarLoteRpsResposta", CodigosRetorno.E001);
            }

            DataTable dt = _contribuintes.ObterPorInscricao(cab.InscricaoMunicipal ?? "");
            if (dt.Rows.Count == 0)
                return XmlNfseBuilder.RespostaErro("EnviarLoteRpsResposta", CodigosRetorno.E010);
            Prestador prestador = Prestador.DeLinha(dt.Rows[0]);
            if (!prestador.Ativo)
                return XmlNfseBuilder.RespostaErro("EnviarLoteRpsResposta", CodigosRetorno.E010);

            string protocolo = _lotes.Inserir(prestador.Id, cab.NumeroLote, qtd, xml);
            DataTable lote = _lotes.Obter(protocolo);
            DateTime recebido = Convert.ToDateTime(lote.Rows[0]["DT_RECEBIMENTO"]);

            return "<EnviarLoteRpsResposta><NumeroLote>" + cab.NumeroLote + "</NumeroLote><DataRecebimento>" +
                   XmlNfseBuilder.FmtData(recebido) + "</DataRecebimento><Protocolo>" + protocolo + "</Protocolo></EnviarLoteRpsResposta>";
        }
    }
}
