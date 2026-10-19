using Microsoft.Extensions.Options;
using Nfse.Fachada.Configuracao;
using Nfse.Fachada.Observabilidade;

namespace Nfse.Fachada.Flags;

/// <summary>
/// Flags lidas da seção <c>Migracao</c> de um arquivo JSON observado (<c>reloadOnChange</c>), via
/// <see cref="IOptionsMonitor{TOptions}"/>. Seção ausente, arquivo inválido, valor que não converte ou fora da faixa:
/// <c>null</c>, e a decisão manda tudo ao legado.
/// </summary>
public sealed class FonteDeFlagsPorConfiguracao(
    IOptionsMonitor<FlagsDeMigracao> monitor,
    IConfiguration configuracao,
    ILogger<FonteDeFlagsPorConfiguracao> logger) : IFonteDeFlags
{
    private int _disponivel = -1; // -1 desconhecido, 0 não, 1 sim: loga só nas transições

    public string Nome => OpcoesFlags.FonteArquivo;

    public FlagsDeMigracao? Atual()
    {
        try
        {
            IConfigurationSection secao = configuracao.GetSection(FlagsDeMigracao.Secao);
            if (!secao.Exists())
            {
                return Indisponivel("seção ausente");
            }

            FlagsDeMigracao flags = monitor.CurrentValue;
            string? erro = OperacoesDescartadasPeloBinder(secao, flags) ?? flags.Validar().FirstOrDefault();
            if (erro is not null)
            {
                return Indisponivel(erro);
            }

            if (Interlocked.Exchange(ref _disponivel, 1) != 1)
            {
                Log.FlagsCarregadas(logger);
            }

            return flags;
        }
#pragma warning disable CA1031 // fail-safe: qualquer falha na leitura das flags manda o tráfego ao legado
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Indisponivel(ex.GetType().Name);
        }
    }

    // O binder descarta em silêncio o item de dicionário cujo valor não converte (ex.: Modo "talvez"); a operação
    // cairia em legado sem aviso. Tratamos como arquivo inválido.
    private static string? OperacoesDescartadasPeloBinder(IConfigurationSection secao, FlagsDeMigracao flags) =>
        secao.GetSection(nameof(FlagsDeMigracao.Operacoes)).GetChildren()
            .Select(op => op.Key)
            .Where(nome => !flags.Operacoes.ContainsKey(nome))
            .Select(nome => $"{nome}: valor inválido")
            .FirstOrDefault();

    private FlagsDeMigracao? Indisponivel(string motivo)
    {
        if (Interlocked.Exchange(ref _disponivel, 0) != 0)
        {
            Log.FlagsIndisponiveis(logger, motivo);
        }

        return null;
    }
}
