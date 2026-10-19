using System.Reflection;
using NetArchTest.Rules;
using Nfse.Aplicacao;
using Nfse.Dominio;

namespace Nfse.Arquitetura.Testes;

// ADR-0006 e ADR-0007: o domínio e a aplicação não conhecem o legado, o transporte nem o banco.
public class DependenciasTests
{
    private static readonly string[] Proibidas =
    [
        "System.Xml",
        "Oracle",
        "Microsoft.AspNetCore",
        "Nfse.Infra",
        "Nfse.Adaptadores",
    ];

    public static TheoryData<string> Nucleo => new() { nameof(Dominio), nameof(Aplicacao) };

    [Theory]
    [MemberData(nameof(Nucleo))]
    public void Nucleo_nao_depende_de_infraestrutura(string camada)
    {
        Assembly assembly = camada == nameof(Dominio)
            ? typeof(DominioAssembly).Assembly
            : typeof(AplicacaoAssembly).Assembly;

        var resultado = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(Proibidas)
            .GetResult();

        Assert.True(resultado.IsSuccessful, string.Join(", ", resultado.FailingTypeNames ?? []));
    }

    [Fact]
    public void Dominio_nao_depende_da_aplicacao()
    {
        var resultado = Types.InAssembly(typeof(DominioAssembly).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Nfse.Aplicacao")
            .GetResult();

        Assert.True(resultado.IsSuccessful, string.Join(", ", resultado.FailingTypeNames ?? []));
    }
}
