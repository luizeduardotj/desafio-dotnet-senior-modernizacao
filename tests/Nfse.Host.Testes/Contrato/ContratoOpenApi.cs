using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NJsonSchema;
using NJsonSchema.Validation;

namespace Nfse.Host.Testes.Contrato;

/// <summary>
/// Confere uma resposta real da <c>/api/v1</c> contra o snapshot versionado <c>openapi-v1.json</c> (ADR-0010): a operação
/// existe, o status está documentado, o media type é um dos declarados e o corpo valida contra o schema da resposta.
/// </summary>
/// <remarks>
/// Validador: NJsonSchema (MIT, versão fixada em <c>src/Directory.Packages.props</c>). O JsonSchema.Net cobre o draft
/// 2020-12 inteiro, mas as versões atuais saíram do MIT para a EULA da Open Source Maintenance Fee (taxa para uso comercial),
/// e as antigas em MIT não recebem correção. O NJsonSchema implementa os drafts 4–7, o que basta: o documento do
/// Microsoft.AspNetCore.OpenApi 10 (OpenAPI 3.1) só usa palavras-chave que existem desde o draft 4 — <c>type</c> (inclusive
/// em lista, como <c>["null","string"]</c>), <c>properties</c>, <c>required</c>, <c>items</c>, <c>additionalProperties</c>,
/// <c>oneOf</c>, <c>pattern</c>, <c>format</c> e <c>$ref</c>. A única adaptação é apontar os <c>$ref</c> de
/// <c>#/components/schemas/</c> para <c>#/definitions/</c>. Se o spec passar a usar palavra-chave só do 2020-12
/// (<c>prefixItems</c>, <c>$defs</c>, <c>unevaluatedProperties</c>...), <see cref="SoDraft4a7"/> falha e obriga a rever a escolha.
/// </remarks>
internal sealed class ContratoOpenApi
{
    private const string PrefixoOpenApi = "#/components/schemas/";
    private const string PrefixoDefinitions = "#/definitions/";

    private static readonly string[] SoDraft2020 =
        ["prefixItems", "$defs", "$dynamicRef", "$dynamicAnchor", "unevaluatedProperties", "unevaluatedItems", "dependentSchemas", "dependentRequired", "const", "contains", "if"];

    private readonly JsonObject _caminhos;
    private readonly JsonObject _definicoes;
    private readonly List<(string Modelo, Regex Padrao)> _modelos;

    private ContratoOpenApi(JsonObject documento)
    {
        ApontarRefsParaDefinitions(documento);
        _caminhos = documento["paths"]!.AsObject();
        _definicoes = documento["components"]!["schemas"]!.AsObject();
        _modelos = [.. _caminhos.Select(c => (c.Key, ParaRegex(c.Key)))];
    }

    /// <summary>O contrato versionado no fonte (o mesmo que <see cref="ContratoOpenApiTests"/> compara com o gerado).</summary>
    public static ContratoOpenApi Snapshot { get; } = Carregar(File.ReadAllText(CaminhoDoSnapshot()));

    public static ContratoOpenApi Carregar(string json) => new(JsonNode.Parse(json)!.AsObject());

    /// <summary>Palavras-chave do spec que o NJsonSchema (drafts 4–7) não validaria.</summary>
    public IReadOnlyList<string> SoDraft4a7() =>
        [.. Chaves(_definicoes).Concat(Chaves(_caminhos)).Where(SoDraft2020.Contains).Distinct()];

