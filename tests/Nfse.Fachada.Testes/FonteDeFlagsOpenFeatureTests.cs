using Microsoft.Extensions.Logging.Abstractions;
using Nfse.Fachada.Flags;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using OpenFeature.Providers.Memory;

namespace Nfse.Fachada.Testes;

/// <summary>
/// Fonte OpenFeature com o <see cref="InMemoryProvider"/> no lugar do flagd. Cada teste usa um domínio próprio do
/// OpenFeature (o <see cref="Api"/> é global), então podem rodar em paralelo.
/// </summary>
public sealed class FonteDeFlagsOpenFeatureTests : IAsyncDisposable
{
    private readonly List<FonteDeFlagsOpenFeature> _fontes = [];

    [Fact]
    public async Task Monta_o_retrato_das_flags()
    {
        var flags = Padrao();
        flags["ConsultarNfse.config"] = Config(Operacao("novo", 25, inclusao: ["10000001"], exclusao: ["20000002"]));
        flags["CancelarNfse.config"] = Config(Operacao("sombra", 0, amostragem: 10));

        FlagsDeMigracao? retrato = await LerAsync(new InMemoryProvider(flags));

        Assert.NotNull(retrato);
        Assert.False(retrato.KillSwitch);
        FlagsDaOperacao consultar = retrato.Da("ConsultarNfse");
        Assert.Equal(ModoDeMigracao.Novo, consultar.Modo);
        Assert.Equal(25, consultar.Percentual);
        Assert.Equal(["10000001"], consultar.Inclusao);
        Assert.Equal(["20000002"], consultar.Exclusao);
        Assert.False(consultar.KillSwitch);
        Assert.Equal(ModoDeMigracao.Sombra, retrato.Da("CancelarNfse").Modo);
        Assert.Equal(10, retrato.Da("CancelarNfse").Sombra.Amostragem);
        Assert.Equal(ModoDeMigracao.Legado, retrato.Da("ConsultarNfseServicoTomado").Modo);
    }

    [Fact]
    public async Task Listas_como_texto_separado_por_virgulas_formato_do_flagd()
    {
        // O provider flagd in-process recusa flag de objeto com lista (TYPE_MISMATCH); no flagd as listas vão como texto.
        var flags = Padrao();
        flags["ConsultarNfse.config"] = Config(Structure.Builder()
            .Set("Modo", "novo").Set("Inclusao", "10000001, 10000002").Set("Exclusao", "").Build());

        FlagsDaOperacao? consultar = (await LerAsync(new InMemoryProvider(flags)))?.Da("ConsultarNfse");

        Assert.NotNull(consultar);
        Assert.Equal(["10000001", "10000002"], consultar.Inclusao);
        Assert.Empty(consultar.Exclusao);
    }

    [Fact]
    public async Task Le_o_rollback_automatico_da_operacao_e_ausente_fica_desligado()
    {
        var flags = Padrao();
        flags["CancelarNfse.config"] = Config(Structure.Builder()
            .Set("Modo", "novo").Set("Percentual", 5)
            .Set("RollbackAutomatico", Structure.Builder().Set("Ligado", true).Set("Rearme", "2026-10-20-incidente-42").Build())
            .Build());

        FlagsDeMigracao? retrato = await LerAsync(new InMemoryProvider(flags));

        Assert.NotNull(retrato);
        Assert.True(retrato.Da("CancelarNfse").RollbackAutomatico.Ligado);
        Assert.Equal("2026-10-20-incidente-42", retrato.Da("CancelarNfse").RollbackAutomatico.Rearme);
        Assert.False(retrato.Da("ConsultarNfse").RollbackAutomatico.Ligado);
        Assert.Equal("", retrato.Da("ConsultarNfse").RollbackAutomatico.Rearme);
    }

    [Fact]
    public async Task Rollback_automatico_com_tipo_errado_invalida_o_retrato()
    {
        var flags = Padrao();
        flags["CancelarNfse.config"] = Config(Structure.Builder()
            .Set("Modo", "novo").Set("RollbackAutomatico", Structure.Builder().Set("Ligado", "sim").Build()).Build());

        Assert.Null(await LerAsync(new InMemoryProvider(flags)));
    }

