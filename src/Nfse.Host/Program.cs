var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok());

await app.RunAsync().ConfigureAwait(false);
