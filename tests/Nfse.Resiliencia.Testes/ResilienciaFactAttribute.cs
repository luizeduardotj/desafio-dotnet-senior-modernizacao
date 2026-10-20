namespace Nfse.Resiliencia.Testes;

/// <summary>
/// Só roda com <c>NFSE_RESILIENCIA=1</c> e o compose de resiliência de pé (<c>tests/resiliencia/rodar.sh</c>). Os testes
/// mexem no Toxiproxy e nas flags da fachada: não rodam em paralelo nem contra outro ambiente.
/// </summary>
public sealed class ResilienciaFactAttribute : FactAttribute
{
    public const string Variavel = "NFSE_RESILIENCIA";

    public ResilienciaFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variavel) != "1")
        {
            Skip = $"defina {Variavel}=1 com o compose de resiliência de pé (tests/resiliencia/README.md)";
        }
    }
}
