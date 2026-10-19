namespace Nfse.Fachada.Roteamento;

/// <summary>Nomes das operações SOAP e extração a partir do header <c>SOAPAction</c>.</summary>
public static class OperacoesSoap
{
    public const string ConsultarNfse = nameof(ConsultarNfse);
    public const string ConsultarNfseServicoTomado = nameof(ConsultarNfseServicoTomado);
    public const string CancelarNfse = nameof(CancelarNfse);

    /// <summary>Operações extraídas do monolito (ADR-0001). As demais passam pela fachada sem leitura do corpo.</summary>
    public static readonly IReadOnlyList<string> EmEscopo = [ConsultarNfse, ConsultarNfseServicoTomado, CancelarNfse];

    private static readonly string[] ForaDoEscopoConhecidas = ["RecepcionarLoteRps", "ConsultarSituacaoLoteRps", "ConsultarLoteRps"];

    /// <summary>
    /// C21/C29: mesmo algoritmo do legado (<c>Program.cs:59-60</c>): <c>Trim().Trim('"')</c>, sufixo após o último
    /// <c>/</c> e comparação exata, sensível a maiúsculas. O elemento do corpo é ignorado.
    /// </summary>
    public static string ExtrairOperacao(string? soapAction)
    {
        string acao = (soapAction ?? string.Empty).Trim().Trim('"');
        return acao[(acao.LastIndexOf('/') + 1)..];
    }

    public static bool EstaEmEscopo(string operacao) =>
        string.Equals(operacao, ConsultarNfse, StringComparison.Ordinal)
        || string.Equals(operacao, ConsultarNfseServicoTomado, StringComparison.Ordinal)
        || string.Equals(operacao, CancelarNfse, StringComparison.Ordinal);

    public static bool EhLeitura(string operacao) => !string.Equals(operacao, CancelarNfse, StringComparison.Ordinal);

    /// <summary>
    /// Nome seguro para log e métrica: o header vem do cliente e pode conter qualquer texto (cardinalidade e dados
    /// pessoais, ADR-0012). Só nomes conhecidos saem como estão.
    /// </summary>
    public static string NomeParaTelemetria(string operacao) =>
        EstaEmEscopo(operacao) || Array.IndexOf(ForaDoEscopoConhecidas, operacao) >= 0 ? operacao : "desconhecida";
}
