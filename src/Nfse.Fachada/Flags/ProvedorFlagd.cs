using Nfse.Fachada.Configuracao;
using OpenFeature.Contrib.Providers.Flagd;

namespace Nfse.Fachada.Flags;

/// <summary>
/// Provider flagd em modo <b>in-process</b>, a partir de <c>Flags:Flagd</c> (o que ficar vazio vem das variáveis
/// <c>FLAGD_*</c>). O provider recebe o conjunto de flags pelo stream de sync do flagd e avalia na própria fachada.
/// </summary>
/// <remarks>
/// Por que não o resolvedor RPC (0.4.0): ele desiste do stream de eventos depois de
/// <c>FLAGD_MAX_EVENT_STREAM_RETRIES</c> tentativas (3) e não emite mais nada; o retrato da
/// <see cref="FonteDeFlagsOpenFeature"/>, refeito a cada evento, ficaria congelado sem erro, e um kill switch não
/// chegaria. O in-process tenta de novo indefinidamente (backoff até 60 s) e emite erro a cada falha.
/// </remarks>
public static class ProvedorFlagd
{
    public static FlagdProvider Criar(OpcoesFlagd opcoes, ILogger logger)
    {
        FlagdConfigBuilder config = FlagdConfig.Builder()
            .WithLogger(logger)
            .WithResolverType(ResolverType.IN_PROCESS);

        if (!string.IsNullOrWhiteSpace(opcoes.Host))
        {
            config = config.WithHost(opcoes.Host);
        }

        if (opcoes.Porta is { } porta)
        {
            config = config.WithPort(porta);
        }

        if (!string.IsNullOrWhiteSpace(opcoes.Seletor))
        {
            config = config.WithSourceSelector(opcoes.Seletor);
        }

        return new FlagdProvider(config.Build());
    }
}
