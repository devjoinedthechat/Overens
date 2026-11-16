using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using Overens.Gateway;
using Overens.Ingestion.Extraction;
using Overens.Worker;

// The worker serves nobody over HTTP. It listens only so that whatever runs it can ask
// whether it is alive (/alive) and whether it can reach its database (/health).
var builder = WebApplication.CreateSlimBuilder(args);
builder.AddServiceDefaults();
// On a developer's machine it listens on a port of its own. In a container the image says where to listen, and that is left alone.
bool Unset(string name) => string.IsNullOrEmpty(builder.Configuration[name]);
if (Unset("urls") && Unset("HTTP_PORTS") && Unset("ASPNETCORE_HTTP_PORTS"))
{
    builder.WebHost.UseUrls("http://localhost:8089");
}

string Setting(string name) => builder.Configuration[name] is { Length: > 0 } value ? value : throw new InvalidOperationException($"The setting {name} is missing.");
decimal Dollars(string name, string fallback) =>
    decimal.TryParse(builder.Configuration[name] ?? fallback, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var dollars) && dollars >= 0
        ? dollars
        : throw new InvalidOperationException($"The setting {name} must be an amount in dollars.");
var modelId = builder.Configuration["MODEL"] ?? Models.Sonnet;
var recordings = Path.Combine(builder.Configuration["RECORDINGS_DIR"] ?? Path.Combine("evals", "recordings"), ExtractionPrompt.Version, modelId);

builder.Services.AddIngestionWorker(
    Setting("ConnectionStrings:worker"),
    Setting("ConnectionStrings:scheduler"),
    WorkerSetup.Model(builder.Configuration["ANTHROPIC_API_KEY"], recordings, Dollars("MODEL_CAP_DOLLARS", "20")),
    modelId,
    Dollars("TENANT_DAILY_BUDGET_DOLLARS", "20"));
builder.Services.AddReadinessCheck("database", async (services, cancellationToken) =>
{
    await using var ping = services.GetRequiredService<NpgsqlDataSource>().CreateCommand("select 1");
    await ping.ExecuteScalarAsync(cancellationToken);
});

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
