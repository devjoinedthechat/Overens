using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

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

    /// <summary>The console's client id at the identity provider.</summary>
    [Required]
    public string WebClient { get; set; } = "overens-web";
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
