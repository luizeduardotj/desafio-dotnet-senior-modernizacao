using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using NotaCarioca.Servicos.Modelos;

namespace NotaCarioca.Servicos
{
    public class LoteLido
    {
        public long NumeroLote;
        public string Cnpj;
        public string InscricaoMunicipal;
        public int QuantidadeInformada;
        public List<RpsLote> Rps = new List<RpsLote>();
    }

    public static class LeitorLoteRps
    {
        public static XmlDocument Carregar(string xml)
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);
            return doc;
        }

        public static string Texto(XmlNode no, string caminho)
        {
            XmlNode n = no.SelectSingleNode(caminho);
            if (n == null) return null;
            return n.InnerText.Trim();
        }

        public static decimal Decimal(string s)
        {
            decimal d;
            if (s != null && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d)) return d;
            return -1m;
        }

        public static LoteLido LerCabecalho(XmlDocument doc)
        {
            XmlNode raiz = doc.DocumentElement;
            if (raiz == null || raiz.Name != "EnviarLoteRpsEnvio")
                throw new XmlException("Elemento raiz inesperado.");
            XmlNode lote = raiz.SelectSingleNode("LoteRps");
            if (lote == null)
                throw new XmlException("LoteRps ausente.");

            LoteLido l = new LoteLido();
            long numero;
            long.TryParse(Texto(lote, "NumeroLote") ?? "0", out numero);
            l.NumeroLote = numero;
            l.Cnpj = Texto(lote, "Prestador/Cnpj");
            l.InscricaoMunicipal = Texto(lote, "Prestador/InscricaoMunicipal");
            int q;
            int.TryParse(Texto(lote, "QuantidadeRps") ?? "0", out q);
            l.QuantidadeInformada = q;
            return l;
        }

        public static LoteLido Ler(XmlDocument doc)
        {
            LoteLido l = LerCabecalho(doc);
            XmlNodeList nos = doc.SelectNodes("/EnviarLoteRpsEnvio/LoteRps/ListaRps/Rps/InfRps");
            int i = 0;
            foreach (XmlNode no in nos)
            {
                RpsLote r = new RpsLote();
                r.Indice = i++;
                long num;
                long.TryParse(Texto(no, "IdentificacaoRps/Numero") ?? "0", out num);
                r.Numero = num;
                r.Serie = Texto(no, "IdentificacaoRps/Serie") ?? "";
                int tipo;
                int.TryParse(Texto(no, "IdentificacaoRps/Tipo") ?? "1", out tipo);
                r.Tipo = tipo;

                DateTime dt;
                if (!DateTime.TryParse(Texto(no, "DataEmissao"), CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                    dt = DateTime.Now;
                r.DataEmissao = dt;

                r.ValorServicos = Decimal(Texto(no, "Servico/Valores/ValorServicos"));
                string ded = Texto(no, "Servico/Valores/ValorDeducoes");
                r.ValorDeducoes = ded == null ? 0m : Decimal(ded);
                r.IssRetido = Texto(no, "Servico/Valores/IssRetido") == "1";
                r.ItemListaServico = Texto(no, "Servico/ItemListaServico");
                r.Discriminacao = Texto(no, "Servico/Discriminacao");

                string doc2 = Texto(no, "Tomador/IdentificacaoTomador/CpfCnpj/Cnpj");
                if (string.IsNullOrEmpty(doc2))
                    doc2 = Texto(no, "Tomador/IdentificacaoTomador/CpfCnpj/Cpf");
                r.TomadorDocumento = doc2;
                r.TomadorRazaoSocial = Texto(no, "Tomador/RazaoSocial");
                l.Rps.Add(r);
            }
            return l;
        }
    }
}
