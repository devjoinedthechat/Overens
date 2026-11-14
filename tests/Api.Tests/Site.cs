using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Overens.Agent.Questions;
using Overens.Data;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Pipeline;
using Overens.Ingestion.Reading;
using Overens.Ingestion.Tests;
using Overens.Synthetic;
using Testcontainers.PostgreSql;

namespace Overens.Api.Tests;

/// <summary>
/// The API as it runs, against a real Postgres, with two stand-ins: an identity provider that
/// is a signing key held by the test, and a pipeline that runs its steps at once, in this
/// process, as the worker's database role.
/// </summary>
public sealed class Site : IAsyncLifetime
{
    public const string Issuer = "https://identity.test/realms/overens";
    public const string Audience = "overens-api";

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.3").Build();
    private readonly RsaSecurityKey key = new(RSA.Create(2048)) { KeyId = "test" };
    private WebApplicationFactory<Program> factory = null!;

    public static SyntheticAgreement Agreement { get; } = Corpus.Generate(Corpus.Agreements[2]);

    public static byte[] Pdf { get; } = AgreementPdf.Render(Agreement.Document);

    /// <summary>The agreement that renews <see cref="Agreement"/> from March 2029.</summary>
    public static SyntheticAgreement Renewal { get; } = Corpus.Generate(Corpus.Agreements[3]);

    public static byte[] RenewalPdf { get; } = AgreementPdf.Render(Renewal.Document);

    /// <summary>The model behind the question agent, for sites created after it is set. Null is no model.</summary>
    public IChatClient? QuestionModel { get; set; }

    /// <summary>Lets a test spoil the model's answers for the uploads that follow.</summary>
    public Action<string, System.Text.Json.Nodes.JsonArray>? Spoil { get; set; }

    private string As(string role, string password) =>
        new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Username = role, Password = password }.ConnectionString;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await Migrator.MigrateAsync(postgres.GetConnectionString());
        await Migrator.SetRolePasswordsAsync(postgres.GetConnectionString(), "api-test", "worker-test");
        factory = Create(uploadsPerHour: 1000);
    }

    public WebApplicationFactory<Program> Create(int uploadsPerHour)
    {
        // Taken now: the host is built later, on first use.
        var questionModel = QuestionModel;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("environment", "Development");
            host.UseSetting("Auth:Authority", Issuer);
            host.UseSetting("Auth:Audience", Audience);
            host.UseSetting("ConnectionStrings:api", As("overens_api", "api-test"));
            host.UseSetting("Limits:UploadsPerHour", uploadsPerHour.ToString(System.Globalization.CultureInfo.InvariantCulture));
            host.ConfigureServices(services =>
            {
                // The provider's keys are given directly, so nothing is fetched over the network.
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var provider = new OpenIdConnectConfiguration { Issuer = Issuer };
                    provider.SigningKeys.Add(key);
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(provider);
                });
                var steps = new IngestionSteps(
                    new TenantScope(NpgsqlDataSource.Create(As("overens_worker", "worker-test"))),
                    new PdfLayoutReader(),
                    new RuleExtractor(new ByTitle(
                        new StandInModel(Agreement.Document, Agreement.Truth, (kind, rules) => Spoil?.Invoke(kind, rules)),
                        new StandInModel(Renewal.Document, Renewal.Truth)), "stand-in"),
                    "stand-in",
                    TimeProvider.System);
                services.RemoveAll<QuestionService>();
                services.AddSingleton(new QuestionService(questionModel is null ? null : new QuestionAgent(questionModel, "stand-in")));
                services.RemoveAll<IPipeline>();
                services.AddSingleton<IPipeline>(new PipelineAtOnce(steps));
            });
        });
    }

    public async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await postgres.DisposeAsync();
    }

    /// <summary>A token as the identity provider would issue it.</summary>
    public string Token(string tenant, string name = "anna", string[]? roles = null, RsaSecurityKey? signedWith = null, TimeSpan? validFor = null, string audience = Audience)
    {
        var claims = new List<Claim> { new("sub", $"id-{name}"), new("preferred_username", name) };
        if (tenant.Length > 0)
        {
            claims.Add(new("tenant", tenant));
        }

        claims.AddRange((roles ?? [Caller.Specialist]).Select(role => new Claim("roles", role)));
        var lifetime = validFor ?? TimeSpan.FromMinutes(5);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Subject = new(claims),
            NotBefore = DateTime.UtcNow.Add(lifetime < TimeSpan.Zero ? lifetime * 2 : TimeSpan.FromMinutes(-1)),
            Expires = DateTime.UtcNow.Add(lifetime),
            SigningCredentials = new(signedWith ?? key, SecurityAlgorithms.RsaSha256),
        });
    }

    public HttpClient Client(string? token, WebApplicationFactory<Program>? site = null)
    {
        var client = (site ?? factory).CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public HttpClient As(string tenant) => Client(Token(tenant));

    /// <summary>Answers for whichever of the two agreements the request is about.</summary>
    private sealed class ByTitle(StandInModel first, StandInModel renewal) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var asked = messages.ToList();
            var aboutRenewal = asked[^1].Text.Contains("1. marts 2029 og kan opsiges", StringComparison.Ordinal);
            return (aboutRenewal ? renewal : first).GetResponseAsync(asked, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Runs each step when it is asked for, so a test sees the result at once.</summary>
    private sealed class PipelineAtOnce(IngestionSteps steps) : IPipeline
    {
        public async Task StartAsync(RunRef run, CancellationToken cancellationToken)
        {
            try
            {
                await steps.ReadAsync(run, cancellationToken);
                await steps.ExtractAsync(run, cancellationToken);
            }
            catch (UnreadableDocumentException unreadable)
            {
                await steps.EndAsync(run, "failed", unreadable.Message, cancellationToken);
            }
        }

        public async Task DecideAsync(RunRef run, bool publish, CancellationToken cancellationToken)
        {
            if (publish)
            {
                await steps.PublishAsync(run, cancellationToken);
            }
            else
            {
                await steps.EndAsync(run, "rejected", null, cancellationToken);
            }
        }
    }
}
