using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nfse.Host.Testes.Contrato;

/// <summary>
/// Contrato da REST <c>/api/v1</c> (ADR-0010): o documento OpenAPI gerado pelo Host tem de ser igual ao snapshot versionado
/// em <c>openapi-v1.json</c>. Mudança intencional: rode com <c>NFSE_ATUALIZAR_CONTRATO=1</c> para reescrever o snapshot
/// e revise o diff no PR.
/// </summary>
[Trait("Categoria", "Contrato")]
public class ContratoOpenApiTests(FabricaDoHost fabrica) : IClassFixture<FabricaDoHost>
{
    private const string Regenerar = "NFSE_ATUALIZAR_CONTRATO=1 dotnet test tests/Nfse.Host.Testes --filter Categoria=Contrato";

    private static readonly JsonSerializerOptions Indentado = new() { WriteIndented = true };

    [Fact]
    public async Task OpenApi_v1_igual_ao_snapshot()
    {
        string gerado = Normalizar(await fabrica.CreateClient().GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)));
        string arquivo = CaminhoDoSnapshot();

        if (Environment.GetEnvironmentVariable("NFSE_ATUALIZAR_CONTRATO") == "1")
        {
            await File.WriteAllTextAsync(arquivo, gerado);
            return;
        }

        Assert.True(File.Exists(arquivo), $"snapshot do contrato não encontrado em {arquivo}; gere com: {Regenerar}");
        string esperado = Normalizar(await File.ReadAllTextAsync(arquivo));
        Assert.True(esperado == gerado,
            $"o contrato /api/v1 mudou; se foi intencional, regenere o snapshot com: {Regenerar}\n{PrimeiraDiferenca(esperado, gerado)}");
    }

    /// <summary>Chaves em ordem ordinal, sem <c>servers</c> (depende do host que atendeu) e com fim de linha <c>\n</c>.</summary>
    internal static string Normalizar(string json)
    {
        JsonNode? raiz = JsonNode.Parse(json);
        if (raiz is JsonObject objeto)
        {
            objeto.Remove("servers");
        }

        return Ordenar(raiz)?.ToJsonString(Indentado).ReplaceLineEndings("\n") + "\n";
    }

    private static JsonNode? Ordenar(JsonNode? no) => no switch
    {
        JsonObject objeto => new JsonObject(objeto
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Ordenar(p.Value)))),
        JsonArray lista => new JsonArray([.. lista.Select(Ordenar)]),
        _ => no?.DeepClone(),
    };

    private static string PrimeiraDiferenca(string esperado, string gerado)
    {
        string[] a = esperado.Split('\n');
        string[] b = gerado.Split('\n');
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            string? linhaA = i < a.Length ? a[i] : null;
            string? linhaB = i < b.Length ? b[i] : null;
            if (linhaA != linhaB)
            {
                return $"primeira diferença na linha {i + 1}:\n  snapshot: {linhaA}\n  gerado:   {linhaB}";
            }
        }

        return "";
    }

    // o snapshot fica no fonte, não na saída do build: é ele que o PR versiona e revisa
    private static string CaminhoDoSnapshot([CallerFilePath] string fonte = "") =>
        Path.Combine(Path.GetDirectoryName(fonte)!, "openapi-v1.json");
}
