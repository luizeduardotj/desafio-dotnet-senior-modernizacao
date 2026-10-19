using System.Net;
using System.Net.Http.Json;
using System.Text;
using Nfse.Aplicacao.Cancelamento;
using Nfse.Dominio.Cancelamento;
using Nfse.Dominio.Notas;

namespace Nfse.Host.Testes.Contrato;

/// <summary>
/// Contrato da REST <c>/api/v1</c> pelo lado das respostas (ADR-0010): cada status que os endpoints devolvem — sucesso e
/// ProblemDetails — tem de estar documentado no snapshot <c>openapi-v1.json</c>, com o media type e o corpo do schema
/// declarado. Complementa <see cref="ContratoOpenApiTests"/>, que só compara o spec gerado com o versionado.
/// </summary>
[Trait("Categoria", "Contrato")]
public class ContratoRespostasRestTests : IClassFixture<FabricaDoHost>
{
    private const string Emitidas = "/api/v1/prestadores/10000001/nfse?dataInicial=2026-10-01&dataFinal=2026-10-31";
    private const string Tomadas = "/api/v1/tomador/nfse?dataInicial=2026-10-01&dataFinal=2026-10-31";
    private const string Cancelamento = "/api/v1/prestadores/10000001/nfse/1/cancelamento";
    private const string Tomador = "01234567000195";

    private static readonly DateTime Agora = new(2026, 10, 9, 15, 0, 0);
    private readonly FabricaDoHost _fabrica;

    public ContratoRespostasRestTests(FabricaDoHost fabrica)
    {
        _fabrica = fabrica;
        _fabrica.Dados.Notas.Clear();

        // todas as formas que a representação tem: normal, cancelada, substituída, tomador sem documento e campos nulos
        _fabrica.Dados.Notas.Add(DadosFalsos.Nota(1, 101, new DateTime(2026, 10, 1, 10, 0, 0)));
        _fabrica.Dados.Notas.Add(DadosFalsos.Nota(2, 102, new DateTime(2026, 10, 2, 10, 0, 0), situacao: SituacaoNota.Cancelada));
        _fabrica.Dados.Notas.Add(DadosFalsos.Nota(3, 103, new DateTime(2026, 10, 3, 10, 0, 0), situacao: SituacaoNota.Substituida)
            with { NumeroSubstituidora = 104 });
        _fabrica.Dados.Notas.Add(DadosFalsos.Nota(4, 104, new DateTime(2026, 10, 4, 10, 0, 0)) with
        {
            CodigoVerificacao = null, Competencia = null, ItemServico = null, Discriminacao = null, ValorServicos = null,
            ValorDeducoes = null, BaseCalculo = null, Aliquota = null, ValorIss = null, IssRetido = true,
            TomadorDocumento = null, TomadorRazaoSocial = null,
        });
        _fabrica.Dados.Notas.Add(DadosFalsos.Nota(5, 105, new DateTime(2026, 10, 5, 10, 0, 0), tomador: "52998224725"));

        Decidir(DecisaoDeCancelamento.Permitido);
    }

    private void Decidir(DecisaoDeCancelamento decisao) =>
        _fabrica.Dados.RespostaDoCancelamento = new ResultadoDoGateway(
            decisao,
            decisao == DecisaoDeCancelamento.NotaNaoEncontrada ? null : new EstadoParaCancelamento(SituacaoNota.Normal, new Competencia(2026, 10), false),
            decisao == DecisaoDeCancelamento.NotaNaoEncontrada ? null : Agora,
            decisao is DecisaoDeCancelamento.Permitido or DecisaoDeCancelamento.JaCancelada ? Agora : null);

    private HttpClient Cliente(string? inscricoes = "10000001", string? documento = null)
    {
        HttpClient cliente = _fabrica.CreateClient();
        if (inscricoes is not null)
        {
            cliente.DefaultRequestHeaders.Add(AutenticacaoDeTeste.Header, inscricoes);
        }

        if (documento is not null)
        {
            cliente.DefaultRequestHeaders.Add(AutenticacaoDeTeste.HeaderDocumento, documento);
        }

        return cliente;
    }

    private static async Task ConfereAsync(HttpResponseMessage resposta, HttpStatusCode esperado)
    {
        Assert.Equal(esperado, resposta.StatusCode);
        await ContratoOpenApi.Snapshot.ValidarAsync(resposta);
    }

    // --- GET /api/v1/prestadores/{inscricao}/nfse ---

    [Theory]
    [InlineData("")]
    [InlineData("&limite=2")]                 // proximoCursor preenchido
    [InlineData("&situacao=cancelada")]
    [InlineData("&situacao=substituida")]
    public async Task Emitidas_200(string filtros)
    {
        using HttpResponseMessage resposta = await Cliente().GetAsync(new Uri(Emitidas + filtros, UriKind.Relative));

        await ConfereAsync(resposta, HttpStatusCode.OK);
    }

