using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Nfse.Fachada.Sombra;

/// <summary>
/// Requisição pseudonimizada para o registro da divergência (ADR-0009, seção 4; ADR-0012): o <c>inputXML</c> com o
/// texto de cada elemento trocado por <c>hmac:</c> + HMAC-SHA256 em hexadecimal.
/// </summary>
/// <remarks>
/// A regra é por lista de permissão, não de bloqueio: só número da nota, período e página ficam em claro. CPF/CNPJ,
/// razões sociais, IM e qualquer campo que o pedido venha a ter saem pseudonimizados. O mesmo valor dá sempre o mesmo
/// HMAC com a mesma chave, o que permite reproduzir com o seed sem expor o dado.
/// </remarks>
internal static class Pseudonimizador
{
    private static readonly HashSet<string> EmClaro = new(StringComparer.Ordinal)
    {
        "Numero", "NumeroNfse", "DataInicial", "DataFinal", "Pagina",
    };

    /// <returns>
    /// O <c>inputXML</c> pseudonimizado; <c>null</c> sem chave, ou se o pedido não for XML bem formado (com DTD, por
    /// exemplo): sem estrutura não há como separar o dado pessoal, e nada sai.
    /// </returns>
    public static string? Requisicao(ReadOnlySpan<byte> envelope, string? chave)
    {
        if (string.IsNullOrEmpty(chave) || XmlSoap.Conteudo(envelope, "inputXML") is not { } entrada || XmlSoap.Carregar(entrada) is not { } documento)
        {
            return null;
        }

        byte[] bytesDaChave = Encoding.UTF8.GetBytes(chave);
        Substituir(documento.DocumentElement!, bytesDaChave);
        return documento.DocumentElement!.OuterXml;
    }

    public static string Hmac(string valor, byte[] chave) =>
        "hmac:" + Convert.ToHexStringLower(HMACSHA256.HashData(chave, Encoding.UTF8.GetBytes(valor)));

    private static void Substituir(XmlElement elemento, byte[] chave)
    {
        foreach (XmlAttribute atributo in elemento.Attributes)
        {
            if (!atributo.Name.StartsWith("xmlns", StringComparison.Ordinal))
            {
                atributo.Value = Hmac(atributo.Value, chave);
            }
        }

        foreach (XmlNode filho in elemento.ChildNodes.Cast<XmlNode>().ToList())
        {
            switch (filho)
            {
                case XmlElement neto:
                    Substituir(neto, chave);
                    break;
                case XmlCharacterData texto when filho.NodeType is XmlNodeType.Text or XmlNodeType.CDATA:
                    if (!EmClaro.Contains(elemento.LocalName) && texto.Value is { Length: > 0 } valor)
                    {
                        texto.Value = Hmac(valor.Trim(), chave);
                    }

                    break;
                case XmlComment comentario:
                    elemento.RemoveChild(comentario);
                    break;
            }
        }
    }
}
