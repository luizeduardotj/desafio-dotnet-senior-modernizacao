using System.Net;
using System.Security;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Nfse.Paridade.Testes;

/// <summary>
/// Golden master (ADR-0011): cada caso de leitura de <c>tests/paridade/casos</c> é enviado ao serviço novo e a
/// resposta tem de ser idêntica, byte a byte, à capturada do legado. Divergências só por decisão do catálogo.
/// </summary>
public sealed class ParidadeLeiturasTests : IClassFixture<ParidadeLeiturasTests.Fabrica>
{
    // Divergências esperadas: decisão consciente, com o ID do catálogo (ADR-0004). O legado continua no golden master;
    // o que o novo responde no lugar é conferido aqui, com o que prova que o legado tinha o defeito.
    private static readonly Dictionary<string, (string Motivo, string NoLegado, string NoNovo)> Esperadas = new(StringComparer.Ordinal)
    {
        ["023-c28-dtd-interna"] = ("C28: o legado expande a DTD interna; o novo recusa e responde E001",
            "&lt;CompNfse&gt;", "&lt;Codigo&gt;E001&lt;/Codigo&gt;"),
        ["112-c15-colisao-cpf-cnpj"] = ("C15: o legado devolve ao CPF as notas do CNPJ de mesmo número; o novo compara texto e responde E040",
            "&lt;Cnpj&gt;74579869109&lt;/Cnpj&gt;", "&lt;Codigo&gt;E040&lt;/Codigo&gt;"),
    };

    private readonly Fabrica _fabrica;

    public ParidadeLeiturasTests(Fabrica fabrica) => _fabrica = fabrica;

    public static TheoryData<string> Casos()
    {
        var casos = new TheoryData<string>();
        foreach (string pasta in Directory.GetDirectories(Path.Combine(RaizDoRepositorio(), "tests", "paridade", "casos")).Order(StringComparer.Ordinal))
        {
            if (File.ReadAllText(Path.Combine(pasta, "operacao")).Trim() is "ConsultarNfse" or "ConsultarNfseServicoTomado")
            {
                casos.Add(Path.GetFileName(pasta));
            }
        }

        return casos;
    }

    [ParidadeTheory]
    [MemberData(nameof(Casos))]
    public async Task Resposta_igual_ao_legado(string caso)
    {
        string pasta = Path.Combine(RaizDoRepositorio(), "tests", "paridade", "casos", caso);
        string operacao = File.ReadAllText(Path.Combine(pasta, "operacao")).Trim();
        string entrada = File.ReadAllText(Path.Combine(pasta, "entrada.xml")).TrimEnd('\n');
        string esperado = File.ReadAllText(Path.Combine(pasta, "legado.xml"));
        var statusEsperado = (HttpStatusCode)int.Parse(File.ReadAllText(Path.Combine(pasta, "legado.status")).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        // Mesmo envelope de tests/paridade/capturar.sh
        string envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>" +
            $"<{operacao}Request xmlns=\"http://notacarioca.rio.gov.br/\"><inputXML>" +
            entrada.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal) +
            $"</inputXML></{operacao}Request></soap:Body></soap:Envelope>";

        using var conteudo = new StringContent(envelope, Encoding.UTF8, "text/xml");
        using var pedido = new HttpRequestMessage(HttpMethod.Post, "/NfseWS.asmx") { Content = conteudo };
        pedido.Headers.Add("SOAPAction", $"\"http://notacarioca.rio.gov.br/{operacao}\"");
        using HttpResponseMessage resposta = await _fabrica.CreateClient().SendAsync(pedido);
        string obtido = await resposta.Content.ReadAsStringAsync();

        if (Esperadas.TryGetValue(caso, out var divergencia))
        {
            Assert.NotEqual(esperado, obtido);
            Assert.True(esperado.Contains(divergencia.NoLegado, StringComparison.Ordinal), $"{divergencia.Motivo}: legado.xml sem {divergencia.NoLegado}");
            Assert.True(obtido.Contains(divergencia.NoNovo, StringComparison.Ordinal), $"{divergencia.Motivo}: o novo respondeu {obtido}");
            Assert.DoesNotContain(divergencia.NoLegado, obtido, StringComparison.Ordinal);
            return;
        }

        Assert.Equal(statusEsperado, resposta.StatusCode);
        Assert.Equal(esperado, obtido);
    }

    private static string RaizDoRepositorio()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Nfse.Moderno.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }

    public sealed class Fabrica : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.UseSetting("ConnectionStrings:Oracle", Environment.GetEnvironmentVariable(ParidadeTheoryAttribute.Variavel) ?? "nao-configurada");
    }
}
