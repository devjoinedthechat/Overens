using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Overens.Api;

// The API's settings, checked when it starts. A missing or impossible value stops the
// service at once, with the setting's name, and not at the first request that needs it.

/// <summary>Who issues the tokens the API accepts.</summary>
public sealed class AuthSettings
{
    [Required, Url]
    public string Authority { get; set; } = "";

    [Required]
    public string Audience { get; set; } = "";

    /// <summary>
    /// Where the API reads the identity provider's keys, when that is not where people's
    /// browsers reach it: inside a network the provider has another address than outside it.
    /// Unset, the keys are read from the authority.
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>The console's client id at the identity provider.</summary>
    [Required]
    public string WebClient { get; set; } = "overens-web";

    /// <summary>What the console asks for when it signs a person in. With Microsoft Entra ID this names the API, for example "api://overens-api/access".</summary>
    [Required]
    public string Scope { get; set; } = "openid";

    /// <summary>
    /// The claim that says which company a person belongs to. Keycloak is set up to issue
    /// "tenant". Microsoft Entra ID issues "tid", the id of the organisation's own directory.
    /// </summary>
    [Required]
    public string TenantClaim { get; set; } = Caller.TenantClaim;

    /// <summary>
    /// Set when every company signs in through a directory of its own, each with its own
    /// issuer, as with Microsoft Entra ID: "https://login.microsoftonline.com/{tenant}/v2.0".
    /// A token is then accepted only if its issuer is this text with the token's own tenant
    /// in it, so a token from one company's directory cannot claim to be another company's.
    /// </summary>
    public string? IssuerPerTenant { get; set; }

    /// <summary>When any are listed, only these companies may sign in. Empty lets in every company the identity provider does.</summary>
    public IList<string> AllowedTenants { get; } = [];
}

/// <summary>How much one company may ask for in an hour.</summary>
public sealed class LimitSettings
{
    [Range(1, 100_000)]
    public int UploadsPerHour { get; set; } = 20;

    [Range(1, 100_000)]
    public int QuestionsPerHour { get; set; } = 120;

    [Range(1, 100_000)]
    public int PayChecksPerHour { get; set; } = 600;
}

/// <summary>The model that words findings and answers questions, and what it may cost.</summary>
public sealed class ModelSettings
{
    /// <summary>Empty when no model is configured: findings get fixed wording and questions are not answered.</summary>
    [ConfigurationKeyName("ANTHROPIC_API_KEY")]
    public string? ApiKey { get; set; }

    [ConfigurationKeyName("API_MODEL"), Required]
    public string Model { get; set; } = Gateway.Models.Haiku;

    /// <summary>What this process may spend in all, whoever asks. A last line behind the companies' own budgets.</summary>
    [ConfigurationKeyName("MODEL_CAP_DOLLARS"), Range(0, 10_000)]
    public decimal CapDollars { get; set; } = 5m;

    /// <summary>What one company's questions and explanations may cost in a day.</summary>
    [ConfigurationKeyName("TENANT_DAILY_ANSWER_DOLLARS"), Range(0, 10_000)]
    public decimal TenantDailyDollars { get; set; } = 1m;
}

/// <summary>What a token must say about its company before the API takes it.</summary>
public static class TenantRules
{
    /// <summary>
    /// The issuer a token must have. With one issuer per company it is the template with
    /// the token's own tenant in it; a token that names no tenant has no valid issuer.
    /// </summary>
    public static string ValidateIssuer(AuthSettings settings, string issuer, SecurityToken token)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var tenant = token is Microsoft.IdentityModel.JsonWebTokens.JsonWebToken jwt && jwt.TryGetPayloadValue<string>(settings.TenantClaim, out var named) ? named : null;
        if (string.IsNullOrWhiteSpace(tenant) || settings.IssuerPerTenant!.Replace("{tenant}", tenant, StringComparison.Ordinal) != issuer)
        {
            throw new SecurityTokenInvalidIssuerException("The token's issuer is not its own company's directory.") { InvalidIssuer = issuer };
        }

        return issuer;
    }

    /// <summary>Whether the company may sign in at all.</summary>
    public static bool Allowed(AuthSettings settings, string? tenant)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.AllowedTenants.Count == 0 || (tenant is not null && settings.AllowedTenants.Contains(tenant));
    }
}
