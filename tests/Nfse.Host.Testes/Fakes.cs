using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nfse.Aplicacao.Consultas;
using Nfse.Aplicacao.Portas;
using Nfse.Dominio.Documentos;
using Nfse.Dominio.Notas;

namespace Nfse.Host.Testes;

/// <summary>Host com portas falsas em memória e autenticação de teste pelo header <c>X-Teste-Inscricoes</c>.</summary>
public sealed class FabricaDoHost : WebApplicationFactory<Program>
{
    public DadosFalsos Dados { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Oracle", "User Id=x;Password=x;Data Source=nao-usado");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IContribuintes>(Dados);
            services.AddSingleton<ILeituraDeNotas>(Dados);
            services.AddAuthentication(o =>
            {
                o.DefaultAuthenticateScheme = AutenticacaoDeTeste.Esquema;
                o.DefaultChallengeScheme = AutenticacaoDeTeste.Esquema;
            }).AddScheme<AuthenticationSchemeOptions, AutenticacaoDeTeste>(AutenticacaoDeTeste.Esquema, _ => { });
        });
    }
}

public sealed class AutenticacaoDeTeste(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Esquema = "Teste";
    public const string Header = "X-Teste-Inscricoes";
    public const string HeaderDocumento = "X-Teste-Documento";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var inscricoes))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("inscricoes", inscricoes.ToString()) };
        if (Request.Headers.TryGetValue(HeaderDocumento, out var documento))
        {
            claims.Add(new Claim("documento", documento.ToString()));
        }

        var identidade = new ClaimsIdentity(claims, Esquema);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identidade), Esquema)));
    }
}

public sealed class DadosFalsos : IContribuintes, ILeituraDeNotas
{
    public List<PrestadorLido> Prestadores { get; } =
    [
        new(1, "10000001", "11100003000113", "PRESTADOR 1", Ativo: true),
        new(30, "10000030", "14000090000125", "PRESTADOR 30", Ativo: false),
    ];

    public List<NotaFiscalLida> Notas { get; } = [];

    public FiltroDeNotasEmitidas? UltimoFiltro { get; private set; }

    public Task<PrestadorLido?> ObterPorInscricaoAsync(string inscricaoMunicipal, CancellationToken cancellationToken) =>
        Task.FromResult(Prestadores.FirstOrDefault(p => p.InscricaoMunicipal == inscricaoMunicipal));

    public Task<IReadOnlyList<NotaFiscalLida>> ListarEmitidasPorFaixaAsync(FiltroDeNotasEmitidas filtro, int deslocamento, int limite, CancellationToken cancellationToken)
    {
        UltimoFiltro = filtro;
        IReadOnlyList<NotaFiscalLida> resultado = [.. Filtrar(filtro).Skip(Math.Max(0, deslocamento)).Take(Math.Max(0, limite - Math.Max(0, deslocamento)))];
        return Task.FromResult(resultado);
    }

    public Task<IReadOnlyList<NotaFiscalLida>> ListarEmitidasAposAsync(FiltroDeNotasEmitidas filtro, PosicaoNaListagem? apos, int limite, CancellationToken cancellationToken)
    {
        UltimoFiltro = filtro;
        IEnumerable<NotaFiscalLida> notas = Filtrar(filtro);
        if (apos is { } p)
        {
            notas = notas.Where(n => n.DataEmissao > p.DataEmissao || (n.DataEmissao == p.DataEmissao && n.Id > p.Id));
        }

        IReadOnlyList<NotaFiscalLida> resultado = [.. notas.Take(limite)];
        return Task.FromResult(resultado);
    }

    public Task<IReadOnlyList<NotaFiscalLida>> ListarTomadasPorFaixaAsync(FiltroDeNotasTomadas filtro, int deslocamento, int limite, CancellationToken cancellationToken)
    {
        UltimoFiltroTomadas = filtro;
        IReadOnlyList<NotaFiscalLida> resultado = [.. Filtrar(filtro).Skip(Math.Max(0, deslocamento)).Take(Math.Max(0, limite - Math.Max(0, deslocamento)))];
        return Task.FromResult(resultado);
    }

    public Task<IReadOnlyList<NotaFiscalLida>> ListarTomadasAposAsync(FiltroDeNotasTomadas filtro, PosicaoNaListagem? apos, int limite, CancellationToken cancellationToken)
    {
        UltimoFiltroTomadas = filtro;
        IEnumerable<NotaFiscalLida> notas = Filtrar(filtro);
        if (apos is { } p)
        {
            notas = notas.Where(n => n.DataEmissao > p.DataEmissao || (n.DataEmissao == p.DataEmissao && n.Id > p.Id));
        }

        IReadOnlyList<NotaFiscalLida> resultado = [.. notas.Take(limite)];
        return Task.FromResult(resultado);
    }

    public FiltroDeNotasTomadas? UltimoFiltroTomadas { get; private set; }

    private IEnumerable<NotaFiscalLida> Filtrar(FiltroDeNotasTomadas f) => Notas
        .Where(n => n.TomadorDocumento?.Digitos == f.TomadorDigitos && n.DataEmissao >= f.InicioInclusivo && n.DataEmissao < f.FimExclusivo)
        .Where(n => f.Situacao is null || n.Situacao == f.Situacao)
        .OrderBy(n => n.DataEmissao).ThenBy(n => n.Id);

    private IEnumerable<NotaFiscalLida> Filtrar(FiltroDeNotasEmitidas f) => Notas
        .Where(n => n.Prestador.Id == f.PrestadorId && n.DataEmissao >= f.InicioInclusivo && n.DataEmissao < f.FimExclusivo)
        .Where(n => f.TomadorDigitos is null || n.TomadorDocumento?.Digitos == f.TomadorDigitos)
        .Where(n => f.Situacao is null || n.Situacao == f.Situacao)
        .OrderBy(n => n.DataEmissao).ThenBy(n => n.Id);

    public static NotaFiscalLida Nota(long id, long numero, DateTime emissao, string tomador = "01234567000195",
        SituacaoNota situacao = SituacaoNota.Normal) => new(
        id, numero, "V00000001", emissao, new DateOnly(emissao.Year, emissao.Month, 1), "01.01", "Serviço & cia <teste>",
        1000m, 0m, 1000m, 0.05m, 50m, IssRetido: false, situacao,
        situacao == SituacaoNota.Cancelada ? emissao.AddDays(1) : null, null,
        new PrestadorLido(1, "10000001", "11100003000113", "PRESTADOR 1", true),
        Documento.Reconstituir(tomador), "TOMADOR");
}
