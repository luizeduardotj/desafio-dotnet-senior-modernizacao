var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok());

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposto para os testes de integração com <c>WebApplicationFactory</c>.</summary>
public partial class Program;
