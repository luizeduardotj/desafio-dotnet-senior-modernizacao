using System.Xml;

namespace Nfse.Fachada.Sombra;

/// <summary>
/// C18/K04 (ADR-0004): o legado identifica o prestador só pela IM e ignora o CNPJ do pedido. Antes de decidir se uma
/// validação futura quebraria ERPs, medimos quantos pedidos trazem um CNPJ diferente do cadastrado para a IM
/// (<c>soap_cnpj_inconsistente_total</c>). Nada aqui consulta o banco: no <c>ConsultarNfse</c> o próprio legado devolve
/// o CNPJ do cadastro em cada nota (<c>PrestadorServico/IdentificacaoPrestador/Cnpj</c> vem do join com
/// <c>CONTRIBUINTES.DOCUMENTO</c>); no <c>CancelarNfse</c> a resposta só ecoa o pedido (K10), e quem confere é o novo,
/// que já lê o cadastro na avaliação da sombra. O documento nunca sai daqui: só o resultado.
/// </summary>
internal static class ConferenciaDeCnpj
{
    private static readonly XmlReaderSettings Seguro = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
    };

    /// <returns>
    /// <c>true</c> se o CNPJ do pedido é o do cadastro, <c>false</c> se difere, <c>null</c> se não há o que conferir: pedido
    /// sem CNPJ, resposta sem nota (E010, E040, Fault) ou XML que não se lê.
    /// </returns>
    public static bool? DaConsulta(ReadOnlySpan<byte> requisicao, ReadOnlySpan<byte> resposta)
    {
        if (XmlSoap.Conteudo(requisicao, "inputXML") is not { } entrada
            || XmlSoap.Carregar(entrada)?.DocumentElement is not { Name: "ConsultarNfseEnvio" } raiz)
        {
            return null;
        }

        string? informado = raiz.SelectSingleNode("Prestador/Cnpj")?.InnerText;
        return string.IsNullOrWhiteSpace(informado) ? null : Conferir(informado, CnpjDoCadastro(resposta));
    }

    /// <summary>Iguais só nos dígitos e sem zeros à esquerda, como no novo: um ERP que omite o zero inicial não diverge.</summary>
    public static bool? Conferir(string? informado, string? cadastrado)
    {
        string a = Normalizar(informado);
        string b = Normalizar(cadastrado);
        return a.Length == 0 || b.Length == 0 ? null : a == b;
    }

    /// <summary>
    /// CNPJ do prestador na primeira nota da página. Todas as notas são do mesmo prestador (a consulta filtra pelo ID
    /// achado pela IM), então a primeira basta; o leitor para ali e não percorre a página.
    /// </summary>
    private static string? CnpjDoCadastro(ReadOnlySpan<byte> resposta)
    {
        if (XmlSoap.Conteudo(resposta, "outputXML") is not { } saida)
        {
            return null;
        }

        try
        {
            using var leitor = XmlReader.Create(new StringReader(saida), Seguro);
            return leitor.ReadToFollowing("IdentificacaoPrestador") && leitor.ReadToDescendant("Cnpj")
                ? leitor.ReadElementContentAsString()
                : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string Normalizar(string? documento) =>
        string.Concat((documento ?? "").Where(char.IsAsciiDigit)).TrimStart('0');
}
