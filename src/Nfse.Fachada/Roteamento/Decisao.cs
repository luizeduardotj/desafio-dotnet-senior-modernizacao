namespace Nfse.Fachada.Roteamento;

public enum Destino
{
    Legado,
    Novo,
}

/// <summary>Motivo do roteamento: vai para log, métrica e span (<c>migracao.motivo_roteamento</c>, ADR-0012).</summary>
public enum MotivoDeRoteamento
{
    ForaDoEscopo,
    FlagsIndisponiveis,
    KillSwitch,
    ModoLegado,
    ListaDeExclusao,
    Sombra,
    ListaDeInclusao,
    Percentual,
    ForaDoPercentual,
    ChaveAusente,
    NovoTotalSemChave,
    NovoNaoConfigurado,
    Fallback,
}

/// <param name="Destino">Quem responde ao cliente.</param>
/// <param name="Motivo">Por que esse destino.</param>
/// <param name="ElegivelParaSombra">
/// Em <c>sombra</c>: a requisição passou pelas listas e pode ser copiada para o novo. A amostragem é sorteada depois da
/// resposta do legado (ADR-0009), fora desta decisão.
/// </param>
public readonly record struct Decisao(Destino Destino, MotivoDeRoteamento Motivo, bool ElegivelParaSombra = false);
