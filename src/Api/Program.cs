using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.Extensions.Options;
using Npgsql;
using Overens.Agent;
using Overens.Agent.Questions;
using Overens.Api;
using Overens.Data;
using Overens.Gateway;

var builder = WebApplication.CreateBuilder(args);

// Telemetry, health checks and resilient outgoing calls, shared by every service.
builder.AddServiceDefaults();

string Setting(string name) => builder.Configuration[name] is { Length: > 0 } value ? value : throw new InvalidOperationException($"The setting {name} is missing.");

// Settings are read into typed options and checked before the first request is served.
builder.Services.AddOptions<AuthSettings>().BindConfiguration("Auth").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<LimitSettings>().BindConfiguration("Limits").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<ModelSettings>().Bind(builder.Configuration).ValidateDataAnnotations().ValidateOnStart();

// People sign in at the identity provider. The API only checks the token it issued.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IOptions<AuthSettings>, IHostEnvironment>((options, auth, environment) =>
{
    options.Authority = auth.Value.Authority;
    if (auth.Value.MetadataAddress is { Length: > 0 } metadata)
    {
        options.MetadataAddress = metadata;
    }

    options.Audience = auth.Value.Audience;
    options.RequireHttpsMetadata = !environment.IsDevelopment();
    options.MapInboundClaims = false;
    options.TokenValidationParameters.NameClaimType = "preferred_username";
    options.TokenValidationParameters.RoleClaimType = "roles";
    if (auth.Value.IssuerPerTenant is { Length: > 0 })
    {
        // Each company has an issuer of its own. The token's issuer must be its own company's.
        options.TokenValidationParameters.IssuerValidator = (issuer, token, _) => TenantRules.ValidateIssuer(auth.Value, issuer, token);
    }

    options.Events = new()
    {
        OnTokenValidated = accepted =>
        {
            // The company is read under one name from here on, whatever the provider calls the claim.
            var identity = (System.Security.Claims.ClaimsIdentity)accepted.Principal!.Identity!;
            var tenant = identity.FindFirst(auth.Value.TenantClaim)?.Value;
            if (!TenantRules.Allowed(auth.Value, tenant))
            {
                accepted.Fail("The company is not one that may sign in here.");
            }
            else if (auth.Value.TenantClaim != Caller.TenantClaim && tenant is not null)
            {
                foreach (var claimed in identity.FindAll(Caller.TenantClaim).ToList())
                {
                    // A claim already named "tenant" in such a token is not the provider's word on the company.
                    identity.RemoveClaim(claimed);
                }

                identity.AddClaim(new(Caller.TenantClaim, tenant));
            }

            return Task.CompletedTask;
        },
    };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Caller.Specialist, policy => policy.RequireRole(Caller.Specialist))
    .AddPolicy(Caller.ChecksPay, policy => policy.RequireRole(Caller.Specialist, Caller.Employer));

// One company cannot fill the pipeline or the model: what it may ask for in an hour is limited per tenant.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.OnRejected = async (rejected, cancellationToken) =>
    {
        // Say when it is worth asking again.
        if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait))
        {
            rejected.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await Results.Problem("The company has asked for this too often in the last hour.", statusCode: StatusCodes.Status429TooManyRequests).ExecuteAsync(rejected.HttpContext);
    };
    void PerTenant(string name, Func<LimitSettings, int> perHour) => limiter.AddPolicy(name, http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst(Caller.TenantClaim)?.Value ?? "",
        _ => new() { PermitLimit = perHour(http.RequestServices.GetRequiredService<IOptions<LimitSettings>>().Value), Window = TimeSpan.FromHours(1) }));
    PerTenant(Endpoints.UploadLimit, limits => limits.UploadsPerHour);
    PerTenant(QuestionEndpoints.Limit, limits => limits.QuestionsPerHour);
    PerTenant(PayChecks.Limit, limits => limits.PayChecksPerHour);
});

// The data source is the container's, so its connections are closed when the service stops.
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(Setting("ConnectionStrings:api")));
builder.Services.AddSingleton(services => new TenantScope(services.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ModelBudget>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// With a model key, findings are worded and questions answered by a model. Each company has
// a daily budget for that, and the process a cap of its own behind it. Without a key,
// findings get fixed wording and questions are not answered.
builder.Services.AddSingleton<ApiModel>();
builder.Services.AddSingleton(services => new FindingExplainer(services.GetRequiredService<ApiModel>().Client, services.GetRequiredService<IOptions<ModelSettings>>().Value.Model));
builder.Services.AddSingleton(services => new QuestionService(
    services.GetRequiredService<ApiModel>().Client is { } model ? new QuestionAgent(model, services.GetRequiredService<IOptions<ModelSettings>>().Value.Model) : null));

// Without the pipeline nothing can be uploaded or decided, so the API does not start without it.
var scheduler = Setting("ConnectionStrings:scheduler");
builder.Services.AddDurableTaskClient(client => client.UseDurableTaskScheduler(scheduler));
builder.Services.AddSingleton<IPipeline, DurablePipeline>();

// Ready means the database answers. The scheduler is not asked: a run that cannot be
// started now is stored and started by the worker.
builder.Services.AddReadinessCheck("database", async (services, cancellationToken) =>
{
    await using var ping = services.GetRequiredService<NpgsqlDataSource>().CreateCommand("select 1");
    await ping.ExecuteScalarAsync(cancellationToken);
});

var app = builder.Build();

// An error nobody caught and a bare status code both leave as a problem document, never as an empty body.
app.UseExceptionHandler();
app.UseStatusCodePages();

// The console is static files. It may load its own scripts and styles and nothing else,
// and may talk only to this API and the identity provider.
var auth = app.Services.GetRequiredService<IOptions<AuthSettings>>().Value;
var authority = new Uri(auth.Authority);
app.Use(async (http, next) =>
{
    if (http.Request.Path.StartsWithSegments("/app"))
    {
        http.Response.Headers.ContentSecurityPolicy =
            $"default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self' {authority.GetLeftPart(UriPartial.Authority)}; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    await next(http);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/app/config.json", () => Results.Ok(new ConsoleConfig(authority.ToString().TrimEnd('/'), auth.WebClient, auth.Scope, auth.TenantClaim))).ExcludeFromDescription();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapGet("/", () => Results.Ok(new ServiceInfo("overens-api"))).ExcludeFromDescription();
app.MapOverens();
app.MapPayChecks();
app.MapQuestions();

app.Run();

/// <summary>Lets the tests start the API in memory.</summary>
public partial class Program;
