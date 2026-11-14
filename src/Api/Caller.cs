using System.Security.Claims;

namespace Overens.Api;

/// <summary>Who is calling, taken from their token: the company they belong to and their own name.</summary>
public sealed record Caller(string TenantId, string Name)
{
    /// <summary>Uploads agreements, reviews proposed rules and approves rule sets.</summary>
    public const string Specialist = "specialist";

    /// <summary>Checks pay against published rule sets. Cannot upload, review or approve.</summary>
    public const string Employer = "employer";

    /// <summary>The policy for checking pay: a specialist or an employer.</summary>
    public const string ChecksPay = "checks-pay";

    /// <summary>Null when the token does not say which company the person belongs to.</summary>
    public static Caller? From(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenant = user.FindFirstValue("tenant");
        var name = user.FindFirstValue("preferred_username") ?? user.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(name) ? null : new(tenant, name);
    }
}
