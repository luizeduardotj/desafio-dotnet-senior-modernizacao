namespace Nfse.Paridade.Testes;

/// <summary>Só roda com <c>NFSE_PARIDADE_ORACLE</c> definido (Oracle real no estado da linha de base, ADR-0011).</summary>
public sealed class ParidadeTheoryAttribute : TheoryAttribute
{
    public const string Variavel = "NFSE_PARIDADE_ORACLE";

    public ParidadeTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variavel)))
        {
            Skip = $"defina {Variavel} com a connection string do NFSE_MODERNO_APP para rodar a paridade";
        }
    }
}
