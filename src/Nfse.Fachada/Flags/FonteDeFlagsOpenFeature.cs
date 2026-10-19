using System.Globalization;
using System.Threading.Channels;
using Nfse.Fachada.Configuracao;
using Nfse.Fachada.Observabilidade;
using Nfse.Fachada.Roteamento;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;

namespace Nfse.Fachada.Flags;

/// <summary>
/// Flags de migração avaliadas pelo OpenFeature (provider flagd em produção), no mesmo retrato
/// <see cref="FlagsDeMigracao"/> da fonte por arquivo. Chaves, as mesmas do <c>FeatureFlag</c> do overlay de produção:
/// <c>migracao.kill-switch</c> (booleano), e por operação em escopo <c>&lt;Operacao&gt;.kill-switch</c> (booleano) e
/// <c>&lt;Operacao&gt;.config</c> (objeto com <c>Modo</c>, <c>Percentual</c>, <c>Inclusao</c>, <c>Exclusao</c> e
/// <c>Sombra.Amostragem</c>, como no arquivo).
/// </summary>
/// <remarks>
/// O retrato é montado fora do caminho da requisição, a cada evento do provider (pronto ou configuração alterada, que o
/// flagd emite pelo stream de sync): <see cref="Atual"/> só lê dois campos. Fail-safe igual ao do arquivo: provider
/// sem retrato ou em erro, flag ausente ou desabilitada, tipo errado, campo desconhecido ou valor fora da faixa
/// devolvem <c>null</c> (tudo ao legado, <c>/health/ready</c> 503).
/// </remarks>
public sealed class FonteDeFlagsOpenFeature : BackgroundService, IFonteDeFlags
{
    /// <summary>Domínio do OpenFeature onde a fachada registra o provider (o <see cref="Api"/> é global).</summary>
    public const string DominioPadrao = "nfse-fachada";

    public const string ChaveKillSwitchGlobal = "migracao.kill-switch";

    private readonly FeatureProvider _provedor;
    private readonly string _dominio;
    private readonly FeatureClient _cliente;
    private readonly ILogger<FonteDeFlagsOpenFeature> _logger;

    // Um sinal pendente basta: cada leitura avalia todas as flags.
    private readonly Channel<bool> _sinais = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private volatile Leitura _leitura = new(null, "provider ainda não leu as flags");
    private volatile bool _providerEmErro;
    private int _disponivel = -1; // -1 desconhecido, 0 não, 1 sim: loga só nas transições
    private int _leituras;

    public FonteDeFlagsOpenFeature(FeatureProvider provedor, string dominio, ILogger<FonteDeFlagsOpenFeature> logger)
    {
        _provedor = provedor;
        _dominio = dominio;
        _logger = logger;
        _cliente = Api.Instance.GetClient(dominio);
    }

    public string Nome => OpcoesFlags.FonteOpenFeature;

    /// <summary>Retratos montados desde a partida (para os testes saberem que o provider já foi lido).</summary>
    internal int Leituras => Volatile.Read(ref _leituras);

    public static string ChaveKillSwitch(string operacao) => $"{operacao}.kill-switch";

    public static string ChaveConfig(string operacao) => $"{operacao}.config";

