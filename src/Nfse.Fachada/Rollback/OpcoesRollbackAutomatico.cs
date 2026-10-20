using System.ComponentModel.DataAnnotations;

namespace Nfse.Fachada.Rollback;

/// <summary>
/// Seção <c>RollbackAutomatico</c>: janela e limites dos gatilhos da ADR-0014, todos [H]. Ligar e desarmar ficam nas
/// flags (<c>RollbackAutomatico.Ligado</c> e <c>.Rearme</c> de cada operação), não aqui.
/// </summary>
public sealed class OpcoesRollbackAutomatico
{
    public const string Secao = "RollbackAutomatico";

    /// <summary>Janela deslizante dos gatilhos estatísticos (plano, §9: "por 15 min").</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan Janela { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Baldes da janela; a janela anda de um balde por vez.</summary>
    [Range(1, 120)]
    public int Baldes { get; set; } = 15;

    /// <summary>Requisições do novo na janela abaixo das quais nenhum gatilho de taxa dispara.</summary>
    [Range(1, int.MaxValue)]
    public int MinimoDeAmostras { get; set; } = 100;

    /// <summary>Requisições em cada lado abaixo das quais o p99 não é comparado (≥ 10 observações acima do p99).</summary>
    [Range(1, int.MaxValue)]
    public int MinimoDeAmostrasDoP99 { get; set; } = 1000;

    /// <summary>Quantil normal dos limites de confiança; 3,29 = 99,9%, porque a regra é reavaliada a cada requisição.</summary>
    [Range(0.5, 10)]
    public double Z { get; set; } = 3.29;

    /// <summary>Fault: dispara se o LI do novo passa o LS do legado mais esta margem (0,1 p.p.).</summary>
    [Range(0.0, 1.0)]
    public double MargemDeFault { get; set; } = 0.001;

    /// <summary>p99: dispara se o LI do p99 do novo passa este múltiplo do LS do p99 do legado.</summary>
    [Range(1.0, 100.0)]
    public double FatorDoP99 { get; set; } = 1.2;

    /// <summary>Fallback: dispara se o LI da fração de tentativas no novo que viraram fallback passa este limite.</summary>
    [Range(0.0, 1.0)]
    public double LimiteDeFallback { get; set; } = 0.05;

    /// <summary>
    /// Por operação: linha de base do legado medida no M0, usada quando o legado não tem amostras na janela (M4).
    /// </summary>
    public Dictionary<string, ReferenciaDoLegado> Referencias { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Linha de base do legado para uma operação (M0).</summary>
public sealed class ReferenciaDoLegado
{
    public TimeSpan? P99 { get; set; }

    /// <summary>Fração (0..1) das requisições com <c>soap:Fault</c>.</summary>
    [Range(0.0, 1.0)]
    public double? TaxaDeFault { get; set; }
}