    /// <summary>Falha o teste se a resposta não for uma das documentadas para a operação chamada.</summary>
    public async Task ValidarAsync(HttpResponseMessage resposta)
    {
        HttpRequestMessage pedido = resposta.RequestMessage ?? throw new ArgumentException("Resposta sem o pedido.", nameof(resposta));
        string caminho = pedido.RequestUri!.IsAbsoluteUri ? pedido.RequestUri.AbsolutePath : pedido.RequestUri.OriginalString.Split('?')[0];
        string metodo = pedido.Method.Method.ToLowerInvariant();
        int status = (int)resposta.StatusCode;
        string onde = $"{pedido.Method} {caminho} → {status}";

        string? modelo = _modelos.Where(m => m.Padrao.IsMatch(caminho)).Select(m => m.Modelo).FirstOrDefault();
        Assert.True(modelo is not null, $"{onde}: caminho fora do contrato");
        JsonNode? operacao = _caminhos[modelo!]![metodo];
        Assert.True(operacao is not null, $"{onde}: método não documentado em {modelo}");

        JsonObject respostas = operacao!["responses"]!.AsObject();
        JsonNode? documentada = respostas[status.ToString(System.Globalization.CultureInfo.InvariantCulture)] ?? respostas["default"];
        Assert.True(documentada is not null, $"{onde}: status não documentado (documentados: {string.Join(", ", respostas.Select(r => r.Key))})");

        string corpo = await resposta.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (documentada!["content"] is not JsonObject conteudos)
        {
            Assert.True(corpo.Length == 0, $"{onde}: o contrato não declara corpo, mas veio: {corpo}");
            return;
        }

        string? tipo = resposta.Content.Headers.ContentType?.MediaType;
        JsonNode? declarado = tipo is null ? null : conteudos[tipo];
        Assert.True(declarado is not null, $"{onde}: media type '{tipo}' não declarado (declarados: {string.Join(", ", conteudos.Select(c => c.Key))})");

        ICollection<ValidationError> erros = await ErrosAsync(declarado!["schema"]!, corpo).ConfigureAwait(false);
        Assert.True(erros.Count == 0, $"{onde}: corpo fora do schema\n{Descrever(erros)}\ncorpo: {corpo}");
    }

    /// <summary>Erros de <paramref name="corpo"/> contra o schema <paramref name="nome"/> de <c>components.schemas</c>.</summary>
    public Task<ICollection<ValidationError>> ErrosAsync(string nome, string corpo) =>
        ErrosAsync(new JsonObject { ["$ref"] = PrefixoDefinitions + nome }, corpo);

    private async Task<ICollection<ValidationError>> ErrosAsync(JsonNode schema, string corpo)
    {
        // allOf em vez de $ref na raiz: no draft 4–7, irmãos de $ref são ignorados, e definitions é irmão aqui
        var raiz = new JsonObject
        {
            ["allOf"] = new JsonArray(schema.DeepClone()),
            ["definitions"] = _definicoes.DeepClone(),
        };
        JsonSchema compilado = await JsonSchema.FromJsonAsync(raiz.ToJsonString()).ConfigureAwait(false);
        return compilado.Validate(corpo);
    }

    private static string Descrever(IEnumerable<ValidationError> erros)
    {
        var texto = new StringBuilder();
        foreach (ValidationError erro in erros)
        {
            texto.Append("  ").Append(erro.Path ?? "#").Append(": ").Append(erro.Kind);
            if (erro is ChildSchemaValidationError filho)
            {
                foreach (ValidationError interno in filho.Errors.SelectMany(e => e.Value))
                {
                    texto.Append("; ").Append(interno.Path ?? "#").Append(": ").Append(interno.Kind);
                }
            }

            texto.Append('\n');
        }

        return texto.ToString();
    }

    private static void ApontarRefsParaDefinitions(JsonNode? no)
    {
        switch (no)
        {
            case JsonObject objeto:
                if (objeto["$ref"]?.GetValue<string>() is { } referencia && referencia.StartsWith(PrefixoOpenApi, StringComparison.Ordinal))
                {
                    objeto["$ref"] = PrefixoDefinitions + referencia[PrefixoOpenApi.Length..];
                }

                foreach (KeyValuePair<string, JsonNode?> filho in objeto.ToList())
                {
                    ApontarRefsParaDefinitions(filho.Value);
                }

                break;
            case JsonArray lista:
                foreach (JsonNode? item in lista)
                {
                    ApontarRefsParaDefinitions(item);
                }

                break;
        }
    }

    private static IEnumerable<string> Chaves(JsonNode? no) => no switch
    {
        JsonObject objeto => objeto.SelectMany(p => Chaves(p.Value).Prepend(p.Key)),
        JsonArray lista => lista.SelectMany(Chaves),
        _ => [],
    };

    private static Regex ParaRegex(string modelo) =>
        new("^" + Regex.Replace(Regex.Escape(modelo), @"\\\{[^}]+}", "[^/]+") + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static string CaminhoDoSnapshot([CallerFilePath] string fonte = "") =>
        Path.Combine(Path.GetDirectoryName(fonte)!, "openapi-v1.json");
}
