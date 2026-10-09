using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace GeradorLote
{
    // Gera um EnviarLoteRpsEnvio com N RPS (valores e tomadores sinteticos).
    // uso: dotnet run -- <qtd> [--im 10000025] [--serie L1] [--inicio 1] [--lote 1] [--saida lote.xml]
    public class Program
    {
        private static readonly string[] Itens = {
            "01.01", "01.02", "01.03", "01.07", "02.01", "04.01", "04.03", "05.01", "06.01", "07.02",
            "07.05", "08.01", "09.01", "10.02", "11.02", "13.02", "14.01", "16.01", "17.01", "17.06"
        };

        public static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("uso: GeradorLote <qtd> [--im 10000025] [--serie L1] [--inicio 1] [--lote 1] [--saida lote.xml]");
                return 1;
            }

            int qtd = int.Parse(args[0], CultureInfo.InvariantCulture);
            string im = "10000025";
            string serie = null;
            long inicio = 1;
            long numeroLote = 1;
            string saida = "lote.xml";
            for (int i = 1; i < args.Length - 1; i += 2)
            {
                switch (args[i])
                {
                    case "--im": im = args[i + 1]; break;
                    case "--serie": serie = args[i + 1]; break;
                    case "--inicio": inicio = long.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                    case "--lote": numeroLote = long.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                    case "--saida": saida = args[i + 1]; break;
                }
            }

            Random rnd = new Random();
            if (serie == null)
            {
                const string alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
                StringBuilder sb = new StringBuilder("G");
                for (int i = 0; i < 4; i++) sb.Append(alfabeto[rnd.Next(alfabeto.Length)]);
                serie = sb.ToString();
            }

            string[] tomadores = new string[40];
            for (int i = 0; i < tomadores.Length; i++)
            {
                tomadores[i] = i % 4 == 3
                    ? CpfComDv((100000000 + (i * 7654321L) % 899999999L).ToString(CultureInfo.InvariantCulture))
                    : CnpjComDv((10000000 + (i * 1234567L) % 89999999L).ToString(CultureInfo.InvariantCulture) + "0001");
            }

            Random vr = new Random(12345);
            XmlWriterSettings cfg = new XmlWriterSettings();
            cfg.Indent = true;
            cfg.IndentChars = "  ";
            cfg.Encoding = new UTF8Encoding(false);
            DateTime agora = DateTime.Now;

            using (XmlWriter w = XmlWriter.Create(saida, cfg))
            {
                w.WriteStartDocument();
                w.WriteStartElement("EnviarLoteRpsEnvio");
                w.WriteStartElement("LoteRps");
                w.WriteAttributeString("Id", "lote" + numeroLote);
                w.WriteElementString("NumeroLote", numeroLote.ToString(CultureInfo.InvariantCulture));
                w.WriteStartElement("Prestador");
                w.WriteElementString("Cnpj", "00000000000000");
                w.WriteElementString("InscricaoMunicipal", im);
                w.WriteEndElement();
                w.WriteElementString("QuantidadeRps", qtd.ToString(CultureInfo.InvariantCulture));
                w.WriteStartElement("ListaRps");
                for (int i = 0; i < qtd; i++)
                {
                    long numero = inicio + i;
                    decimal valor = 100 + vr.Next(0, 2000000) / 100m;
                    decimal ded = (i % 10 == 0) ? Math.Round(valor * 0.1m, 2) : 0m;
                    string tomador = tomadores[vr.Next(tomadores.Length)];
                    w.WriteStartElement("Rps");
                    w.WriteStartElement("InfRps");
                    w.WriteAttributeString("Id", "rps" + numero);
                    w.WriteStartElement("IdentificacaoRps");
                    w.WriteElementString("Numero", numero.ToString(CultureInfo.InvariantCulture));
                    w.WriteElementString("Serie", serie);
                    w.WriteElementString("Tipo", "1");
                    w.WriteEndElement();
                    w.WriteElementString("DataEmissao", agora.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
                    w.WriteStartElement("Servico");
                    w.WriteStartElement("Valores");
                    w.WriteElementString("ValorServicos", valor.ToString("0.00", CultureInfo.InvariantCulture));
                    w.WriteElementString("ValorDeducoes", ded.ToString("0.00", CultureInfo.InvariantCulture));
                    w.WriteElementString("IssRetido", (i % 7 == 0) ? "1" : "2");
                    w.WriteEndElement();
                    w.WriteElementString("ItemListaServico", Itens[vr.Next(Itens.Length)]);
                    w.WriteElementString("Discriminacao", Discriminacao(numero));
                    w.WriteEndElement();
                    w.WriteStartElement("Tomador");
                    w.WriteStartElement("IdentificacaoTomador");
                    w.WriteStartElement("CpfCnpj");
                    w.WriteElementString(tomador.Length == 14 ? "Cnpj" : "Cpf", tomador);
                    w.WriteEndElement();
                    w.WriteEndElement();
                    w.WriteElementString("RazaoSocial", "TOMADOR SINTETICO " + tomador.Substring(0, 6));
                    w.WriteEndElement();
                    w.WriteEndElement();
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndDocument();
            }

            long bytes = new FileInfo(saida).Length;
            Console.WriteLine("lote gerado: {0} RPS, serie {1}, numeros {2}..{3}, {4:0.0} MB -> {5}",
                qtd, serie, inicio, inicio + qtd - 1, bytes / 1048576.0, saida);
            return 0;
        }

        private static string Discriminacao(long numero)
        {
            return "Prestacao de servicos conforme contrato e ordem de servico " + numero.ToString(CultureInfo.InvariantCulture) +
                   ". Atividades executadas no periodo, incluindo levantamento de requisitos, execucao, acompanhamento, " +
                   "relatorios gerenciais, suporte presencial e remoto, treinamento da equipe do tomador, visitas tecnicas " +
                   "e demais atividades previstas no escopo contratado, conforme detalhamento anexo a ordem de servico. " +
                   "Valores ja consideram materiais e deslocamentos quando aplicavel. Documento emitido para fins de teste.";
        }

        private static string CnpjComDv(string base12)
        {
            int[] p1 = { 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 };
            int[] p2 = { 6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 };
            int s = 0;
            for (int i = 0; i < 12; i++) s += (base12[i] - '0') * p1[i];
            int d1 = s % 11 < 2 ? 0 : 11 - s % 11;
            s = 0;
            for (int i = 0; i < 12; i++) s += (base12[i] - '0') * p2[i];
            s += d1 * p2[12];
            int d2 = s % 11 < 2 ? 0 : 11 - s % 11;
            return base12 + d1 + d2;
        }

        private static string CpfComDv(string base9)
        {
            int s = 0;
            for (int i = 0; i < 9; i++) s += (base9[i] - '0') * (10 - i);
            int d1 = (s * 10) % 11;
            if (d1 == 10) d1 = 0;
            s = 0;
            for (int i = 0; i < 9; i++) s += (base9[i] - '0') * (11 - i);
            s += d1 * 2;
            int d2 = (s * 10) % 11;
            if (d2 == 10) d2 = 0;
            return base9 + d1 + d2;
        }
    }
}
