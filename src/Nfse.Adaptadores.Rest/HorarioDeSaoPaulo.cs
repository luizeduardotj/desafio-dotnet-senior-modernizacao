namespace Nfse.Adaptadores.Rest;

/// <summary>Os instantes do legado são horário de São Paulo sem offset; a API os publica com offset (ADR-0010).</summary>
internal static class HorarioDeSaoPaulo
{
    private static readonly TimeZoneInfo Fuso = Resolver();

    public static DateTimeOffset ComOffset(DateTime local) =>
        new(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Fuso.GetUtcOffset(local));

    private static TimeZoneInfo Resolver()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("America/Sao_Paulo", TimeSpan.FromHours(-3), "America/Sao_Paulo", "BRT");
        }
    }
}
