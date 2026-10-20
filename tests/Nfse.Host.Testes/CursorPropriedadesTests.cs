using System.Net.Http.Json;
using System.Text.Json;
using CsCheck;
using Nfse.Aplicacao.Consultas;
using Nfse.Dominio.Notas;

namespace Nfse.Host.Testes;

/// <summary>
/// Propriedade do cursor da REST (ADR-0010, ADR-0011): para qualquer massa de notas falsas e qualquer <c>limite</c>,
/// percorrer todas as páginas devolve cada nota do período uma vez, na ordem <c>(DT_EMISSAO, ID)</c>, sem repetir nem
/// perder, inclusive com emissões no mesmo instante (o ID desempata). O período é inclusivo: 31/10 23:59 entra,
/// 30/09 23:59 e 01/11 00:00 não. Os documentos do tomador saem com os zeros à esquerda.
/// </summary>
public class CursorPropriedadesTests : IClassFixture<FabricaDoHost>
{
    private const string Url = "/api/v1/prestadores/10000001/nfse?dataInicial=2026-10-01&dataFinal=2026-10-31";

    private static readonly string[] Tomadores = ["00074579869109", "01234567000195", "52998224725", "04815162342"];

    private static readonly DateTime[] ForaDoPeriodo = [new(2026, 9, 30, 23, 59, 59), new(2026, 11, 1, 0, 0, 0)];

    // Poucos horários possíveis por dia: emissões no mesmo instante aparecem com frequência.
    private static readonly Gen<DateTime> Emissao =
        Gen.Select(Gen.Int[1, 31], Gen.Int[0, 3]).Select((dia, hora) => new DateTime(2026, 10, dia, hora * 8 - (hora == 3 ? 1 : 0), hora == 3 ? 59 : 0, 0));

    private static readonly Gen<(DateTime Emissao, int Tomador)[]> Massa =
        Gen.Select(Emissao, Gen.Int[0, Tomadores.Length - 1]).Array[0, 60];

    private readonly FabricaDoHost _fabrica;

    public CursorPropriedadesTests(FabricaDoHost fabrica) => _fabrica = fabrica;

    [Fact]
    public async Task Percorrer_todas_as_paginas_nao_repete_nem_perde_nota()
    {
        using HttpClient cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add(AutenticacaoDeTeste.Header, "10000001");

        // Uma thread: a massa fica nos dados falsos compartilhados pelo host.
        await Gen.Select(Massa, Gen.Int[1, 10]).SampleAsync(async (massa, limite) =>
        {
            List<NotaFiscalLida> notas = Carregar(massa);
            var vistas = new List<(long Numero, string Tomador)>();
            string? cursor = null;
            int paginas = 0;
            do
            {
                JsonElement pagina = await cliente.GetFromJsonAsync<JsonElement>(
                    $"{Url}&limite={limite}" + (cursor is null ? "" : "&cursor=" + cursor));
                JsonElement[] itens = pagina.GetProperty("itens").EnumerateArray().ToArray();
                Assert.InRange(itens.Length, 0, limite);
                vistas.AddRange(itens.Select(n => (
                    n.GetProperty("numero").GetInt64(),
                    n.GetProperty("tomador").GetProperty("documento").GetProperty("numero").GetString()!)));
                cursor = pagina.GetProperty("proximoCursor").GetString();
                Assert.True(++paginas <= notas.Count + 1, "o cursor não termina");
            }
            while (cursor is not null);

            (long, string)[] esperadas = notas
                .Where(n => n.DataEmissao.Month == 10)
                .OrderBy(n => n.DataEmissao).ThenBy(n => n.Id)
                .Select(n => (n.Numero, n.TomadorDocumento!.Digitos))
                .ToArray();
            Assert.Equal(esperadas, vistas);
        }, threads: 1, iter: 60);
    }

    private List<NotaFiscalLida> Carregar((DateTime Emissao, int Tomador)[] massa)
    {
        List<NotaFiscalLida> notas = _fabrica.Dados.Notas;
        notas.Clear();
        // IDs fora da ordem de emissão, para o desempate pelo ID não coincidir com a ordem de inserção.
        for (int i = 0; i < massa.Length; i++)
        {
            long id = (i * 37 % 61) + 1;
            notas.Add(DadosFalsos.Nota(id, 1000 + id, massa[i].Emissao, Tomadores[massa[i].Tomador],
                i % 5 == 0 ? SituacaoNota.Cancelada : SituacaoNota.Normal));
        }

        notas.AddRange(ForaDoPeriodo.Select((emissao, i) => DadosFalsos.Nota(100 + i, 9000 + i, emissao)));
        return notas;
    }
}
