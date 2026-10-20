using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Nfse.Fachada.Flags;
using Nfse.Fachada.Observabilidade;
using Nfse.Fachada.Roteamento;

namespace Nfse.Fachada.Rollback;

/// <summary>
/// Rollback automático desta réplica (ADR-0014): observa o que a fachada já mede por requisição, dispara quando um
/// gatilho do plano (§9) passa do limite e, a partir daí, manda ao legado o que iria ao novo. Só um humano desarma,
/// trocando <c>RollbackAutomatico.Rearme</c> nas flags da operação.
/// </summary>
/// <remarks>
/// Estado em memória, por réplica, sem coordenação: o disparo emite <c>rollback_automatico_ativo</c>, e o alerta sobre
/// ele leva o plantão ao kill switch nível 1, que é global e sobrevive a restart.
/// </remarks>
public sealed class RollbackAutomatico : IDisposable
{
    private readonly TimeProvider _relogio;
    private readonly IOptionsMonitor<OpcoesRollbackAutomatico> _opcoes;
    private readonly ILogger<RollbackAutomatico> _logger;
    private readonly Dictionary<string, EstadoDaOperacao> _operacoes = new(StringComparer.Ordinal);
    private readonly Meter _medidor;
    private readonly Counter<long> _disparos;

    public RollbackAutomatico(
        TimeProvider relogio,
        IOptionsMonitor<OpcoesRollbackAutomatico> opcoes,
        IFonteDeFlags flags,
        IMeterFactory fabrica,
        ILogger<RollbackAutomatico> logger)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(fabrica);
        _relogio = relogio;
        _opcoes = opcoes;
        _logger = logger;

        OpcoesRollbackAutomatico o = opcoes.CurrentValue;
        foreach (string operacao in OperacoesSoap.EmEscopo)
        {
            _operacoes[operacao] = new EstadoDaOperacao(o.Janela, o.Baldes);
        }

