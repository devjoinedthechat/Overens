var builder = WebApplication.CreateBuilder(args);

// Telemetry, health checks and resilient outgoing calls, shared by every service.
builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "overens-api" }));

app.Run();

/// <summary>Lets the tests start the API in memory.</summary>
public partial class Program;
