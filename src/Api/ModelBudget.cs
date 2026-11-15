using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Overens.Data;
using Overens.Gateway;

namespace Overens.Api;

/// <summary>
/// What a company's questions and explanations may cost in a day, kept in the database: the
/// count is the same on every copy of the API and is still there after a restart.
/// </summary>
public sealed class ModelBudget(TenantScope database, TimeProvider clock, IOptions<ModelSettings> settings)
{
    public const string UsedUp = "The company's daily budget for model answers is used up. Try again tomorrow.";

    /// <summary>Whether the company has anything left to spend today.</summary>
    public async Task<bool> HasRoomAsync(string tenantId, CancellationToken cancellationToken)
    {
        var today = Today();
        var spent = await database.RunAsync(tenantId, db =>
            db.Database.SqlQuery<long>($"select coalesce((select cost_micros from model_spend where day = {today}), 0)::bigint as \"Value\"").SingleAsync(cancellationToken), cancellationToken);
        return spent < settings.Value.TenantDailyDollars * 1_000_000m;
    }

    /// <summary>Adds what a piece of work cost to the company's day. Work that called no model costs nothing and writes nothing.</summary>
    public async Task ChargeAsync(string tenantId, Usage usage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.Calls == 0)
        {
            return;
        }

        var (today, calls, micros) = (Today(), usage.Calls, usage.CostMicros);
        await database.RunAsync(tenantId, db =>
            db.Database.ExecuteSqlAsync($"select note_model_spend({today}, {calls}, {micros})", cancellationToken), cancellationToken);
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
}

/// <summary>The model the API asks, or none. Made once, and disposed with the service.</summary>
public sealed class ApiModel(IOptions<ModelSettings> settings) : IDisposable
{
    public Microsoft.Extensions.AI.IChatClient? Client { get; } = settings.Value.ApiKey is { Length: > 0 } key
        ? new MeteredChatClient(Claude.Create(key, Claude.Interactive), new Spend(), settings.Value.CapDollars)
        : null;

    public void Dispose() => Client?.Dispose();
}
