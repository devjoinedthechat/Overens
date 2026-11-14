using System.Security.Claims;

namespace Overens.Api;

/// <summary>Who is calling, taken from their token: the company they belong to and their own name.</summary>
public sealed record Caller(string TenantId, string Name)
{
    public const string Specialist = "specialist";

    /// <summary>Null when the token does not say which company the person belongs to.</summary>
    public static Caller? From(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenant = user.FindFirstValue("tenant");
        var name = user.FindFirstValue("preferred_username") ?? user.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(name) ? null : new(tenant, name);
    }
}