    [Fact]
    public async Task Le_os_kill_switches_global_e_da_operacao()
    {
        var flags = Padrao();
        flags["migracao.kill-switch"] = Booleano(true);
        flags["CancelarNfse.kill-switch"] = Booleano(true);

        FlagsDeMigracao? retrato = await LerAsync(new InMemoryProvider(flags));

        Assert.NotNull(retrato);
        Assert.True(retrato.KillSwitch);
        Assert.True(retrato.Da("CancelarNfse").KillSwitch);
        Assert.False(retrato.Da("ConsultarNfse").KillSwitch);
    }

    [Fact]
    public async Task Campos_ausentes_no_objeto_ficam_no_padrao_como_no_arquivo()
    {
        var flags = Padrao();
        flags["ConsultarNfse.config"] = Config(Structure.Builder().Set("Modo", "Novo").Build());

        FlagsDaOperacao? consultar = (await LerAsync(new InMemoryProvider(flags)))?.Da("ConsultarNfse");

        Assert.NotNull(consultar);
        Assert.Equal(ModoDeMigracao.Novo, consultar.Modo);
        Assert.Equal(0, consultar.Percentual);
        Assert.Empty(consultar.Inclusao);
        Assert.Equal(0, consultar.Sombra.Amostragem);
    }

    [Fact]
    public async Task Recarrega_quando_as_flags_mudam_no_provider()
    {
        var provedor = new InMemoryProvider(Padrao());
        FonteDeFlagsOpenFeature fonte = await IniciarAsync(provedor);
        Assert.Equal(ModoDeMigracao.Legado, fonte.Atual()!.Da("ConsultarNfse").Modo);

        var novas = Padrao();
        novas["ConsultarNfse.config"] = Config(Operacao("novo", 10));
        await provedor.UpdateFlagsAsync(novas);

        Assert.True(await Aguardar(() => fonte.Atual()?.Da("ConsultarNfse").Modo == ModoDeMigracao.Novo),
            "as flags não recarregaram em 15 s");

        novas["migracao.kill-switch"] = Booleano(true);
        await provedor.UpdateFlagsAsync(novas);

        Assert.True(await Aguardar(() => fonte.Atual()?.KillSwitch == true), "o kill switch não chegou em 15 s");
    }

