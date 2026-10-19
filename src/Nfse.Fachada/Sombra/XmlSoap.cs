using System.Text;
using System.Xml;

namespace Nfse.Fachada.Sombra;

/// <summary>Leitura segura (sem DTD, sem resolução externa) dos envelopes SOAP copiados pela sombra.</summary>
internal static class XmlSoap
{
    private static readonly XmlReaderSettings Seguro = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
    };

    // O outputXML vem escapado no envelope: um código de retorno só existe se este trecho existir. Procurar os bytes
    // evita o parsing de uma página de notas no caminho do cliente.
    private static readonly byte[] MarcaDeMensagem = "ListaMensagemRetorno"u8.ToArray();

    /// <summary>Texto do primeiro elemento com o nome local dado (<c>inputXML</c>, <c>outputXML</c>), em qualquer namespace.</summary>
    public static string? Conteudo(ReadOnlySpan<byte> envelope, string nomeLocal)
    {
        try
        {
            using var leitor = XmlReader.Create(new StringReader(Encoding.UTF8.GetString(envelope)), Seguro);
            while (leitor.Read())
            {
                if (leitor.NodeType == XmlNodeType.Element && leitor.LocalName == nomeLocal)
                {
                    return leitor.ReadElementContentAsString();
                }
            }
        }
        catch (XmlException)
        {
        }

        return null;
    }

    /// <summary>Documento carregado sem DTD; <c>null</c> se não for XML bem formado.</summary>
    public static XmlDocument? Carregar(string xml)
    {
        try
        {
            var documento = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            using var leitor = XmlReader.Create(new StringReader(xml), Seguro);
            documento.Load(leitor);
            return documento;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    public static XmlDocument? Carregar(ReadOnlySpan<byte> xml) => Carregar(Encoding.UTF8.GetString(xml));

    /// <summary>Códigos de <c>ListaMensagemRetorno/MensagemRetorno/Codigo</c> do <c>outputXML</c> da resposta.</summary>
    public static IReadOnlyList<string> CodigosDeRetorno(ReadOnlySpan<byte> resposta)
    {
        if (resposta.IndexOf(MarcaDeMensagem) < 0 || Conteudo(resposta, "outputXML") is not { } saida || Carregar(saida) is not { } documento)
        {
            return [];
        }

        var codigos = new List<string>();
        foreach (XmlNode no in documento.GetElementsByTagName("Codigo"))
        {
            if (no.ParentNode?.Name == "MensagemRetorno")
            {
                codigos.Add(no.InnerText.Trim());
            }
        }

        return codigos;
    }

    /// <summary><c>faultcode</c> e <c>faultstring</c> de um <c>soap:Fault</c>; <c>null</c> se a resposta não for um Fault.</summary>
    public static (string Codigo, string Texto)? Fault(ReadOnlySpan<byte> resposta)
    {
        if (Carregar(resposta)?.DocumentElement is not { } envelope)
        {
            return null;
        }

        var ns = new XmlNamespaceManager(envelope.OwnerDocument.NameTable);
        ns.AddNamespace("soap", "http://schemas.xmlsoap.org/soap/envelope/");
        XmlNode? fault = envelope.SelectSingleNode("/soap:Envelope/soap:Body/soap:Fault", ns);
        if (fault is null)
        {
            return null;
        }

        return (fault.SelectSingleNode("faultcode")?.InnerText.Trim() ?? string.Empty,
            fault.SelectSingleNode("faultstring")?.InnerText ?? string.Empty);
    }
}
