using System.Text.RegularExpressions;

namespace Nfse.Fachada.Testes;

/// <summary>Acesso aos arquivos de <c>tools/exemplos</c> copiados para a saída do teste.</summary>
internal static partial class Exemplos
{
    private static readonly string Pasta = Path.Combine(AppContext.BaseDirectory, "exemplos");

    public static byte[] Bytes(string arquivo) => File.ReadAllBytes(Path.Combine(Pasta, arquivo));

    public static IEnumerable<string> Pedidos() =>
        Directory.GetFiles(Pasta, "*.xml")
            .Select(Path.GetFileName)
            .Where(nome => !nome!.EndsWith(".resposta.xml", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)!;

    public static string? Resposta(string pedido)
    {
        string resposta = pedido[..^".xml".Length] + ".resposta.xml";
        return File.Exists(Path.Combine(Pasta, resposta)) ? resposta : null;
    }

    /// <summary>Mesma regra de <c>tools/exemplos/executar.sh</c>: o primeiro elemento <c>*Request</c> do arquivo.</summary>
    public static string Operacao(string pedido)
    {
        string texto = File.ReadAllText(Path.Combine(Pasta, pedido));
        return ElementoRequest().Match(texto).Groups[1].Value;
    }

    /// <summary>Header como o <c>executar.sh</c> envia: <c>"http://notacarioca.rio.gov.br/{op}"</c>, com aspas.</summary>
    public static string SoapAction(string operacao) => $"\"http://notacarioca.rio.gov.br/{operacao}\"";

    [GeneratedRegex("<([A-Za-z]*)Request")]
    private static partial Regex ElementoRequest();
}
