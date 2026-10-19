using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nfse.Dominio.Notas;

namespace Nfse.Host.Testes;

public class RestTests : IClassFixture<FabricaDoHost>
{
    private readonly FabricaDoHost _fabrica;

    public RestTests(FabricaDoHost fabrica)
    {
        _fabrica = fabrica;
        _fabrica.Dados.Notas.Clear();
        for (int i = 1; i <= 5; i++)
        {
            _fabrica.Dados.Notas.Add(DadosFalsos.Nota(i, 100 + i, new DateTime(2026, 10, i, 10, 0, 0),
                situacao: i == 3 ? SituacaoNota.Cancelada : SituacaoNota.Normal));
        }
    }

    private HttpClient Cliente(string? inscricoes = "10000001")
    {
        HttpClient cliente = _fabrica.CreateClient();
        if (inscricoes is not null)
        {
            cliente.DefaultRequestHeaders.Add(AutenticacaoDeTeste.Header, inscricoes);
        }

        return cliente;
    }

    private const string Url = "/api/v1/prestadores/10000001/nfse?dataInicial=2026-10-01&dataFinal=2026-10-31";

    [Fact]
    public async Task Sem_autenticacao_e_401()
    {
        HttpResponseMessage resposta = await Cliente(inscricoes: null).GetAsync(Url);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Outro_prestador_e_403_com_problem_details()
    {
        HttpResponseMessage resposta = await Cliente("10000002").GetAsync(Url);

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("dataInicial=2026-10-01&dataFinal=2026-11-01")]   // 32 dias: o legado aceita (C03), a API não
    [InlineData("dataInicial=10/01/2026&dataFinal=2026-10-31")]   // não ISO: o legado aceita (C04), a API não
    [InlineData("dataInicial=2026-10-01")]
    [InlineData("dataInicial=2026-10-01&dataFinal=2026-10-31&limite=101")]
    [InlineData("dataInicial=2026-10-01&dataFinal=2026-10-31&limite=abc")]   // não inteiro: do catálogo, não o 400 do binding
    [InlineData("dataInicial=2026-10-01&dataFinal=2026-10-31&situacao=talvez")]
    [InlineData("dataInicial=2026-10-01&dataFinal=2026-10-31&cursor=lixo")]
    public async Task Parametros_invalidos_sao_400(string query)
    {
        HttpResponseMessage resposta = await Cliente().GetAsync($"/api/v1/prestadores/10000001/nfse?{query}");
        JsonElement problema = await resposta.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("https://api.notacarioca.rio.gov.br/problemas/parametros-invalidos", problema.GetProperty("type").GetString());
        Assert.NotEmpty(problema.GetProperty("errors").EnumerateObject());
    }

    [Fact]
    public async Task Prestador_inativo_e_404()
    {
        HttpResponseMessage resposta = await Cliente("10000030")
            .GetAsync("/api/v1/prestadores/10000030/nfse?dataInicial=2026-10-01&dataFinal=2026-10-31");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task Mesmo_dia_inicial_e_final_devolve_as_notas_do_dia()
    {
        // no SOAP isso é E040 (C02)
        JsonElement pagina = await Cliente().GetFromJsonAsync<JsonElement>(
            "/api/v1/prestadores/10000001/nfse?dataInicial=2026-10-02&dataFinal=2026-10-02");

        Assert.Equal(102, pagina.GetProperty("itens")[0].GetProperty("numero").GetInt64());
    }

    [Fact]
    public async Task Cursor_percorre_todas_as_paginas_sem_repetir_nem_perder()
    {
        var numeros = new List<long>();
        string? cursor = null;
        do
        {
            string url = Url + "&limite=2" + (cursor is null ? "" : "&cursor=" + cursor);
            JsonElement pagina = await Cliente().GetFromJsonAsync<JsonElement>(url);
            numeros.AddRange(pagina.GetProperty("itens").EnumerateArray().Select(n => n.GetProperty("numero").GetInt64()));
            cursor = pagina.GetProperty("proximoCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal([101, 102, 103, 104, 105], numeros);
    }

    [Fact]
    public async Task Representacao_preserva_zero_e_publica_offset_e_situacao()
    {
        JsonElement nota = (await Cliente().GetFromJsonAsync<JsonElement>(Url + "&situacao=cancelada")).GetProperty("itens")[0];

        Assert.Equal("01234567000195", nota.GetProperty("tomador").GetProperty("documento").GetProperty("numero").GetString());
        Assert.Equal("2026-10-03T10:00:00-03:00", nota.GetProperty("dataEmissao").GetString());
        Assert.Equal("cancelada", nota.GetProperty("situacao").GetString());
        Assert.Equal("2026-10", nota.GetProperty("competencia").GetString());
    }

    [Fact]
    public async Task OpenApi_publicado()
    {
        HttpResponseMessage resposta = await Cliente().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Contains("/api/v1/prestadores/{inscricao}/nfse", await resposta.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

public class RestTomadorTests : IClassFixture<FabricaDoHost>
{
    private readonly FabricaDoHost _fabrica;

    public RestTomadorTests(FabricaDoHost fabrica)
    {
        _fabrica = fabrica;
        _fabrica.Dados.Notas.Clear();
        _fabrica.Dados.Notas.Add(DadosFalsos.Nota(1, 21, new DateTime(2026, 10, 4, 9, 53, 0)));
        _fabrica.Dados.Notas.Add(DadosFalsos.Nota(2, 30, new DateTime(2026, 10, 5, 9, 0, 0), tomador: "52998224725"));
    }

    private HttpClient Cliente(string? documento)
    {
        HttpClient cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add(AutenticacaoDeTeste.Header, "nenhuma");
        if (documento is not null)
        {
            cliente.DefaultRequestHeaders.Add(AutenticacaoDeTeste.HeaderDocumento, documento);
        }

        return cliente;
    }

    [Fact]
    public async Task So_ve_as_notas_do_titular_do_token()
    {
        JsonElement pagina = await Cliente("01234567000195").GetFromJsonAsync<JsonElement>("/api/v1/tomador/nfse?dataInicial=2026-10-01&dataFinal=2026-10-31");

        Assert.Equal([21L], pagina.GetProperty("itens").EnumerateArray().Select(n => n.GetProperty("numero").GetInt64()));
        Assert.Equal("01234567000195", _fabrica.Dados.UltimoFiltroTomadas!.TomadorDigitos);
    }

    [Fact]
    public async Task Token_sem_documento_valido_e_403()
    {
        HttpResponseMessage resposta = await Cliente(null).GetAsync("/api/v1/tomador/nfse?dataInicial=2026-10-01&dataFinal=2026-10-31");

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    [Fact]
    public async Task Documento_nao_e_aceito_na_url()
    {
        // não existe rota com o documento no caminho
        HttpResponseMessage resposta = await Cliente("01234567000195").GetAsync("/api/v1/tomadores/01234567000195/nfse?dataInicial=2026-10-01&dataFinal=2026-10-31");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }
}
