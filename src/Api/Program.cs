using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Npgsql;
using Overens.Agent;
using Overens.Api;
using Overens.Data;
using Overens.Gateway;

var builder = WebApplication.CreateBuilder(args);

// Telemetry, health checks and resilient outgoing calls, shared by every service.
builder.AddServiceDefaults();

string Setting(string name) => builder.Configuration[name] ?? throw new InvalidOperationException($"The setting {name} is missing.");

// People sign in at the identity provider. The API only checks the token it issued.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = Setting("Auth:Authority");
    options.Audience = Setting("Auth:Audience");
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    options.MapInboundClaims = false;
    options.TokenValidationParameters.NameClaimType = "preferred_username";
    options.TokenValidationParameters.RoleClaimType = "roles";
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Caller.Specialist, policy => policy.RequireRole(Caller.Specialist))
    .AddPolicy(Caller.ChecksPay, policy => policy.RequireRole(Caller.Specialist, Caller.Employer));

// One company cannot fill the pipeline: uploads are limited per tenant.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(Endpoints.UploadLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst("tenant")?.Value ?? "",
        _ => new() { PermitLimit = builder.Configuration.GetValue("Limits:UploadsPerHour", 20), Window = TimeSpan.FromHours(1) }));
});

builder.Services.AddSingleton(_ => new TenantScope(NpgsqlDataSource.Create(Setting("ConnectionStrings:api"))));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();

// Findings are worded by a model when there is a key, under a spending cap. Without one, fixed wording is used.
var explainModel = builder.Configuration["EXPLAIN_MODEL"] ?? Models.Haiku;
builder.Services.AddSingleton(_ => new FindingExplainer(
    builder.Configuration["ANTHROPIC_API_KEY"] is { Length: > 0 } key
        ? new MeteredChatClient(Claude.Create(key), new Spend(), decimal.Parse(builder.Configuration["MODEL_CAP_DOLLARS"] ?? "5", System.Globalization.CultureInfo.InvariantCulture))
        : null,
    explainModel));
if (builder.Configuration["ConnectionStrings:scheduler"] is { } scheduler)
{
    builder.Services.AddDurableTaskClient(client => client.UseDurableTaskScheduler(scheduler));
    builder.Services.AddSingleton<IPipeline, DurablePipeline>();
}

var app = builder.Build();

// The console is static files. It may load its own scripts and styles and nothing else,
// and may talk only to this API and the identity provider.
var authority = new Uri(Setting("Auth:Authority"));
app.Use(async (http, next) =>
{
    if (http.Request.Path.StartsWithSegments("/app"))
    {
        http.Response.Headers.ContentSecurityPolicy =
            $"default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self' {authority.GetLeftPart(UriPartial.Authority)}; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    await next(http);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/app/config.json", () => Results.Ok(new { authority = authority.ToString().TrimEnd('/'), clientId = app.Configuration["Auth:WebClient"] ?? "overens-web" }));

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "overens-api" }));
app.MapOverens();
app.MapPayChecks();

app.Run();

/// <summary>Lets the tests start the API in memory.</summary>
public partial class Program;
