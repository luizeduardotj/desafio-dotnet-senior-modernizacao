using System.Text.RegularExpressions;

namespace Nfse.Paridade.Testes;

/// <summary>
/// ADR-0011, "Como verificar": ID do catálogo sem caso falha o build. Cada ID da ADR-0004 (C01–C29, K01–K11) tem de
/// aparecer na <c>descricao</c> de algum caso de <c>tests/paridade/casos</c> ou em <c>tests/paridade/catalogo-sem-caso</c>
/// com o motivo. Não precisa de Oracle: roda no job <c>moderno</c>.
/// </summary>
public sealed partial class CoberturaDoCatalogoTests
{
    [GeneratedRegex(@"^\| (?<id>[CK]\d{2}) \|", RegexOptions.Multiline)]
    private static partial Regex LinhaDoCatalogo();

    [GeneratedRegex(@"\b[CK]\d{2}\b")]
    private static partial Regex IdCitado();

    [Fact]
    public void Todo_ID_do_catalogo_tem_caso_ou_excecao_justificada()
    {
        SortedSet<string> catalogo = Catalogo();
        Dictionary<string, SortedSet<string>> casosPorId = CasosPorId();
        Dictionary<string, string> excecoes = Excecoes();

        Assert.True(catalogo.Count >= 40, $"catálogo da ADR-0004 lido com {catalogo.Count} IDs; o formato da tabela mudou?");

        string[] semCobertura = catalogo.Where(id => !casosPorId.ContainsKey(id) && !excecoes.ContainsKey(id)).ToArray();
        Assert.True(semCobertura.Length == 0,
            "ID do catálogo sem caso em tests/paridade/casos nem motivo em tests/paridade/catalogo-sem-caso: " + string.Join(", ", semCobertura));

        string[] obsoletas = excecoes.Keys.Where(casosPorId.ContainsKey)
            .Select(id => $"{id} (casos {string.Join(", ", casosPorId[id])})").ToArray();
        Assert.True(obsoletas.Length == 0, "ID com caso continua em catalogo-sem-caso, remova a linha: " + string.Join("; ", obsoletas));

        string[] semMotivo = excecoes.Where(e => e.Value.Length < 20).Select(e => e.Key).ToArray();
        Assert.True(semMotivo.Length == 0, "exceção sem motivo: " + string.Join(", ", semMotivo));

        string[] desconhecidos = casosPorId.Keys.Concat(excecoes.Keys).Where(id => !catalogo.Contains(id)).Distinct().ToArray();
        Assert.True(desconhecidos.Length == 0, "ID citado que não está no catálogo da ADR-0004: " + string.Join(", ", desconhecidos));
    }

    private static SortedSet<string> Catalogo()
    {
        string adr = File.ReadAllText(Path.Combine(Corpus.Raiz(), "docs", "adr", "0004-comportamentos-legados-nao-documentados.md"));
        return new SortedSet<string>(LinhaDoCatalogo().Matches(adr).Select(m => m.Groups["id"].Value), StringComparer.Ordinal);
    }

    private static Dictionary<string, SortedSet<string>> CasosPorId()
    {
        var porId = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (string pasta in Directory.GetDirectories(Path.Combine(Corpus.Raiz(), "tests", "paridade", "casos")))
        {
            foreach (Match id in IdCitado().Matches(File.ReadAllText(Path.Combine(pasta, "descricao"))))
            {
                if (!porId.TryGetValue(id.Value, out SortedSet<string>? casos))
                {
                    porId[id.Value] = casos = new SortedSet<string>(StringComparer.Ordinal);
                }

                casos.Add(Path.GetFileName(pasta));
            }
        }

        return porId;
    }

    private static Dictionary<string, string> Excecoes()
    {
        var excecoes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string linha in File.ReadAllLines(Path.Combine(Corpus.Raiz(), "tests", "paridade", "catalogo-sem-caso")))
        {
            if (string.IsNullOrWhiteSpace(linha) || linha.StartsWith('#'))
            {
                continue;
            }

            string[] partes = linha.Split(' ', 2);
            Assert.True(excecoes.TryAdd(partes[0], partes.Length > 1 ? partes[1].Trim() : ""), $"exceção repetida: {partes[0]}");
        }

        return excecoes;
    }
}