    [Fact]
    public async Task Emitidas_200_pagina_vazia()
    {
        using HttpResponseMessage resposta = await Cliente()
            .GetAsync(new Uri("/api/v1/prestadores/10000001/nfse?dataInicial=2026-09-01&dataFinal=2026-09-30", UriKind.Relative));

        await ConfereAsync(resposta, HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("dataInicial=2026-10-01&dataFinal=2026-11-01")]
    [InlineData("dataInicial=10/01/2026&dataFinal=2026-10-31")]
    [InlineData("dataInicial=2026-10-01")]
    [InlineData("dataInicial=2026-10-01&dataFinal=2026-10-31&limite=101")]
    [InlineData("dataInicial=2026-10-01&dataFinal=2026-10-31&limite=abc")]
    [InlineData("dataInicial=2026-10-01&dataFinal=2026-10-31&situacao=talvez")]
    [InlineData("dataInicial=2026-10-01&dataFinal=2026-10-31&cursor=lixo")]
    public async Task Emitidas_400(string query)
    {
        using HttpResponseMessage resposta = await Cliente().GetAsync(new Uri($"/api/v1/prestadores/10000001/nfse?{query}", UriKind.Relative));

        await ConfereAsync(resposta, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Emitidas_401()
    {
        using HttpResponseMessage resposta = await Cliente(inscricoes: null).GetAsync(new Uri(Emitidas, UriKind.Relative));

        await ConfereAsync(resposta, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Emitidas_403()
    {
        using HttpResponseMessage resposta = await Cliente("10000002").GetAsync(new Uri(Emitidas, UriKind.Relative));

        await ConfereAsync(resposta, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Emitidas_404()
    {
        using HttpResponseMessage resposta = await Cliente("10000030")
            .GetAsync(new Uri("/api/v1/prestadores/10000030/nfse?dataInicial=2026-10-01&dataFinal=2026-10-31", UriKind.Relative));

        await ConfereAsync(resposta, HttpStatusCode.NotFound);
    }

    // --- GET /api/v1/tomador/nfse ---

    [Theory]
    [InlineData("")]
    [InlineData("&limite=1")]
    public async Task Tomadas_200(string filtros)
    {
        using HttpResponseMessage resposta = await Cliente("nenhuma", Tomador).GetAsync(new Uri(Tomadas + filtros, UriKind.Relative));

        await ConfereAsync(resposta, HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("dataInicial=2026-10-01&dataFinal=2026-11-01")]
    [InlineData("dataInicial=2026-10-01&dataFinal=2026-10-31&limite=0")]
    [InlineData("dataInicial=2026-10-01&dataFinal=2026-10-31&cursor=lixo")]
    public async Task Tomadas_400(string query)
    {
        using HttpResponseMessage resposta = await Cliente("nenhuma", Tomador).GetAsync(new Uri($"/api/v1/tomador/nfse?{query}", UriKind.Relative));

        await ConfereAsync(resposta, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Tomadas_401()
    {
        using HttpResponseMessage resposta = await Cliente(inscricoes: null).GetAsync(new Uri(Tomadas, UriKind.Relative));

        await ConfereAsync(resposta, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tomadas_403()
    {
        using HttpResponseMessage resposta = await Cliente("nenhuma").GetAsync(new Uri(Tomadas, UriKind.Relative));

        await ConfereAsync(resposta, HttpStatusCode.Forbidden);
    }

    // --- POST /api/v1/prestadores/{inscricao}/nfse/{numero}/cancelamento ---

    [Theory]
    [InlineData(DecisaoDeCancelamento.Permitido)]
    [InlineData(DecisaoDeCancelamento.JaCancelada)]
    public async Task Cancelamento_200(DecisaoDeCancelamento decisao)
    {
        Decidir(decisao);

        using HttpResponseMessage resposta = await Cliente().PostAsJsonAsync(Cancelamento, new { motivo = "erro de digitação" });

        await ConfereAsync(resposta, HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("{\"motivo\":\"   \"}")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"motivo\":\"" + "çççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççççç" + "\"}")]
    public async Task Cancelamento_400(string corpo)
    {
        using var conteudo = new StringContent(corpo, Encoding.UTF8, "application/json");
        using HttpResponseMessage resposta = await Cliente().PostAsync(new Uri(Cancelamento, UriKind.Relative), conteudo);

        await ConfereAsync(resposta, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cancelamento_401()
    {
        using HttpResponseMessage resposta = await Cliente(inscricoes: null).PostAsJsonAsync(Cancelamento, new { motivo = "x" });

        await ConfereAsync(resposta, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cancelamento_403()
    {
        using HttpResponseMessage resposta = await Cliente("10000002").PostAsJsonAsync(Cancelamento, new { motivo = "x" });

        await ConfereAsync(resposta, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Cancelamento_404_prestador()
    {
        using HttpResponseMessage resposta = await Cliente("99999999")
            .PostAsJsonAsync("/api/v1/prestadores/99999999/nfse/1/cancelamento", new { motivo = "x" });

        await ConfereAsync(resposta, HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(DecisaoDeCancelamento.NotaNaoEncontrada, HttpStatusCode.NotFound)]
    [InlineData(DecisaoDeCancelamento.NotaSubstituida, HttpStatusCode.Conflict)]
    [InlineData(DecisaoDeCancelamento.PrazoExpirado, HttpStatusCode.UnprocessableEntity)]
    [InlineData(DecisaoDeCancelamento.GuiaPaga, HttpStatusCode.UnprocessableEntity)]
    public async Task Cancelamento_negativas(DecisaoDeCancelamento decisao, HttpStatusCode status)
    {
        Decidir(decisao);

        using HttpResponseMessage resposta = await Cliente().PostAsJsonAsync(Cancelamento, new { motivo = "x" });

        await ConfereAsync(resposta, status);
    }
}