    public FlagsDeMigracao? Atual()
    {
        ProviderStatus status = _cliente.ProviderStatus;
        if (status is ProviderStatus.NotReady or ProviderStatus.Fatal)
        {
            return Indisponivel($"provider {status}");
        }

        // Erro do provider (flagd fora, stream caído) vale na hora, mesmo com um retrato anterior em memória.
        if (_providerEmErro)
        {
            return Indisponivel("provider em erro");
        }

        Leitura leitura = _leitura;
        if (leitura.Flags is null)
        {
            return Indisponivel(leitura.Erro ?? "sem retrato");
        }

        if (Interlocked.Exchange(ref _disponivel, 1) != 1)
        {
            Log.FlagsCarregadas(_logger);
        }

        return leitura.Flags;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        _sinais.Writer.TryComplete();
        try
        {
            await _provedor.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // encerramento: falha do provider não impede o shutdown da fachada
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Não segura a partida: até o provider ficar pronto, Atual() devolve null (tudo ao legado).
            await Api.Instance.SetProviderAsync(_dominio, _provedor, stoppingToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // fail-safe: o status do provider fica em erro e Atual() devolve null
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _leitura = new(null, $"inicialização do provider: {ex.GetType().Name}");
        }

        // O erro é acompanhado pelos eventos, não pelo status do SDK: o flagd in-process, ao reconectar, emite só
        // ConfigurationChanged, que não tira o status de Error. Configuração recebida = provider saudável de novo.
        // Handlers depois do SetProviderAsync: antes dele o domínio responde com o provider padrão (no-op), também
        // "pronto". Handler registrado com o provider já pronto é chamado na hora.
        _providerEmErro = _cliente.ProviderStatus is ProviderStatus.Error;
        _cliente.AddHandler(ProviderEventTypes.ProviderError, _ => _providerEmErro = true);
        _cliente.AddHandler(ProviderEventTypes.ProviderReady, Recuperar);
        _cliente.AddHandler(ProviderEventTypes.ProviderConfigurationChanged, Recuperar);
        _sinais.Writer.TryWrite(true);

        await foreach (bool _ in _sinais.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            _leitura = await LerAsync(stoppingToken).ConfigureAwait(false);
            Interlocked.Increment(ref _leituras);
        }
    }

    private void Recuperar(ProviderEventPayload? evento)
    {
        _providerEmErro = false;
        _sinais.Writer.TryWrite(true);
    }

    private async Task<Leitura> LerAsync(CancellationToken cancelamento)
    {
        try
        {
            var flags = new FlagsDeMigracao();

            (bool killSwitchGlobal, string? erro) = await BooleanoAsync(ChaveKillSwitchGlobal, cancelamento).ConfigureAwait(false);
            if (erro is not null)
            {
                return new(null, erro);
            }

            flags.KillSwitch = killSwitchGlobal;

            foreach (string operacao in OperacoesSoap.EmEscopo)
            {
                (bool killSwitch, erro) = await BooleanoAsync(ChaveKillSwitch(operacao), cancelamento).ConfigureAwait(false);
                if (erro is not null)
                {
                    return new(null, erro);
                }

                string chave = ChaveConfig(operacao);
                FlagEvaluationDetails<Value> config = await _cliente
                    .GetObjectDetailsAsync(chave, new Value(), cancellationToken: cancelamento).ConfigureAwait(false);
                if (FalhaDaAvaliacao(chave, config.ErrorType, config.Reason) is { } falha)
                {
                    return new(null, falha);
                }

                if (Converter(config.Value, out erro) is not { } daOperacao)
                {
                    return new(null, $"{chave}: {erro}");
                }

                daOperacao.KillSwitch = killSwitch;
                flags.Operacoes[operacao] = daOperacao;
            }

            erro = flags.Validar().FirstOrDefault();
            return erro is null ? new(flags, null) : new(null, erro);
        }
#pragma warning disable CA1031 // fail-safe: qualquer falha na leitura das flags manda o tráfego ao legado
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return new(null, ex.GetType().Name);
        }
    }

    private async Task<(bool Valor, string? Erro)> BooleanoAsync(string chave, CancellationToken cancelamento)
    {
        FlagEvaluationDetails<bool> detalhes = await _cliente
            .GetBooleanDetailsAsync(chave, false, cancellationToken: cancelamento).ConfigureAwait(false);
        return (detalhes.Value, FalhaDaAvaliacao(chave, detalhes.ErrorType, detalhes.Reason));
    }

    // Flag ausente (FlagNotFound), tipo errado (TypeMismatch) ou provider fora: o SDK devolve o padrão com ErrorType.
    // Flag desabilitada devolve o padrão sem erro; também não serve, porque o padrão do código não é decisão de ninguém.
    private static string? FalhaDaAvaliacao(string chave, ErrorType erro, string? motivo) =>
        erro != ErrorType.None ? $"{chave}: {erro}"
        : string.Equals(motivo, Reason.Disabled, StringComparison.Ordinal) ? $"{chave}: desabilitada"
        : null;

