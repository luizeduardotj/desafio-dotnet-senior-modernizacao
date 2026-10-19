using System.Text;
using System.Xml;

namespace Nfse.Fachada.Sombra;

/// <summary>
/// Caminhos XPath em que duas respostas divergem, sobre o XML canônico do <c>outputXML</c> (ou do envelope, quando
/// não há <c>outputXML</c>, como num Fault). Nenhum campo é ignorado (ADR-0009). Os caminhos só têm nomes e posições,
/// nunca valores: podem ir para o log.
/// </summary>
internal static class DiferencaXml
{
    public const int MaximoDeCaminhos = 10;

    public static IReadOnlyList<string> Caminhos(ReadOnlySpan<byte> legado, ReadOnlySpan<byte> novo)
    {
        XmlElement? a = Raiz(legado);
        XmlElement? b = Raiz(novo);
        if (a is null || b is null)
        {
            return a is null && b is null ? [] : ["/"];
        }

        var caminhos = new List<string>();
        if (a.Name != b.Name)
        {
            caminhos.Add("/");
        }
        else
        {
            Comparar(a, b, "/" + a.Name, caminhos);
        }

        return caminhos;
    }

    private static XmlElement? Raiz(ReadOnlySpan<byte> resposta)
    {
        string? saida = XmlSoap.Conteudo(resposta, "outputXML");
        XmlDocument? documento = saida is null ? XmlSoap.Carregar(resposta) : XmlSoap.Carregar(saida);
        return documento?.DocumentElement;
    }

    private static void Comparar(XmlElement a, XmlElement b, string caminho, List<string> caminhos)
    {
        foreach (string atributo in NomesDosAtributos(a).Union(NomesDosAtributos(b)).Order(StringComparer.Ordinal))
        {
            if (!string.Equals(a.GetAttribute(atributo), b.GetAttribute(atributo), StringComparison.Ordinal)
                || a.HasAttribute(atributo) != b.HasAttribute(atributo))
            {
                Adicionar(caminhos, caminho + "/@" + atributo);
            }
        }

        if (!string.Equals(TextoProprio(a), TextoProprio(b), StringComparison.Ordinal))
        {
            Adicionar(caminhos, caminho + "/text()");
        }

        List<XmlElement> filhosA = Filhos(a);
        List<XmlElement> filhosB = Filhos(b);
        var ocorrencias = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Math.Max(filhosA.Count, filhosB.Count) && caminhos.Count < MaximoDeCaminhos; i++)
        {
            XmlElement? filhoA = i < filhosA.Count ? filhosA[i] : null;
            XmlElement? filhoB = i < filhosB.Count ? filhosB[i] : null;
            string nome = (filhoA ?? filhoB)!.Name;
            int posicao = ocorrencias[nome] = ocorrencias.GetValueOrDefault(nome) + 1;
            string caminhoDoFilho = $"{caminho}/{nome}[{posicao}]";

            if (filhoA is null || filhoB is null || filhoA.Name != filhoB.Name)
            {
                Adicionar(caminhos, caminhoDoFilho);
            }
            else
            {
                Comparar(filhoA, filhoB, caminhoDoFilho, caminhos);
            }
        }
    }

    private static void Adicionar(List<string> caminhos, string caminho)
    {
        if (caminhos.Count < MaximoDeCaminhos)
        {
            caminhos.Add(caminho);
        }
    }

    private static IEnumerable<string> NomesDosAtributos(XmlElement elemento) =>
        elemento.Attributes.Cast<XmlAttribute>().Select(atributo => atributo.Name);

    private static List<XmlElement> Filhos(XmlElement elemento) => elemento.ChildNodes.OfType<XmlElement>().ToList();

    // Texto direto do elemento (sem o dos filhos), como o XML canônico o preserva.
    private static string TextoProprio(XmlElement elemento)
    {
        var texto = new StringBuilder();
        foreach (XmlNode no in elemento.ChildNodes)
        {
            if (no.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
            {
                texto.Append(no.Value);
            }
        }

        return texto.ToString();
    }
}
