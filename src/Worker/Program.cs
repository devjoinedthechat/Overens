using Overens.Gateway;
using Overens.Ingestion.Extraction;
using Overens.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

string Setting(string name) => builder.Configuration[name] ?? throw new InvalidOperationException($"The setting {name} is missing.");
var modelId = builder.Configuration["MODEL"] ?? Models.Sonnet;
var recordings = Path.Combine(builder.Configuration["RECORDINGS_DIR"] ?? Path.Combine("evals", "recordings"), ExtractionPrompt.Version, modelId);
var cap = decimal.Parse(builder.Configuration["MODEL_CAP_DOLLARS"] ?? "5", System.Globalization.CultureInfo.InvariantCulture);

builder.Services.AddIngestionWorker(
    Setting("ConnectionStrings:worker"),
    Setting("ConnectionStrings:scheduler"),
    WorkerSetup.Model(builder.Configuration["ANTHROPIC_API_KEY"], recordings, cap),
    modelId,
    decimal.Parse(builder.Configuration["TENANT_DAILY_BUDGET_DOLLARS"] ?? "20", System.Globalization.CultureInfo.InvariantCulture));

builder.Build().Run();