    /// <summary>
    /// Objeto <c>&lt;Operacao&gt;.config</c> no formato da operação do arquivo. Campo ausente fica no padrão, como no
    /// arquivo; campo desconhecido ou de tipo errado invalida o retrato (no arquivo, o binder também recusa o que não
    /// converte).
    /// </summary>
    internal static FlagsDaOperacao? Converter(Value valor, out string? erro)
    {
        erro = null;
        if (!valor.IsStructure)
        {
            erro = "não é objeto";
            return null;
        }

        var op = new FlagsDaOperacao();
        foreach ((string campo, Value v) in valor.AsStructure!.AsDictionary())
        {
            switch (campo)
            {
                case nameof(FlagsDaOperacao.Modo) when TentarModo(v, out ModoDeMigracao modo):
                    op.Modo = modo;
                    break;
                case nameof(FlagsDaOperacao.Percentual) when TentarInteiro(v, out int percentual):
                    op.Percentual = percentual;
                    break;
                case nameof(FlagsDaOperacao.Inclusao) when TentarLista(v, out string[] inclusao):
                    op.Inclusao = inclusao;
                    break;
                case nameof(FlagsDaOperacao.Exclusao) when TentarLista(v, out string[] exclusao):
                    op.Exclusao = exclusao;
                    break;
                case nameof(FlagsDaOperacao.Sombra) when TentarSombra(v, out FlagsDaSombra sombra):
                    op.Sombra = sombra;
                    break;
                default:
                    erro = $"campo {campo} inválido";
                    return null;
            }
        }

        return op;
    }

    private static bool TentarModo(Value v, out ModoDeMigracao modo)
    {
        modo = ModoDeMigracao.Legado;
        switch (v.IsString ? v.AsString!.ToLowerInvariant() : null)
        {
            case "legado":
                return true;
            case "sombra":
                modo = ModoDeMigracao.Sombra;
                return true;
            case "novo":
                modo = ModoDeMigracao.Novo;
                return true;
            default:
                return false;
        }
    }

    // JSON não distingue inteiro de real: 5.0 serve, 5.5 não.
    private static bool TentarInteiro(Value v, out int inteiro)
    {
        inteiro = 0;
        if (!v.IsNumber || v.AsDouble is not { } d || d != Math.Floor(d) || d is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        inteiro = (int)d;
        return true;
    }

    /// <summary>
    /// Lista JSON de textos ou texto separado por vírgulas (<c>"10000001,10000002"</c>; vazio = lista vazia). O texto
    /// existe porque o provider flagd in-process (OpenFeature.Contrib.Providers.Flagd 0.4.0 e 0.5.0) devolve
    /// <c>TYPE_MISMATCH</c> para flag de objeto que contém lista; é o formato usado no flagd.
    /// </summary>
    private static bool TentarLista(Value v, out string[] itens)
    {
        itens = [];
        if (v.IsString)
        {
            itens = v.AsString!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return true;
        }

        if (!v.IsList || v.AsList!.Any(item => !item.IsString))
        {
            return false;
        }

        itens = v.AsList!.Select(item => item.AsString!).ToArray();
        return true;
    }

    private static bool TentarSombra(Value v, out FlagsDaSombra sombra)
    {
        sombra = new FlagsDaSombra();
        if (!v.IsStructure)
        {
            return false;
        }

        foreach ((string campo, Value valor) in v.AsStructure!.AsDictionary())
        {
            if (!string.Equals(campo, nameof(FlagsDaSombra.Amostragem), StringComparison.Ordinal)
                || !TentarInteiro(valor, out int amostragem))
            {
                return false;
            }

            sombra.Amostragem = amostragem;
        }

        return true;
    }

    private FlagsDeMigracao? Indisponivel(string motivo)
    {
        if (Interlocked.Exchange(ref _disponivel, 0) != 0)
        {
            Log.FlagsIndisponiveis(_logger, motivo);
        }

        return null;
    }

    private sealed record Leitura(FlagsDeMigracao? Flags, string? Erro);
}
