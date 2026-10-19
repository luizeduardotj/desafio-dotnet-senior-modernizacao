using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Nfse.Paridade.Testes;

/// <summary>
/// Endpoint interno da sombra do cancelamento contra o Oracle real, pelo usuário NFSE_MODERNO_RO. Usa os casos de
/// negativa do corpus, que não dependem do estado mutável nem do relógio.
/// </summary>
public sealed class SombraDoCancelamentoOracleTests : IClassFixture<SombraDoCancelamentoOracleTests.Fabrica>
{
    private readonly Fabrica _fabrica;

    public SombraDoCancelamentoOracleTests(Fabrica fabrica) => _fabrica = fabrica;

    [SombraOracleTheory]
    [InlineData("202-exemplo-06e-inexistente")]
    [InlineData("203-exemplo-06d-substituida")]
    [InlineData("204-exemplo-06c-prazo")]
    [InlineData("205-k02-prazo-antes-de-guia-paga")]
    [InlineData("207-e010-prestador-inexistente")]
    [InlineData("208-e001-numero-nao-numerico")]
    public async Task Avaliacao_sem_escrita_coincide_com_o_legado(string caso)
    {
        string pasta = Path.Combine(Corpus.Raiz(), "tests", "paridade", "casos", caso);
        string entrada = File.ReadAllText(Path.Combine(pasta, "entrada.xml")).TrimEnd('\n');
        string envelope =
            "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><CancelarNfseRequest xmlns=\"http://notacarioca.rio.gov.br/\"><inputXML>" +
            System.Security.SecurityElement.Escape(entrada) + "</inputXML></CancelarNfseRequest></soap:Body></soap:Envelope>";

        HttpResponseMessage resposta = await _fabrica.CreateClient().PostAsJsonAsync("/interno/sombra/cancelamento", new
        {
            envelope,
            respostaLegado = File.ReadAllText(Path.Combine(pasta, "legado.xml")),
            statusLegado = int.Parse(File.ReadAllText(Path.Combine(pasta, "legado.status")).Trim(), CultureInfo.InvariantCulture),
            idadeMs = 300,
        });
        string texto = await resposta.Content.ReadAsStringAsync();
        Assert.True(resposta.IsSuccessStatusCode, $"{(int)resposta.StatusCode}: {texto}");
        JsonElement corpo = JsonDocument.Parse(texto).RootElement;

        Assert.True(corpo.GetProperty("resultado").GetString() == "igual", corpo.ToString());
    }

    public sealed class Fabrica : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Oracle", Environment.GetEnvironmentVariable(ParidadeTheoryAttribute.Variavel) ?? "nao-configurada");
            builder.UseSetting("ConnectionStrings:OracleSomenteLeitura", Environment.GetEnvironmentVariable(SombraOracleTheoryAttribute.Variavel) ?? "");
            builder.UseSetting("Interno:Porta", "0"); // o TestServer não tem porta local
        }
    }
}

public sealed class SombraOracleTheoryAttribute : TheoryAttribute
{
    public const string Variavel = "NFSE_PARIDADE_ORACLE_RO";

    public SombraOracleTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variavel)))
        {
            Skip = $"defina {Variavel} com a connection string do NFSE_MODERNO_RO";
        }
    }
}