        // Meter do IMeterFactory, como em MedidoresDeEstado: depende do estado desta réplica.
        _medidor = fabrica.Create(Telemetria.Nome);
        _disparos = _medidor.CreateCounter<long>(
            "rollback_automatico_disparos_total", description: "Disparos do rollback automático desta réplica, por operação e gatilho (ADR-0014).");
        _medidor.CreateObservableGauge(
            "rollback_automatico_ativo", () => Ativos(flags.Atual()),
            description: "1 enquanto o rollback automático da operação vale nesta réplica (alerta: aplicar o kill switch nível 1).");
    }

    /// <summary>
    /// Só move para o legado: uma decisão <c>novo</c> de operação disparada vira <c>legado</c>; qualquer outra passa
    /// intacta. Nunca cria uma decisão <c>novo</c>, e o modo <c>sombra</c> (já <c>legado</c>) continua copiando.
    /// </summary>
    public static Decisao Aplicar(Decisao decisao, bool disparado) =>
        disparado && decisao.Destino == Destino.Novo ? new(Destino.Legado, MotivoDeRoteamento.RollbackAutomatico) : decisao;

    /// <summary>Se o disparo vale para a operação. A troca do <c>Rearme</c> nas flags desarma aqui e zera a janela.</summary>
    /// <param name="flags">Flags da operação; <c>null</c> se indisponíveis (aí tudo já vai ao legado e nada muda).</param>
    public bool Disparado(string operacao, FlagsDaOperacao? flags)
    {
        if (!_operacoes.TryGetValue(operacao, out EstadoDaOperacao? estado) || estado.Disparo is not { } disparo)
        {
            return false;
        }

        if (flags is null || MesmoRearme(flags, disparo))
        {
            return true;
        }

        lock (estado.Trava)
        {
            if (ReferenceEquals(estado.Disparo, disparo))
            {
                estado.Disparo = null;
                estado.Novo.Zerar();
                estado.Legado.Zerar();
                Log.RollbackDesarmado(_logger, operacao, AvaliadorDeGatilhos.Nome(disparo.Gatilho));
            }
        }

        return false;
    }

    /// <summary>Registra o resultado de uma requisição em escopo e avalia os gatilhos quando ela é do lado novo.</summary>
    public void Observar(string operacao, FlagsDaOperacao flags, Observacao observacao)
    {
        ArgumentNullException.ThrowIfNull(flags);
        if (!flags.RollbackAutomatico.Ligado || !_operacoes.TryGetValue(operacao, out EstadoDaOperacao? estado))
        {
            return;
        }

        lock (estado.Trava)
        {
            // Disparada, a operação não manda nada ao novo; a janela recomeça vazia no desarme.
            if (estado.Disparo is not null)
            {
                return;
            }

            DateTimeOffset agora = _relogio.GetUtcNow();
            bool novo = observacao.Lado == Destino.Novo;
            (novo ? estado.Novo : estado.Legado).Registrar(agora, observacao.Fault, observacao.Fallback, observacao.Duracao);
            if (!novo)
            {
                return;
            }

            OpcoesRollbackAutomatico o = _opcoes.CurrentValue;
            Disparo? disparo = observacao.RegraDivergente
                ? new Disparo(GatilhoDeRollback.RegraDivergente, "dual-run nao_classificada na resposta do novo")
                : AvaliadorDeGatilhos.Avaliar(
                    estado.Novo.Somar(agora), estado.Legado.Somar(agora), o.Referencias.GetValueOrDefault(operacao), o);
            if (disparo is not { } d)
            {
                return;
            }

            estado.Disparo = new DisparoAtivo(d.Gatilho, d.Detalhe, agora, flags.RollbackAutomatico.Rearme ?? "");
            string gatilho = AvaliadorDeGatilhos.Nome(d.Gatilho);
            _disparos.Add(1, new KeyValuePair<string, object?>("operacao", operacao), new KeyValuePair<string, object?>("gatilho", gatilho));
            Log.RollbackDisparado(_logger, operacao, gatilho, d.Detalhe, estado.Disparo.Rearme);
        }
    }

    /// <summary>Estado desta réplica, para <c>GET /admin/rollback-automatico</c>.</summary>
    public IReadOnlyList<EstadoDoRollback> Estado(FlagsDeMigracao? flags) =>
        [.. OperacoesSoap.EmEscopo.Select(operacao =>
        {
            DisparoAtivo? d = _operacoes[operacao].Disparo;
            FlagsDaOperacao? op = flags?.Da(operacao);
            return new EstadoDoRollback(
                operacao,
                op?.RollbackAutomatico.Ligado ?? false,
                d is not null && (op is null || MesmoRearme(op, d)),
                d is null ? null : AvaliadorDeGatilhos.Nome(d.Gatilho),
                d?.Detalhe,
                d?.Instante,
                d?.Rearme);
        })];

    public void Dispose() => _medidor.Dispose();

    private IEnumerable<Measurement<long>> Ativos(FlagsDeMigracao? flags) =>
        Estado(flags).Select(e => new Measurement<long>(e.Disparado ? 1 : 0, new KeyValuePair<string, object?>("operacao", e.Operacao)));

    private static bool MesmoRearme(FlagsDaOperacao flags, DisparoAtivo disparo) =>
        string.Equals(flags.RollbackAutomatico.Rearme ?? "", disparo.Rearme, StringComparison.Ordinal);

    private sealed class EstadoDaOperacao(TimeSpan janela, int baldes)
    {
        public Lock Trava { get; } = new();

        public JanelaDeObservacao Novo { get; } = new(janela, baldes);

        public JanelaDeObservacao Legado { get; } = new(janela, baldes);

        // Lido sem lock no caminho de cada requisição; escrito sob a trava.
        private volatile DisparoAtivo? _disparo;

        public DisparoAtivo? Disparo
        {
            get => _disparo;
            set => _disparo = value;
        }
    }

    private sealed record DisparoAtivo(GatilhoDeRollback Gatilho, string Detalhe, DateTimeOffset Instante, string Rearme);
}

/// <param name="Lado">Quem foi chamado: <c>novo</c> (atendeu ou falhou e virou fallback) ou <c>legado</c>.</param>
/// <param name="Fault">Resposta não 2xx entregue ao cliente por esse lado.</param>
/// <param name="Fallback">Tentativa no novo que foi ao legado.</param>
/// <param name="Duracao">Tempo na chamada a esse lado (no fallback, até a falha do novo).</param>
/// <param name="RegraDivergente">O novo sinalizou o dual-run <c>nao_classificada</c> (header <c>X-Nfse-Dual-Run</c>).</param>
public readonly record struct Observacao(Destino Lado, bool Fault, bool Fallback, TimeSpan Duracao, bool RegraDivergente = false);

public sealed record EstadoDoRollback(
    string Operacao, bool Ligado, bool Disparado, string? Gatilho, string? Detalhe, DateTimeOffset? Instante, string? Rearme);
