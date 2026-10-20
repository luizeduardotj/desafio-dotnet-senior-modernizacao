using System.Collections.Concurrent;
using System.Globalization;

namespace Nfse.Resiliencia.Testes;

/// <summary>
/// Tempos medidos [M], gravados no fim da execução em <c>NFSE_RESILIENCIA_MEDICOES</c> (padrão
/// <c>TestResults/resiliencia-medicoes.md</c>) como tabela Markdown; o CI a copia para o resumo do job.
/// </summary>
public sealed class Medicoes
{
    private static readonly string[] Cabecalho = ["| Cenário | Medida | Valor |", "| --- | --- | --- |"];

    private readonly ConcurrentQueue<(string Cenario, string Medida, string Valor)> _linhas = new();

    public void Registrar(string cenario, string medida, TimeSpan valor) =>
        _linhas.Enqueue((cenario, medida, valor.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms"));

    public void Registrar(string cenario, string medida, string valor) => _linhas.Enqueue((cenario, medida, valor));

    public void Gravar()
    {
        if (_linhas.IsEmpty)
        {
            return;
        }

        string arquivo = Environment.GetEnvironmentVariable("NFSE_RESILIENCIA_MEDICOES") is { Length: > 0 } v
            ? v
            : Path.Combine(Repositorio.Raiz, "TestResults", "resiliencia-medicoes.md");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(arquivo))!);
        // Por cenário, na ordem das letras do README; dentro do cenário, na ordem em que foram medidas.
        IEnumerable<string> linhas = Cabecalho.Concat(_linhas
            .OrderBy(l => l.Cenario, StringComparer.Ordinal)
            .Select(l => $"| {l.Cenario} | {l.Medida} | {l.Valor} |"));
        File.WriteAllLines(arquivo, linhas);
    }
}