    [Theory]
    [InlineData("kill switch global ausente")]
    [InlineData("config da operação ausente")]
    [InlineData("kill switch com tipo errado")]
    [InlineData("config que não é objeto")]
    [InlineData("flag desabilitada")]
    [InlineData("modo desconhecido")]
    [InlineData("percentual acima de 100")]
    [InlineData("percentual fracionário")]
    [InlineData("percentual texto")]
    [InlineData("amostragem negativa")]
    [InlineData("campo desconhecido")]
    [InlineData("lista com número")]
    [InlineData("sombra invertida no cancelamento")]
    [InlineData("invertida não booleana")]
    public async Task Flags_ausentes_ou_invalidas_viram_null(string caso)
    {
        var flags = Padrao();
        switch (caso)
        {
            case "kill switch global ausente":
                flags.Remove("migracao.kill-switch");
                break;
            case "config da operação ausente":
                flags.Remove("CancelarNfse.config");
                break;
            case "kill switch com tipo errado":
                flags["ConsultarNfse.kill-switch"] = new Flag<string>(new() { ["sim"] = "sim" }, "sim");
                break;
            case "config que não é objeto":
                flags["ConsultarNfse.config"] = new Flag<Value>(new() { ["x"] = new Value("novo") }, "x");
                break;
            case "flag desabilitada":
                flags["ConsultarNfse.kill-switch"] = new Flag<bool>(new() { ["ligado"] = true, ["desligado"] = false }, "desligado", disabled: true);
                break;
            case "modo desconhecido":
                flags["ConsultarNfse.config"] = Config(Operacao("talvez", 0));
                break;
            case "percentual acima de 100":
                flags["ConsultarNfse.config"] = Config(Operacao("novo", 101));
                break;
            case "percentual fracionário":
                flags["ConsultarNfse.config"] = Config(Structure.Builder().Set("Modo", "novo").Set("Percentual", 2.5).Build());
                break;
            case "percentual texto":
                flags["ConsultarNfse.config"] = Config(Structure.Builder().Set("Modo", "novo").Set("Percentual", "muito").Build());
                break;
            case "amostragem negativa":
                flags["ConsultarNfse.config"] = Config(Operacao("sombra", 0, amostragem: -1));
                break;
            case "campo desconhecido":
                flags["ConsultarNfse.config"] = Config(Structure.Builder().Set("Modo", "novo").Set("Percentagem", 5).Build());
                break;
            case "lista com número":
                flags["ConsultarNfse.config"] = Config(Structure.Builder()
                    .Set("Modo", "novo").Set("Inclusao", new List<Value> { new(10000001) }).Build());
                break;
            case "sombra invertida no cancelamento":
                flags["CancelarNfse.config"] = Config(Structure.Builder().Set("Modo", "novo").Set("Percentual", 100)
                    .Set("Sombra", Structure.Builder().Set("Invertida", true).Build()).Build());
                break;
            case "invertida não booleana":
                flags["ConsultarNfse.config"] = Config(Structure.Builder().Set("Modo", "novo")
                    .Set("Sombra", Structure.Builder().Set("Invertida", "sim").Build()).Build());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caso), caso, null);
        }

        Assert.Null(await LerAsync(new InMemoryProvider(flags)));
    }

    [Fact]
    public async Task Sombra_invertida_das_leituras_vem_do_objeto_da_operacao()
    {
        var flags = Padrao();
        flags["ConsultarNfse.config"] = Config(Structure.Builder().Set("Modo", "novo").Set("Percentual", 100)
            .Set("Sombra", Structure.Builder().Set("Amostragem", 5).Set("Invertida", true).Build()).Build());

        FlagsDeMigracao? retrato = await LerAsync(new InMemoryProvider(flags));

        Assert.NotNull(retrato);
        Assert.True(retrato.Da("ConsultarNfse").Sombra.Invertida);
        Assert.Equal(5, retrato.Da("ConsultarNfse").Sombra.Amostragem);
    }

    [Fact]
    public async Task Provider_que_nao_inicializa_vira_null()
    {
        var provedor = new ProvedorControlavel(Padrao()) { FalharNaInicializacao = true };

        Assert.Null(await LerAsync(provedor));
    }

    [Fact]
    public async Task Provider_em_erro_vira_null_mesmo_com_retrato_anterior_e_volta_quando_fica_pronto()
    {
        var provedor = new ProvedorControlavel(Padrao());
        FonteDeFlagsOpenFeature fonte = await IniciarAsync(provedor);
        Assert.NotNull(fonte.Atual());

        provedor.Emitir(ProviderEventTypes.ProviderError);
        Assert.True(await Aguardar(() => fonte.Atual() is null), "o erro do provider não levou ao fail-safe");

        provedor.Emitir(ProviderEventTypes.ProviderReady);
        Assert.True(await Aguardar(() => fonte.Atual() is not null), "as flags não voltaram com o provider pronto");
    }

    [Fact]
    public async Task Provider_que_reconecta_so_com_configuracao_alterada_volta_a_valer()
    {
        // O flagd in-process (0.4.0), ao reconectar o sync, emite ConfigurationChanged e não Ready; o status do SDK fica
        // em Error. A fonte não pode ficar presa no fail-safe por isso.
        var provedor = new ProvedorControlavel(Padrao());
        FonteDeFlagsOpenFeature fonte = await IniciarAsync(provedor);

        provedor.Emitir(ProviderEventTypes.ProviderError);
        Assert.True(await Aguardar(() => fonte.Atual() is null), "o erro do provider não levou ao fail-safe");

        provedor.Emitir(ProviderEventTypes.ProviderConfigurationChanged);
        Assert.True(await Aguardar(() => fonte.Atual() is not null), "as flags não voltaram com a configuração recebida");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (FonteDeFlagsOpenFeature fonte in _fontes)
        {
            await fonte.StopAsync(CancellationToken.None);
            fonte.Dispose();
        }
    }

    private async Task<FlagsDeMigracao?> LerAsync(FeatureProvider provedor)
    {
        FonteDeFlagsOpenFeature fonte = await IniciarAsync(provedor);
        return fonte.Atual();
    }

    /// <summary>Inicia a fonte e espera o primeiro retrato montado com o provider do teste.</summary>
    private async Task<FonteDeFlagsOpenFeature> IniciarAsync(FeatureProvider provedor)
    {
        var fonte = new FonteDeFlagsOpenFeature(provedor, $"teste-{Guid.NewGuid():N}", NullLogger<FonteDeFlagsOpenFeature>.Instance);
        _fontes.Add(fonte);
        await fonte.StartAsync(CancellationToken.None);
        Assert.True(await Aguardar(() => fonte.Leituras > 0), "a fonte não leu o provider em 15 s");
        return fonte;
    }

    private static Dictionary<string, Flag> Padrao() => new()
    {
        ["migracao.kill-switch"] = Booleano(false),
        ["ConsultarNfse.kill-switch"] = Booleano(false),
        ["ConsultarNfse.config"] = Config(Operacao("legado", 0)),
        ["ConsultarNfseServicoTomado.kill-switch"] = Booleano(false),
        ["ConsultarNfseServicoTomado.config"] = Config(Operacao("legado", 0)),
        ["CancelarNfse.kill-switch"] = Booleano(false),
        ["CancelarNfse.config"] = Config(Operacao("legado", 0)),
    };

    // Mesmo formato do FeatureFlag nfse-migracao (deploy/k8s/overlays/producao/flagd/featureflag.yaml).
    private static Flag<bool> Booleano(bool valor) =>
        new(new() { ["ligado"] = true, ["desligado"] = false }, valor ? "ligado" : "desligado");

    private static Flag<Value> Config(Structure operacao) => new(new() { ["atual"] = new Value(operacao) }, "atual");

    private static Structure Operacao(string modo, int percentual, string[]? inclusao = null, string[]? exclusao = null, int amostragem = 0) =>
        Structure.Builder()
            .Set("Modo", modo)
            .Set("Percentual", percentual)
            .Set("Inclusao", (inclusao ?? []).Select(i => new Value(i)).ToList())
            .Set("Exclusao", (exclusao ?? []).Select(e => new Value(e)).ToList())
            .Set("Sombra", Structure.Builder().Set("Amostragem", amostragem).Build())
            .Build();

    private static async Task<bool> Aguardar(Func<bool> condicao)
    {
        for (int i = 0; i < 150; i++)
        {
            if (condicao())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>Provider em memória que pode falhar na inicialização e emitir eventos de status, como o flagd.</summary>
    private sealed class ProvedorControlavel(Dictionary<string, Flag> flags) : FeatureProvider
    {
        private readonly InMemoryProvider _flags = new(flags);

        public bool FalharNaInicializacao { get; init; }

        public void Emitir(ProviderEventTypes tipo) =>
            EventChannel.Writer.TryWrite(new ProviderEventPayload { Type = tipo, ProviderName = nameof(ProvedorControlavel) });

        public override Metadata GetMetadata() => new(nameof(ProvedorControlavel));

        public override Task InitializeAsync(EvaluationContext context, CancellationToken cancellationToken = default) =>
            FalharNaInicializacao ? throw new InvalidOperationException("flagd fora") : Task.CompletedTask;

        public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(string flagKey, bool defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
            _flags.ResolveBooleanValueAsync(flagKey, defaultValue, context, cancellationToken);

        public override Task<ResolutionDetails<string>> ResolveStringValueAsync(string flagKey, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
            _flags.ResolveStringValueAsync(flagKey, defaultValue, context, cancellationToken);

        public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(string flagKey, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
            _flags.ResolveIntegerValueAsync(flagKey, defaultValue, context, cancellationToken);

        public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(string flagKey, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
            _flags.ResolveDoubleValueAsync(flagKey, defaultValue, context, cancellationToken);

        public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(string flagKey, Value defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
            _flags.ResolveStructureValueAsync(flagKey, defaultValue, context, cancellationToken);
    }
}
