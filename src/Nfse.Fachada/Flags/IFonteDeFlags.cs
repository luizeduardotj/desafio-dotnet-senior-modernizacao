namespace Nfse.Fachada.Flags;

/// <summary>Origem das flags de migração. Implementações não lançam exceção.</summary>
public interface IFonteDeFlags
{
    /// <summary>Nome da fonte no span (<c>migracao.fonte_flags</c>): <c>arquivo</c> ou <c>openfeature</c>.</summary>
    string Nome { get; }

    /// <summary>Retrato atual, ou <c>null</c> se as flags não puderam ser lidas (fail-safe: tudo no legado).</summary>
    FlagsDeMigracao? Atual();
}
