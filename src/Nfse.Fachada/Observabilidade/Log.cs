using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Observabilidade;

/// <summary>Eventos de log estruturados. Nenhum recebe IM, CPF/CNPJ ou corpo (ADR-0012).</summary>
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
}
