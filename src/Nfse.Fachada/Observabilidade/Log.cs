using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Observabilidade;

/// <summary>Eventos de log estruturados. Nenhum recebe IM, CPF/CNPJ ou corpo em claro (ADR-0012).</summary>
internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Information, "SOAP {Operacao} -> {Destino} ({Motivo}) status {Status} em {DuracaoMs} ms")]
    public static partial void Roteada(ILogger logger, string operacao, Destino destino, MotivoDeRoteamento motivo, int status, long duracaoMs);

    [LoggerMessage(2, LogLevel.Warning, "SOAP {Operacao}: falha no novo ({Erro}); fallback para o legado")]
    public static partial void FallbackParaLegado(ILogger logger, string operacao, string erro);

    [LoggerMessage(3, LogLevel.Error, "SOAP {Operacao}: falha no novo depois do envio ({Erro}); sem retry nem fallback")]
    public static partial void FalhaSemFallback(ILogger logger, string operacao, string erro);

    [LoggerMessage(4, LogLevel.Warning, "SOAP {Operacao}: falha ao encaminhar ao legado ({Erro})")]
    public static partial void FalhaNoLegado(ILogger logger, string operacao, string erro);

    [LoggerMessage(5, LogLevel.Information, "Flags de migração carregadas")]
    public static partial void FlagsCarregadas(ILogger logger);

    [LoggerMessage(6, LogLevel.Error, "Flags de migração indisponíveis ({Motivo}); todo o tráfego vai ao legado")]
    public static partial void FlagsIndisponiveis(ILogger logger, string motivo);

    [LoggerMessage(7, LogLevel.Warning, "Kill switch administrativo {Acao} para {Alcance}")]
    private static partial void KillSwitchAlteradoCore(ILogger logger, string acao, string alcance);

    public static void KillSwitchAlterado(ILogger logger, bool ativado, string alcance) =>
        KillSwitchAlteradoCore(logger, ativado ? "ativado" : "desativado", alcance);

    // Sombra (ADR-0009). A requisição só entra pseudonimizada; as respostas, só como SHA-256.
    [LoggerMessage(20, LogLevel.Debug, "Sombra {Operacao}: igual (trace {TraceId})")]
    public static partial void SombraIgual(ILogger logger, string operacao, string traceId);

    [LoggerMessage(21, LogLevel.Warning,
        "Sombra {Operacao}: divergência {Classe} (regra {Regra}) trace {TraceId} status {StatusLegado}/{StatusNovo} " +
        "sha256 legado {HashLegado} novo {HashNovo} caminhos {Caminhos} requisição {Requisicao} modo {Modo} reprodução {Reproducao}")]
    public static partial void SombraDivergente(
        ILogger logger, string operacao, string classe, string regra, string traceId, int statusLegado, int statusNovo,
        string hashLegado, string hashNovo, string caminhos, string requisicao, string modo, string reproducao);

    [LoggerMessage(22, LogLevel.Warning, "Sombra {Operacao}: divergência erro_novo ({Erro}) trace {TraceId}")]
    public static partial void SombraErroNoNovo(ILogger logger, string operacao, string erro, string traceId);

    [LoggerMessage(23, LogLevel.Debug, "Sombra {Operacao}: cópia descartada ({Motivo})")]
    public static partial void SombraDescartada(ILogger logger, string operacao, string motivo);

    [LoggerMessage(25, LogLevel.Warning,
        "Sombra {Operacao}: divergência {Classe} trace {TraceId} decisão legado {DecisaoLegado} novo {DecisaoNovo} " +
        "status legado {StatusLegado} sha256 legado {HashLegado} requisição {Requisicao} reprodução {Reproducao}")]
    public static partial void SombraCancelamentoDivergente(
        ILogger logger, string operacao, string classe, string traceId, string decisaoLegado, string decisaoNovo,
        int statusLegado, string hashLegado, string requisicao, string reproducao);

    [LoggerMessage(24, LogLevel.Error, "Sombra {Operacao}: falha interna na comparação ({Tipo})")]
    public static partial void SombraFalhaInterna(ILogger logger, string operacao, string tipo);

    // Reprodução cifrada (ADR-0009, fase 2): nunca o conteúdo nem o caminho, só o tipo do erro.
    [LoggerMessage(26, LogLevel.Warning, "Sombra {Operacao}: reprodução cifrada não gravada ({Tipo})")]
    public static partial void ReproducaoNaoGuardada(ILogger logger, string operacao, string tipo);

    [LoggerMessage(27, LogLevel.Warning, "Sombra: limpeza da reprodução cifrada falhou ({Tipo})")]
    public static partial void ReproducaoLimpezaFalhou(ILogger logger, string tipo);

    [LoggerMessage(28, LogLevel.Information, "Sombra: {Apagados} arquivos de reprodução cifrada passaram da retenção e foram apagados")]
    public static partial void ReproducaoLimpeza(ILogger logger, int apagados);
}
