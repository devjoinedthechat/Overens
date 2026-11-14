using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Npgsql;
using Overens.Api;
using Overens.Data;

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
builder.Services.AddAuthorizationBuilder().AddPolicy(Caller.Specialist, policy => policy.RequireRole(Caller.Specialist));

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
if (builder.Configuration["ConnectionStrings:scheduler"] is { } scheduler)
{
    builder.Services.AddDurableTaskClient(client => client.UseDurableTaskScheduler(scheduler));
    builder.Services.AddSingleton<IPipeline, DurablePipeline>();
}

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "overens-api" }));
app.MapOverens();

app.Run();

/// <summary>Lets the tests start the API in memory.</summary>
public partial class Program;
