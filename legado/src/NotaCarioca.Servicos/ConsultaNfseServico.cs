using System;
using System.Data;
using System.Globalization;
using System.Text;
using System.Xml;
using NotaCarioca.Dados;
using NotaCarioca.Dominio;
using NotaCarioca.Servicos.Modelos;

namespace NotaCarioca.Servicos
{
    public class ConsultaNfseServico
    {
        private ContribuinteRepositorio _contribuintes = new ContribuinteRepositorio();
        private LoteRepositorio _lotes = new LoteRepositorio();
        private NfseRepositorio _nfse = new NfseRepositorio();

        private Prestador BuscarPrestador(XmlNode raiz)
        {
            string im = LeitorLoteRps.Texto(raiz, "Prestador/InscricaoMunicipal");
            if (string.IsNullOrEmpty(im)) return null;
            DataTable dt = _contribuintes.ObterPorInscricao(im);
            if (dt.Rows.Count == 0) return null;
            Prestador p = Prestador.DeLinha(dt.Rows[0]);
            if (!p.Ativo) return null;
            return p;
        }

        private static bool LerPeriodo(XmlNode raiz, out DateTime ini, out DateTime fim)
        {
            ini = DateTime.MinValue;
            fim = DateTime.MinValue;
            string si = LeitorLoteRps.Texto(raiz, "PeriodoEmissao/DataInicial");
            string sf = LeitorLoteRps.Texto(raiz, "PeriodoEmissao/DataFinal");
            if (!DateTime.TryParse(si, CultureInfo.InvariantCulture, DateTimeStyles.None, out ini)) return false;
            if (!DateTime.TryParse(sf, CultureInfo.InvariantCulture, DateTimeStyles.None, out fim)) return false;
            ini = ini.Date;
            fim = fim.Date;
            if (ini > fim) return false;
            if ((fim - ini).TotalDays > 31) return false;
            return true;
        }

        private static int LerPagina(XmlNode raiz)
        {
            int p;
            if (!int.TryParse(LeitorLoteRps.Texto(raiz, "Pagina"), out p)) p = 1;
            if (p < 1) p = 1;
            return p;
        }

        private static string LerDocumento(XmlNode no)
        {
            if (no == null) return null;
            string d = LeitorLoteRps.Texto(no, "CpfCnpj/Cnpj");
            if (string.IsNullOrEmpty(d)) d = LeitorLoteRps.Texto(no, "CpfCnpj/Cpf");
            return d;
        }

        private static string MontarPagina(string raiz, DataTable dt)
        {
            int total = dt.Rows.Count;
            int exibir = Math.Min(total, NfseRepositorio.TamanhoPagina);
            StringBuilder sb = new StringBuilder();
            sb.Append("<").Append(raiz).Append("><ListaNfse>");
            for (int i = 0; i < exibir; i++)
            {
                sb.Append(XmlNfseBuilder.CompNfse(dt.Rows[i]));
            }
            sb.Append("</ListaNfse><ProximaPagina>")
              .Append(total > NfseRepositorio.TamanhoPagina ? "true" : "false")
              .Append("</ProximaPagina></").Append(raiz).Append(">");
            return sb.ToString();
        }

        public string ConsultarNfse(string xml)
        {
            const string raizResp = "ConsultarNfseResposta";
            XmlDocument doc;
            try { doc = LeitorLoteRps.Carregar(xml); }
            catch (XmlException) { return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E001); }
            XmlNode raiz = doc.DocumentElement;
            if (raiz == null || raiz.Name != "ConsultarNfseEnvio")
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E001);

