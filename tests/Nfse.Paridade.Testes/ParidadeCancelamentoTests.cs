using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;

namespace Nfse.Paridade.Testes;

/// <summary>
/// Golden master do <c>CancelarNfse</c> (ADR-0011). Muda o estado do banco: exige a linha de base restaurada,
/// <c>FIXED_DATE = '2026-10-09-15:00:00'</c> e roda os casos na ordem dos nomes, uma única vez. Por isso só roda com
/// <c>NFSE_PARIDADE_CANCELAMENTO=1</c> além da connection string. Depois de cada caso, o estado de <c>NFSE</c> e
/// <c>GUIAS</c> da competência da nota pedida também tem de ser igual ao que o legado deixou (<c>legado.estado</c>):
/// cobre K07 e qualquer escrita fora do lugar.
/// </summary>
public sealed class ParidadeCancelamentoTests : IClassFixture<ParidadeLeiturasTests.Fabrica>
{
    // K11/C20: o legado devolve o Fault com a mensagem do Oracle (ORA-12899); o novo, um texto genérico com trace.
    private const string CasoDoFault = "209-k11-motivo-255-acentuados";

    private readonly ParidadeLeiturasTests.Fabrica _fabrica;

    public ParidadeCancelamentoTests(ParidadeLeiturasTests.Fabrica fabrica) => _fabrica = fabrica;

    [ParidadeCancelamentoFact]
    public async Task Cancelamentos_iguais_ao_legado_na_ordem_do_corpus()
    {
        var divergencias = new List<string>();
        string raiz = Corpus.Raiz();
        foreach (string pasta in Directory.GetDirectories(Path.Combine(raiz, "tests", "paridade", "casos")).Order(StringComparer.Ordinal))
        {
            if (File.ReadAllText(Path.Combine(pasta, "operacao")).Trim() != "CancelarNfse")
            {
                continue;
            }

            string caso = Path.GetFileName(pasta);
            (HttpStatusCode status, string corpo) = await Corpus.EnviarAsync(_fabrica.CreateClient(), "CancelarNfse", pasta);
            string esperado = File.ReadAllText(Path.Combine(pasta, "legado.xml"));
            var statusEsperado = (HttpStatusCode)int.Parse(File.ReadAllText(Path.Combine(pasta, "legado.status")).Trim(), CultureInfo.InvariantCulture);

            string estadoEsperado = Path.Combine(pasta, "legado.estado");
            if (File.Exists(estadoEsperado))
            {
                string estado = await EstadoDoBanco.LerAsync(pasta);
                if (estado != File.ReadAllText(estadoEsperado).TrimEnd('\n'))
                {
                    divergencias.Add($"{caso}: estado de NFSE/GUIAS\n  legado: {File.ReadAllText(estadoEsperado)}\n  novo:   {estado}");
                }
            }
            else
            {
                divergencias.Add($"{caso}: sem legado.estado (capture com ESTADO=1, tests/paridade/README.md)");
            }

            if (caso == CasoDoFault)
            {
                bool ok = status == statusEsperado && corpo.Contains("<faultcode>soap:Server</faultcode>", StringComparison.Ordinal)
                    && !corpo.Contains("ORA-", StringComparison.Ordinal) && esperado.Contains("ORA-12899", StringComparison.Ordinal);
                if (!ok)
                {
                    divergencias.Add($"{caso}: esperado Fault soap:Server sem mensagem do Oracle; veio {status}: {corpo}");
                }

                continue;
            }

            if (status != statusEsperado || corpo != esperado)
            {
                divergencias.Add($"{caso}: {status}\n  legado: {esperado}\n  novo:   {corpo}");
            }
        }

        Assert.True(divergencias.Count == 0, string.Join("\n", divergencias));
    }
}

public sealed class ParidadeCancelamentoFactAttribute : FactAttribute
{
    public ParidadeCancelamentoFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ParidadeTheoryAttribute.Variavel)) ||
            Environment.GetEnvironmentVariable("NFSE_PARIDADE_CANCELAMENTO") != "1")
        {
            Skip = "muda o banco: defina NFSE_PARIDADE_CANCELAMENTO=1 sobre a linha de base com FIXED_DATE (tests/paridade/README.md)";
        }
    }
}

/// <summary>Snapshot de <c>tests/paridade/estado-depois-do-cancelamento.sql</c>, o mesmo que o <c>estado-do-banco.sh</c> captura do legado.</summary>
internal static partial class EstadoDoBanco
{
    [GeneratedRegex("<InscricaoMunicipal>([^<]*)</InscricaoMunicipal>")]
    private static partial Regex Inscricao();

    [GeneratedRegex("<Numero>([0-9]+)</Numero>")]
    private static partial Regex Numero();

    public static async Task<string> LerAsync(string pasta)
    {
        string entrada = File.ReadAllText(Path.Combine(pasta, "entrada.xml"));
        string sql = File.ReadAllText(Path.Combine(Corpus.Raiz(), "tests", "paridade", "estado-depois-do-cancelamento.sql"));
        Match numero = Numero().Match(entrada);

        await using var conexao = new OracleConnection(Environment.GetEnvironmentVariable(ParidadeTheoryAttribute.Variavel));
        await conexao.OpenAsync();
        await using var comando = new OracleCommand(sql, conexao) { BindByName = true };
        comando.Parameters.Add("im", OracleDbType.Varchar2).Value = Inscricao().Match(entrada).Groups[1].Value;
        comando.Parameters.Add("numero", OracleDbType.Int64).Value =
            numero.Success ? long.Parse(numero.Groups[1].Value, CultureInfo.InvariantCulture) : DBNull.Value;

        var linhas = new List<string>();
        await using OracleDataReader leitor = await comando.ExecuteReaderAsync();
        while (await leitor.ReadAsync())
        {
            linhas.Add(leitor.GetString(0));
        }

        return string.Join('\n', linhas);
    }
}

/// <summary>Envelope e envio iguais aos de <c>tests/paridade/capturar.sh</c>.</summary>
internal static class Corpus
{
    public static async Task<(HttpStatusCode Status, string Corpo)> EnviarAsync(HttpClient cliente, string operacao, string pasta)
    {
        string entrada = File.ReadAllText(Path.Combine(pasta, "entrada.xml")).TrimEnd('\n');
        string envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>" +
            $"<{operacao}Request xmlns=\"http://notacarioca.rio.gov.br/\"><inputXML>" +
            entrada.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal) +
            $"</inputXML></{operacao}Request></soap:Body></soap:Envelope>";

        using var conteudo = new StringContent(envelope, Encoding.UTF8, "text/xml");
        using var pedido = new HttpRequestMessage(HttpMethod.Post, "/NfseWS.asmx") { Content = conteudo };
        pedido.Headers.Add("SOAPAction", $"\"http://notacarioca.rio.gov.br/{operacao}\"");
        using HttpResponseMessage resposta = await cliente.SendAsync(pedido);
        return (resposta.StatusCode, await resposta.Content.ReadAsStringAsync());
    }

    public static string Raiz()
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
}
