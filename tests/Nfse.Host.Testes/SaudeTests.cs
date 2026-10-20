using System.Diagnostics;
using System.Net;

namespace Nfse.Host.Testes;

/// <summary>Pronto depende do Oracle, vivo não. A <see cref="FabricaDoHost"/> aponta para um banco que não existe.</summary>
public class SaudeTests : IClassFixture<FabricaDoHost>
{
    private readonly FabricaDoHost _fabrica;

    public SaudeTests(FabricaDoHost fabrica) => _fabrica = fabrica;

    [Fact]
    public async Task Ready_sem_oracle_e_503_sem_esperar_o_connection_timeout()
    {
        var cronometro = Stopwatch.StartNew();
        using HttpResponseMessage resposta = await _fabrica.CreateClient().GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
        // O check desiste em 1,5 s (timeoutSeconds 2 do readinessProbe); a folga é para a máquina carregada dos testes.
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(3), $"/health/ready levou {cronometro.Elapsed}");
    }

    [Fact]
    public async Task Live_sem_oracle_continua_200()
    {
        using HttpResponseMessage resposta = await _fabrica.CreateClient().GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }
}