            Prestador prestador = BuscarPrestador(raiz);
            if (prestador == null)
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E010);

            string tomador = LerDocumento(raiz.SelectSingleNode("Tomador"));
            if (!string.IsNullOrEmpty(tomador) && !ValidadorDocumento.ValidarCpfOuCnpj(tomador))
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E020);

            DateTime ini, fim;
            if (!LerPeriodo(raiz, out ini, out fim))
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E030);

            DataTable dt = _nfse.ConsultarPorPrestador(prestador.Id, ini, fim, tomador, LerPagina(raiz));
            if (dt.Rows.Count == 0)
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E040);
            return MontarPagina(raizResp, dt);
        }

        public string ConsultarNfseServicoTomado(string xml)
        {
            const string raizResp = "ConsultarNfseServicoTomadoResposta";
            XmlDocument doc;
            try { doc = LeitorLoteRps.Carregar(xml); }
            catch (XmlException) { return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E001); }
            XmlNode raiz = doc.DocumentElement;
            if (raiz == null || raiz.Name != "ConsultarNfseServicoTomadoEnvio")
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E001);

            string tomador = LerDocumento(raiz.SelectSingleNode("Consulente"));
            if (string.IsNullOrEmpty(tomador))
                tomador = LerDocumento(raiz.SelectSingleNode("Tomador"));
            if (!ValidadorDocumento.ValidarCpfOuCnpj(tomador))
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E020);

            DateTime ini, fim;
            if (!LerPeriodo(raiz, out ini, out fim))
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E030);

            DataTable dt = _nfse.ConsultarServicoTomado(tomador, ini, fim, LerPagina(raiz));
            if (dt.Rows.Count == 0)
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E040);
            return MontarPagina(raizResp, dt);
        }

        private DataRow ObterLote(XmlNode raiz, Prestador prestador, out string protocolo)
        {
            protocolo = LeitorLoteRps.Texto(raiz, "Protocolo");
            if (string.IsNullOrEmpty(protocolo)) return null;
            DataTable dt = _lotes.Obter(protocolo);
            if (dt.Rows.Count == 0) return null;
            if (Convert.ToInt64(dt.Rows[0]["PRESTADOR_ID"]) != prestador.Id) return null;
            return dt.Rows[0];
        }

        public string ConsultarSituacaoLoteRps(string xml)
        {
            const string raizResp = "ConsultarSituacaoLoteRpsResposta";
            XmlDocument doc;
            try { doc = LeitorLoteRps.Carregar(xml); }
            catch (XmlException) { return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E001); }
            XmlNode raiz = doc.DocumentElement;
            if (raiz == null || raiz.Name != "ConsultarSituacaoLoteRpsEnvio")
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E001);

            Prestador prestador = BuscarPrestador(raiz);
            if (prestador == null)
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E010);

            string protocolo;
            DataRow lote = ObterLote(raiz, prestador, out protocolo);
            if (lote == null)
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E070);

            return "<" + raizResp + "><NumeroLote>" + Convert.ToInt64(lote["NUMERO_LOTE"]) + "</NumeroLote><Situacao>" +
                   Convert.ToInt32(lote["SITUACAO"]) + "</Situacao></" + raizResp + ">";
        }

        public string ConsultarLoteRps(string xml)
        {
            const string raizResp = "ConsultarLoteRpsResposta";
            XmlDocument doc;
            try { doc = LeitorLoteRps.Carregar(xml); }
            catch (XmlException) { return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E001); }
            XmlNode raiz = doc.DocumentElement;
            if (raiz == null || raiz.Name != "ConsultarLoteRpsEnvio")
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E001);

            Prestador prestador = BuscarPrestador(raiz);
            if (prestador == null)
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E010);

            string protocolo;
            DataRow lote = ObterLote(raiz, prestador, out protocolo);
            if (lote == null)
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E070);

            int situacao = Convert.ToInt32(lote["SITUACAO"]);
            if (situacao < 3)
                return XmlNfseBuilder.RespostaErro(raizResp, CodigosRetorno.E071);

            DataTable notas = _nfse.ListarPorProtocolo(protocolo);
            StringBuilder lista = new StringBuilder();
            foreach (DataRow r in notas.Rows)
            {
                lista.Append(XmlNfseBuilder.CompNfse(r));
            }

            string resposta = "<" + raizResp + "><ListaNfse>" + lista.ToString() + "</ListaNfse>";
            if (situacao == 3 && lote["ERROS"] != DBNull.Value)
                resposta += Convert.ToString(lote["ERROS"]);
            resposta += "</" + raizResp + ">";
            return resposta;
        }
    }
}
