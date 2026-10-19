using System.Net;
using System.Text;

namespace Nfse.Host.Testes.Contrato;

/// <summary>O validador de <see cref="ContratoOpenApi"/> reprova o que deve reprovar: sem isso, um teste verde não diz nada.</summary>
[Trait("Categoria", "Contrato")]
public class ContratoOpenApiValidadorTests
{
    private static readonly ContratoOpenApi Contrato = ContratoOpenApi.Snapshot;

    private const string Nota =
        "{\"numero\":1,\"codigoVerificacao\":null,\"dataEmissao\":\"2026-10-03T10:00:00-03:00\",\"competencia\":\"2026-10\",\"situacao\":\"normal\"," +
        "\"servico\":{\"item\":\"01.01\",\"discriminacao\":\"x\",\"valorServicos\":1000.00,\"valorDeducoes\":0,\"baseCalculo\":1000,\"aliquota\":0.05," +
        "\"valorIss\":50,\"issRetido\":false},\"prestador\":{\"inscricaoMunicipal\":\"10000001\",\"documento\":null,\"razaoSocial\":null}," +
        "\"tomador\":{\"documento\":{\"numero\":\"01234567000195\",\"tipo\":\"cnpj\"},\"razaoSocial\":\"T\"},\"cancelamento\":null,\"substituidaPor\":null}";

    [Fact]
    public void Spec_so_usa_palavras_chave_que_o_validador_conhece() =>
        Assert.Empty(Contrato.SoDraft4a7());

    [Fact]
    public async Task Aceita_uma_pagina_valida() =>
        Assert.Empty(await Contrato.ErrosAsync("PaginaDto", $"{{\"itens\":[{Nota}],\"proximoCursor\":null}}"));

    [Theory]
    [InlineData("{\"itens\":[]}")]                                         // falta proximoCursor
    [InlineData("{\"itens\":{},\"proximoCursor\":null}")]                  // itens não é lista
    [InlineData("{\"itens\":[{\"numero\":1}],\"proximoCursor\":null}")]    // item sem os obrigatórios
    [InlineData("{\"itens\":[],\"proximoCursor\":7}")]                     // cursor não é string
    public async Task Reprova_pagina_fora_do_schema(string corpo) =>
        Assert.NotEmpty(await Contrato.ErrosAsync("PaginaDto", corpo));

    [Theory]
    [InlineData("\"dataEmissao\":\"2026-10-03T10:00:00-03:00\"", "\"dataEmissao\":\"ontem\"")]        // format date-time
    [InlineData("\"documento\":{\"numero\":\"01234567000195\",\"tipo\":\"cnpj\"}", "\"documento\":\"01234567000195\"")] // oneOf null | DocumentoDto
    [InlineData("\"issRetido\":false", "\"issRetido\":2")]
    [InlineData("\"numero\":1,", "\"numero\":\"1a\",")]                                               // pattern do int64 em string
    public async Task Reprova_nota_com_campo_errado(string original, string trocado) =>
        Assert.NotEmpty(await Contrato.ErrosAsync("NfseDto", Nota.Replace(original, trocado, StringComparison.Ordinal)));

    [Fact]
    public async Task Reprova_status_nao_documentado()
    {
        using HttpResponseMessage resposta = Resposta(HttpStatusCode.Conflict, "application/problem+json", "{}", "/api/v1/tomador/nfse");

        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => Contrato.ValidarAsync(resposta));
    }

    [Fact]
    public async Task Reprova_media_type_nao_declarado()
    {
        using HttpResponseMessage resposta = Resposta(HttpStatusCode.Forbidden, "application/json", "{}", "/api/v1/tomador/nfse");

        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => Contrato.ValidarAsync(resposta));
    }

    [Fact]
    public async Task Reprova_caminho_fora_do_contrato()
    {
        using HttpResponseMessage resposta = Resposta(HttpStatusCode.OK, "application/json", "{}", "/api/v1/tomadores/1/nfse");

        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => Contrato.ValidarAsync(resposta));
    }

    [Fact]
    public async Task Casa_o_modelo_do_caminho_e_valida_o_corpo()
    {
        using HttpResponseMessage valida = Resposta(HttpStatusCode.OK, "application/json",
            "{\"numero\":1,\"dataHora\":\"2026-10-09T15:00:00-03:00\",\"jaEstavaCancelada\":false}", "/api/v1/prestadores/10000001/nfse/1/cancelamento", HttpMethod.Post);
        using HttpResponseMessage invalida = Resposta(HttpStatusCode.OK, "application/json",
            "{\"numero\":1,\"dataHora\":\"2026-10-09T15:00:00-03:00\"}", "/api/v1/prestadores/10000001/nfse/1/cancelamento", HttpMethod.Post);

        await Contrato.ValidarAsync(valida);
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => Contrato.ValidarAsync(invalida));
    }

    private static HttpResponseMessage Resposta(HttpStatusCode status, string tipo, string corpo, string caminho, HttpMethod? metodo = null) =>
        new(status)
        {
            Content = new StringContent(corpo, Encoding.UTF8, tipo),
            RequestMessage = new HttpRequestMessage(metodo ?? HttpMethod.Get, new Uri("http://localhost" + caminho)),
        };
}
