using System;
using System.Data;
using System.Globalization;
using System.Security;
using System.Text;
using NotaCarioca.Dominio;
using NotaCarioca.Servicos.Modelos;

namespace NotaCarioca.Servicos
{
    public static class XmlNfseBuilder
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Esc(string s)
        {
            if (s == null) return "";
            return SecurityElement.Escape(s);
        }

        public static string FmtData(DateTime d)
        {
            return d.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv);
        }

        public static string FmtCompetencia(DateTime d)
        {
            return d.ToString("yyyy-MM-01'T'00:00:00", Inv);
        }

        public static string FmtValor(decimal v)
        {
            return v.ToString("0.00", Inv);
        }

        public static string FmtAliquota(decimal a)
        {
            return a.ToString("0.####", Inv);
        }

        public static string Mensagem(string codigo)
        {
            return "<MensagemRetorno><Codigo>" + codigo + "</Codigo><Mensagem>" + Esc(CodigosRetorno.Mensagem(codigo)) +
                   "</Mensagem><Correcao>" + Esc(CodigosRetorno.Correcao(codigo)) + "</Correcao></MensagemRetorno>";
        }

        public static string ListaMensagens(string codigo)
        {
            return "<ListaMensagemRetorno>" + Mensagem(codigo) + "</ListaMensagemRetorno>";
        }

        public static string RespostaErro(string raiz, string codigo)
        {
            return "<" + raiz + ">" + ListaMensagens(codigo) + "</" + raiz + ">";
        }

        public static string MensagemRps(ResultadoRps r)
        {
            return "<MensagemRetorno><IdentificacaoRps><Numero>" + r.NumeroRps + "</Numero><Serie>" + Esc(r.Serie) +
                   "</Serie></IdentificacaoRps><Codigo>" + r.Codigo + "</Codigo><Mensagem>" + Esc(r.Mensagem) +
                   "</Mensagem><Correcao>" + Esc(CodigosRetorno.Correcao(r.Codigo)) + "</Correcao></MensagemRetorno>";
        }

        public static string CompNfse(DataRow r)
        {
            StringBuilder sb = new StringBuilder(1024);
            string situacao = Convert.ToString(r["SITUACAO"]);

            sb.Append("<CompNfse><Nfse><InfNfse>");
            sb.Append("<Numero>").Append(Convert.ToInt64(r["NUMERO"])).Append("</Numero>");
            sb.Append("<CodigoVerificacao>").Append(Esc(Convert.ToString(r["CODIGO_VERIFICACAO"]))).Append("</CodigoVerificacao>");
            sb.Append("<DataEmissao>").Append(FmtData(Convert.ToDateTime(r["DT_EMISSAO"]))).Append("</DataEmissao>");
            sb.Append("<Competencia>").Append(FmtCompetencia(Convert.ToDateTime(r["COMPETENCIA"]))).Append("</Competencia>");
            sb.Append("<Servico><Valores>");
            sb.Append("<ValorServicos>").Append(FmtValor(Convert.ToDecimal(r["VALOR_SERVICOS"]))).Append("</ValorServicos>");
            sb.Append("<ValorDeducoes>").Append(FmtValor(Convert.ToDecimal(r["VALOR_DEDUCOES"]))).Append("</ValorDeducoes>");
            sb.Append("<BaseCalculo>").Append(FmtValor(Convert.ToDecimal(r["BASE_CALCULO"]))).Append("</BaseCalculo>");
            sb.Append("<Aliquota>").Append(FmtAliquota(Convert.ToDecimal(r["ALIQUOTA"]))).Append("</Aliquota>");
            sb.Append("<ValorIss>").Append(FmtValor(Convert.ToDecimal(r["VALOR_ISS"]))).Append("</ValorIss>");
            sb.Append("<IssRetido>").Append(Convert.ToString(r["ISS_RETIDO"]) == "S" ? "1" : "2").Append("</IssRetido>");
            sb.Append("</Valores>");
            sb.Append("<ItemListaServico>").Append(Esc(Convert.ToString(r["ITEM_SERVICO"]))).Append("</ItemListaServico>");
            sb.Append("<Discriminacao>").Append(Esc(Convert.ToString(r["DISCRIMINACAO"]))).Append("</Discriminacao>");
            sb.Append("</Servico>");
            sb.Append("<PrestadorServico><IdentificacaoPrestador>");
            sb.Append("<Cnpj>").Append(Esc(Convert.ToString(r["PRESTADOR_DOC"]))).Append("</Cnpj>");
            sb.Append("<InscricaoMunicipal>").Append(Esc(Convert.ToString(r["PRESTADOR_IM"]))).Append("</InscricaoMunicipal>");
            sb.Append("</IdentificacaoPrestador>");
            sb.Append("<RazaoSocial>").Append(Esc(Convert.ToString(r["PRESTADOR_RAZAO"]))).Append("</RazaoSocial>");
            sb.Append("</PrestadorServico>");

            string docOriginal = Convert.ToString(r["TOMADOR_DOCUMENTO"]);
            string tag = docOriginal.Length > 11 ? "Cnpj" : "Cpf";
            string docNumero = Convert.ToInt64(r["TOMADOR_DOCUMENTO"]).ToString();
            sb.Append("<TomadorServico><IdentificacaoTomador><CpfCnpj>");
            sb.Append("<").Append(tag).Append(">").Append(docNumero).Append("</").Append(tag).Append(">");
            sb.Append("</CpfCnpj></IdentificacaoTomador>");
            sb.Append("<RazaoSocial>").Append(Esc(Convert.ToString(r["TOMADOR_RAZAO"]))).Append("</RazaoSocial>");
            sb.Append("</TomadorServico>");
            sb.Append("</InfNfse>");

            if (situacao == "C" && r["DT_CANCELAMENTO"] != DBNull.Value)
            {
                sb.Append("<NfseCancelamento><Confirmacao><DataHora>")
                  .Append(FmtData(Convert.ToDateTime(r["DT_CANCELAMENTO"])))
                  .Append("</DataHora></Confirmacao></NfseCancelamento>");
            }
            if (situacao == "S" && r["NUM_SUBSTITUIDORA"] != DBNull.Value)
            {
                sb.Append("<NfseSubstituicao><NfseSubstituidora>")
                  .Append(Convert.ToInt64(r["NUM_SUBSTITUIDORA"]))
                  .Append("</NfseSubstituidora></NfseSubstituicao>");
            }
            sb.Append("</Nfse></CompNfse>");
            return sb.ToString();
        }
    }
}
