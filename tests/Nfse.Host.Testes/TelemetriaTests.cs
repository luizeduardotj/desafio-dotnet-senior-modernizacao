using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Nfse.Infra.Oracle;
using Oracle.ManagedDataAccess.OpenTelemetry;
using OpenTelemetry.Trace;

namespace Nfse.Host.Testes;

public class TelemetriaTests : IClassFixture<FabricaDoHost>
{
    private readonly FabricaDoHost _fabrica;

    public TelemetriaTests(FabricaDoHost fabrica) => _fabrica = fabrica;

    [Fact]
    public void Fonte_do_ODP_NET_e_ouvida_pelo_tracing_do_host()
    {
        Assert.NotNull(_fabrica.Services.GetRequiredService<TracerProvider>());

        using var fonte = new ActivitySource(TelemetriaDoOracle.Fonte);
        Assert.True(fonte.HasListeners());
    }

    [Fact]
    public void Span_do_Oracle_nao_leva_texto_do_SQL_nem_atributos_da_conexao()
    {
        var opcoes = new OracleDataProviderInstrumentationOptions
        {
            SetDbStatementForText = true,
            EnableConnectionLevelAttributes = true,
            RecordException = true,
        };

        TelemetriaDoOracle.ConfigurarOpcoes(opcoes);

        Assert.False(opcoes.SetDbStatementForText);
        Assert.False(opcoes.EnableConnectionLevelAttributes);
        Assert.False(opcoes.RecordException);
    }
}
