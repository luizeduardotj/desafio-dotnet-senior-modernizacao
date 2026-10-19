namespace Nfse.Fachada.Flags;

/// <summary>Origem das flags de migração. Implementações não lançam exceção.</summary>
public interface IFonteDeFlags
{
    /// <summary>Retrato atual, ou <c>null</c> se as flags não puderam ser lidas (fail-safe: tudo no legado).</summary>
    FlagsDeMigracao? Atual();
}
