using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nfse.Fachada.Flags;

namespace Nfse.Fachada.Testes;

public sealed class FonteDeFlagsPorConfiguracaoTests : IDisposable
{
    private readonly string _pasta = Directory.CreateTempSubdirectory("fachada-flags-").FullName;
    private readonly List<ServiceProvider> _provedores = [];

    private string Arquivo => Path.Combine(_pasta, "flags.json");

    [Fact]
    public void Le_o_arquivo_de_flags()
    {
        File.WriteAllText(Arquivo, Json("novo", 25));
        FlagsDeMigracao? flags = Criar().Atual();

        Assert.NotNull(flags);
        Assert.Equal(ModoDeMigracao.Novo, flags.Da("ConsultarNfse").Modo);
        Assert.Equal(25, flags.Da("ConsultarNfse").Percentual);
        Assert.Equal(["10000001"], flags.Da("ConsultarNfse").Inclusao);
        Assert.Equal(ModoDeMigracao.Legado, flags.Da("CancelarNfse").Modo);
    }

    [Fact]
    public async Task Recarrega_quando_o_arquivo_muda()
    {
        File.WriteAllText(Arquivo, Json("legado", 0));
        var fonte = Criar();
        Assert.Equal(ModoDeMigracao.Legado, fonte.Atual()!.Da("ConsultarNfse").Modo);

        File.WriteAllText(Arquivo, Json("novo", 10));

        Assert.True(await Aguardar(() => fonte.Atual()?.Da("ConsultarNfse").Modo == ModoDeMigracao.Novo),
            "as flags não recarregaram em 15 s");
    }

    [Fact]
    public async Task Arquivo_que_fica_invalido_vira_fail_safe_e_volta_quando_corrigido()
    {
        File.WriteAllText(Arquivo, Json("novo", 10));
        var fonte = Criar();
        Assert.NotNull(fonte.Atual());

        File.WriteAllText(Arquivo, "{ \"Migracao\": { ");
        Assert.True(await Aguardar(() => fonte.Atual() is null), "JSON inválido não levou ao fail-safe");

        File.WriteAllText(Arquivo, Json("sombra", 0));
        Assert.True(await Aguardar(() => fonte.Atual()?.Da("ConsultarNfse").Modo == ModoDeMigracao.Sombra),
            "as flags não voltaram depois da correção");
    }

    [Theory]
    [InlineData("""{ "Outra": {} }""")]
    [InlineData("""{ "Migracao": { "Operacoes": { "ConsultarNfse": { "Modo": "talvez" } } } }""")]
    [InlineData("""{ "Migracao": { "Operacoes": { "ConsultarNfse": { "Modo": "novo", "Percentual": "muito" } } } }""")]
    [InlineData("""{ "Migracao": { "Operacoes": { "ConsultarNfse": { "Modo": "novo", "Percentual": 101 } } } }""")]
    [InlineData("""{ "Migracao": { "Operacoes": { "ConsultarNfse": { "Modo": "sombra", "Sombra": { "Amostragem": -1 } } } } }""")]
    [InlineData("""{ "Migracao": { "KillSwitch": "sim" } }""")]
    [InlineData("isto não é json")]
    public void Flags_invalidas_ou_ausentes_viram_null(string conteudo)
    {
        File.WriteAllText(Arquivo, conteudo);
        Assert.Null(Criar().Atual());
    }

    [Fact]
    public void Arquivo_inexistente_vira_null() => Assert.Null(Criar().Atual());

    public void Dispose()
    {
        foreach (ServiceProvider provedor in _provedores)
        {
            provedor.Dispose();
        }

        Directory.Delete(_pasta, recursive: true);
    }

    private FonteDeFlagsPorConfiguracao Criar()
    {
        IConfigurationRoot configuracao = new ConfigurationBuilder()
            .AddJsonFile(fonte =>
            {
                fonte.Path = Arquivo;
                fonte.Optional = true;
                fonte.ReloadOnChange = true;
                fonte.ReloadDelay = 50;
                fonte.OnLoadException = contexto => contexto.Ignore = true;
                fonte.ResolveFileProvider();
            })
            .Build();

        ServiceProvider provedor = new ServiceCollection()
            .Configure<FlagsDeMigracao>(configuracao.GetSection(FlagsDeMigracao.Secao))
            .BuildServiceProvider();
        _provedores.Add(provedor);

        return new FonteDeFlagsPorConfiguracao(
            provedor.GetRequiredService<IOptionsMonitor<FlagsDeMigracao>>(),
            configuracao,
            NullLogger<FonteDeFlagsPorConfiguracao>.Instance);
    }

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

    private static string Json(string modo, int percentual) => $$"""
        {
          "Migracao": {
            "KillSwitch": false,
            "Operacoes": {
              "ConsultarNfse": { "Modo": "{{modo}}", "Percentual": {{percentual}}, "Inclusao": ["10000001"] },
              "CancelarNfse": { "Modo": "legado" }
            }
          }
        }
        """;
}
