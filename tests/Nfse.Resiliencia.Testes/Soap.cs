namespace Nfse.Resiliencia.Testes;

/// <summary>Requisições SOAP do cenário. O envelope é o de <c>tests/paridade/capturar.sh</c>.</summary>
public sealed record Soap(string Operacao, string Envelope)
{
    public const string ConsultarNfseOp = "ConsultarNfse";
    public const string CancelarNfseOp = "CancelarNfse";

    /// <summary>Caso 001 do golden master (exemplo 04): a resposta tem de ser a do legado, byte a byte.</summary>
    public static Soap ConsultarNfse() => DoCaso("001-exemplo-04-consultar-nfse");

    public static string RespostaDoLegado(string caso = "001-exemplo-04-consultar-nfse") =>
        File.ReadAllText(Path.Combine(Repositorio.Raiz, "tests", "paridade", "casos", caso, "legado.xml"));

    /// <summary>Prestador 25 (IM 10000025): notas na competência do mês da carga, com o prazo aberto pelo relógio real.</summary>
    public const int Prestador = 25;

    /// <summary>Nota 99999 do prestador 25: E050 sem escrever, serve de sonda do roteamento do cancelamento.</summary>
    public static Soap CancelarInexistente() => Cancelar(99999);

    public static Soap Cancelar(long numero) => Montar(
        CancelarNfseOp,
        "<CancelarNfseEnvio><Pedido><InfPedidoCancelamento><IdentificacaoNfse>" +
        $"<Numero>{numero}</Numero><Cnpj>13500075000183</Cnpj><InscricaoMunicipal>10000025</InscricaoMunicipal>" +
        "</IdentificacaoNfse><CodigoCancelamento>1</CodigoCancelamento>" +
        "<Motivo>Teste de resiliencia</Motivo></InfPedidoCancelamento></Pedido></CancelarNfseEnvio>");

    private static Soap DoCaso(string caso)
    {
        string pasta = Path.Combine(Repositorio.Raiz, "tests", "paridade", "casos", caso);
        return Montar(File.ReadAllText(Path.Combine(pasta, "operacao")).Trim(), File.ReadAllText(Path.Combine(pasta, "entrada.xml")).TrimEnd('\n'));
    }

    private static Soap Montar(string operacao, string entrada) => new(
        operacao,
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>" +
        $"<{operacao}Request xmlns=\"http://notacarioca.rio.gov.br/\"><inputXML>" +
        entrada.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal) +
        $"</inputXML></{operacao}Request></soap:Body></soap:Envelope>");
}
