using System.Text;
using System.Xml;

namespace Nfse.Fachada.Roteamento;

/// <summary>
/// Extrai a chave de roteamento de dentro do <c>inputXML</c> escapado, lendo os mesmos caminhos que o legado lê
/// (<c>ConsultaNfseServico.cs</c>, <c>CancelamentoNfseServico.cs</c>), mas com parsing seguro: sem DTD e sem resolução
/// externa. Qualquer falha devolve <c>null</c> e a decisão trata como chave ausente (legado, salvo novo 100%).
/// </summary>
/// <remarks>A chave pode ser documento de pessoa: só existe em memória para o hash e as listas; nunca vai para log.</remarks>
public static class ExtratorDeChave
{
    private static readonly XmlReaderSettings Seguro = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
    };

    public static string? Extrair(string operacao, ReadOnlySpan<byte> envelope)
    {
        try
        {
            // O legado decodifica o corpo como UTF-8 e ignora a declaração de encoding (Program.cs:63).
            string? entrada = LerInputXml(Encoding.UTF8.GetString(envelope));
            if (string.IsNullOrEmpty(entrada))
            {
                return null;
            }

            XmlElement? raiz = Carregar(entrada);
            if (raiz is null)
            {
                return null;
            }

            return operacao switch
            {
                OperacoesSoap.ConsultarNfse => SeRaiz(raiz, "ConsultarNfseEnvio", "Prestador/InscricaoMunicipal"),
                OperacoesSoap.CancelarNfse => SeRaiz(raiz, "CancelarNfseEnvio", "Pedido/InfPedidoCancelamento/IdentificacaoNfse/InscricaoMunicipal"),
                OperacoesSoap.ConsultarNfseServicoTomado => DocumentoDoConsulente(raiz),
                _ => null,
            };
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>C22: o primeiro elemento com nome local <c>inputXML</c>, em qualquer namespace.</summary>
    private static string? LerInputXml(string envelope)
    {
        using var leitor = XmlReader.Create(new StringReader(envelope), Seguro);
        while (leitor.Read())
        {
            if (leitor.NodeType == XmlNodeType.Element && leitor.LocalName == "inputXML")
            {
                return leitor.ReadElementContentAsString();
            }
        }

        return null;
    }

    private static XmlElement? Carregar(string xml)
    {
        var documento = new XmlDocument { XmlResolver = null };
        using var leitor = XmlReader.Create(new StringReader(xml), Seguro);
        documento.Load(leitor);
        return documento.DocumentElement;
    }

    // C23/C29: caminhos sem namespace, nome da raiz exato, InnerText.Trim().
    private static string? SeRaiz(XmlElement raiz, string nomeRaiz, string caminho) =>
        raiz.Name == nomeRaiz ? Texto(raiz, caminho) : null;

    private static string? DocumentoDoConsulente(XmlElement raiz)
    {
        if (raiz.Name != "ConsultarNfseServicoTomadoEnvio")
        {
            return null;
        }

        string? documento = Documento(raiz.SelectSingleNode("Consulente"));
        if (string.IsNullOrEmpty(documento))
        {
            documento = Documento(raiz.SelectSingleNode("Tomador"));
        }

        if (string.IsNullOrEmpty(documento))
        {
            return null;
        }

        string digitos = string.Concat(documento.Where(char.IsAsciiDigit));
        return digitos.Length == 0 ? null : digitos;
    }

    private static string? Documento(XmlNode? no)
    {
        if (no is null)
        {
            return null;
        }

        string? documento = Texto(no, "CpfCnpj/Cnpj");
        return string.IsNullOrEmpty(documento) ? Texto(no, "CpfCnpj/Cpf") : documento;
    }

    private static string? Texto(XmlNode no, string caminho)
    {
        string? texto = no.SelectSingleNode(caminho)?.InnerText.Trim();
        return string.IsNullOrEmpty(texto) ? null : texto;
    }
}
