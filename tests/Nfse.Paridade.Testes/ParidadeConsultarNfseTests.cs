using System.Net;
using System.Security;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Nfse.Paridade.Testes;

/// <summary>
/// Golden master (ADR-0011): cada caso de <c>tests/paridade/casos</c> da operação é enviado ao serviço novo e a
/// resposta tem de ser idêntica, byte a byte, à capturada do legado. Divergências só por decisão do catálogo.
/// </summary>
public sealed class ParidadeConsultarNfseTests : IClassFixture<ParidadeConsultarNfseTests.Fabrica>
{
    // Divergências esperadas: decisão consciente, com o ID do catálogo (ADR-0004).
    private static readonly Dictionary<string, string> Esperadas = new(StringComparer.Ordinal)
    {
        ["023-c28-dtd-interna"] = "C28: o legado expande a DTD interna; o novo recusa e responde E001",
    };

    private readonly Fabrica _fabrica;

    public ParidadeConsultarNfseTests(Fabrica fabrica) => _fabrica = fabrica;

    public static TheoryData<string> Casos()
    {
        var casos = new TheoryData<string>();
        foreach (string pasta in Directory.GetDirectories(Path.Combine(RaizDoRepositorio(), "tests", "paridade", "casos")).Order(StringComparer.Ordinal))
        {
            if (File.ReadAllText(Path.Combine(pasta, "operacao")).Trim() == "ConsultarNfse")
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
        string entrada = File.ReadAllText(Path.Combine(pasta, "entrada.xml")).TrimEnd('\n');
        string esperado = File.ReadAllText(Path.Combine(pasta, "legado.xml"));
        var statusEsperado = (HttpStatusCode)int.Parse(File.ReadAllText(Path.Combine(pasta, "legado.status")).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        // Mesmo envelope de tests/paridade/capturar.sh
        string envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>" +
            "<ConsultarNfseRequest xmlns=\"http://notacarioca.rio.gov.br/\"><inputXML>" +
            entrada.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal) +
            "</inputXML></ConsultarNfseRequest></soap:Body></soap:Envelope>";

        using var conteudo = new StringContent(envelope, Encoding.UTF8, "text/xml");
        using var pedido = new HttpRequestMessage(HttpMethod.Post, "/NfseWS.asmx") { Content = conteudo };
        pedido.Headers.Add("SOAPAction", "\"http://notacarioca.rio.gov.br/ConsultarNfse\"");
        using HttpResponseMessage resposta = await _fabrica.CreateClient().SendAsync(pedido);
        string obtido = await resposta.Content.ReadAsStringAsync();

        if (Esperadas.TryGetValue(caso, out string? motivo))
        {
            Assert.NotEqual(esperado, obtido);
            Assert.Contains("&lt;Codigo&gt;E001&lt;/Codigo&gt;", obtido, StringComparison.Ordinal);
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
