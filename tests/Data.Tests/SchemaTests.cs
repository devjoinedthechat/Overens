using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Overens.Data.Tests;

public sealed class SchemaTests(Database database) : IClassFixture<Database>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static int counter;

    /// <summary>A document, a run awaiting review and two proposed rules, for a tenant of its own.</summary>
    private async Task<(string Tenant, string Run)> Seed(string problems = "[]")
    {
        var number = Interlocked.Increment(ref counter);
        var (tenant, run) = ($"tenant-{number}", $"run-{number}");
        var sha = new string('a', 64);
        await database.AsApi.RunAsync(tenant, db =>
        {
            db.Documents.Add(new() { TenantId = tenant, Sha256 = sha, FileName = "a.pdf", Content = [1, 2, 3], UploadedBy = "anna", UploadedAt = Now });
            db.Runs.Add(new() { Id = run, TenantId = tenant, AgreementId = "lager", DocumentSha256 = sha, Status = "started", CreatedAt = Now });
            return Task.CompletedTask;
        });
        await database.AsWorker.RunAsync(tenant, async db =>
        {
            db.ProposedRules.Add(new() { RunId = run, RuleId = "holiday_allowance-2026", TenantId = tenant, Rule = """{"percent": 12.5}""", AlsoFrom = "[]", Problems = "[]" });
            db.ProposedRules.Add(new() { RunId = run, RuleId = "pension-2026", TenantId = tenant, Rule = """{"employerPercent": 8}""", AlsoFrom = "[]", Problems = problems });
            (await db.Runs.SingleAsync()).Status = "awaiting_review";
        });
        return (tenant, run);
    }

    private Task Decide(string tenant, string decision, string? only = null) =>
        database.AsApi.RunAsync(tenant, async db =>
        {
            foreach (var rule in await db.ProposedRules.Where(rule => only == null || rule.RuleId == only).ToListAsync())
            {
                (rule.Decision, rule.DecidedBy, rule.DecidedAt) = (decision, "anna", Now);
            }
        });

    private Task Approve(string tenant, string run) =>
        database.AsApi.RunAsync(tenant, db =>
        {
            db.Approvals.Add(new() { RunId = run, TenantId = tenant, ApprovedBy = "anna", ApprovedAt = Now });
            return Task.CompletedTask;
        });

    private static Task<int> Publish(TenantScope scope, string tenant, string run) =>
        scope.RunAsync(tenant, db => db.Database.SqlQuery<int>($"select publish_rule_set({run}, {Now}) as \"Value\"").SingleAsync());

    private static async Task<PostgresException> Refused(Func<Task> attempt)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(attempt);
        return Assert.IsType<PostgresException>(error as PostgresException ?? error.InnerException);
    }

    [Fact]
    public async Task Approved_rules_are_published_as_the_next_version_once_and_only_once()
    {
        var (tenant, run) = await Seed();
        await Decide(tenant, "approved", "holiday_allowance-2026");
        await Decide(tenant, "rejected", "pension-2026");
        await Approve(tenant, run);

        Assert.Equal(1, await Publish(database.AsWorker, tenant, run));
        Assert.Equal(1, await Publish(database.AsWorker, tenant, run));

        var set = await database.AsApi.RunAsync(tenant, db => db.RuleSets.SingleAsync());
        Assert.Equal(("lager", 1, "anna"), (set.AgreementId, set.Version, set.ApprovedBy));
        Assert.Contains("12.5", set.Rules, StringComparison.Ordinal);
        Assert.DoesNotContain("employerPercent", set.Rules, StringComparison.Ordinal);
        Assert.Equal("published", await database.AsApi.RunAsync(tenant, db => db.Runs.Select(row => row.Status).SingleAsync()));
    }

    [Fact]
    public async Task Nothing_is_published_without_a_persons_approval()
    {
        var (tenant, run) = await Seed();
        await Decide(tenant, "approved");

        var error = await Refused(() => Publish(database.AsWorker, tenant, run));
        Assert.Contains("has not been approved by a person", error.MessageText, StringComparison.Ordinal);

        // The pipeline cannot approve its own work, and cannot write a rule set directly.
        Assert.Equal("42501", (await Refused(() => database.AsWorker.RunAsync(tenant, db =>
        {
            db.Approvals.Add(new() { RunId = run, TenantId = tenant, ApprovedBy = "pipeline", ApprovedAt = Now });
            return Task.CompletedTask;
        }))).SqlState);
        Assert.Equal("42501", (await Refused(() => database.AsWorker.RunAsync(tenant, db =>
        {
            db.RuleSets.Add(new() { TenantId = tenant, AgreementId = "lager", Version = 1, RunId = run, Rules = "[]", ApprovedBy = "pipeline", PublishedAt = Now });
            return Task.CompletedTask;
        }))).SqlState);
        // Nor can it decide on a rule.
        Assert.Equal("42501", (await Refused(() => database.AsWorker.RunAsync(tenant, async db =>
            (await db.ProposedRules.FirstAsync()).Decision = "rejected"))).SqlState);
        // The API cannot publish, with or without an approval.
        await Approve(tenant, run);
        Assert.Equal("42501", (await Refused(() => Publish(database.AsApi, tenant, run))).SqlState);
    }

    [Fact]
    public async Task Nothing_is_published_while_a_rule_is_undecided_or_when_none_is_approved()
    {
        var (tenant, run) = await Seed();
        await Decide(tenant, "approved", "holiday_allowance-2026");
        await Approve(tenant, run);
        Assert.Contains("nobody has decided on", (await Refused(() => Publish(database.AsWorker, tenant, run))).MessageText, StringComparison.Ordinal);

        var (other, otherRun) = await Seed();
        await Decide(other, "rejected");
        await Approve(other, otherRun);
        Assert.Contains("no approved rules", (await Refused(() => Publish(database.AsWorker, other, otherRun))).MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rule_the_checks_found_a_problem_with_cannot_be_approved()
    {
        var (tenant, _) = await Seed(problems: """[{"code": "quote_not_in_clause"}]""");

        var error = await Refused(() => Decide(tenant, "approved", "pension-2026"));

        Assert.Equal("23514", error.SqlState);
        await Decide(tenant, "rejected", "pension-2026");
    }

    [Fact]
    public async Task A_decision_cannot_change_once_the_run_is_approved()
    {
        var (tenant, run) = await Seed();
        await Decide(tenant, "approved");
        await Approve(tenant, run);

        // Between the approval and the publication, nobody can swap what was approved.
        var error = await Refused(() => Decide(tenant, "rejected", "pension-2026"));

        Assert.Contains("can no longer change", error.MessageText, StringComparison.Ordinal);
        Assert.Equal(1, await Publish(database.AsWorker, tenant, run));
    }

    [Fact]
    public async Task A_published_rule_set_a_document_and_an_approval_cannot_be_changed_or_removed()
    {
        var (tenant, run) = await Seed();
        await Decide(tenant, "approved");
        await Approve(tenant, run);
        await Publish(database.AsWorker, tenant, run);

        // Not by the run-time roles, which lack the right...
        Assert.Equal("42501", (await Refused(() => database.AsApi.RunAsync(tenant, db => db.Database.ExecuteSqlAsync($"update rule_sets set rules = '[]'")))).SqlState);
        Assert.Equal("42501", (await Refused(() => database.AsWorker.RunAsync(tenant, db => db.Database.ExecuteSqlAsync($"delete from rule_sets")))).SqlState);

        // ...and not by the owner either, whom the triggers stop.
        await using var owner = NpgsqlDataSource.Create(database.Owner);
        foreach (var sql in new[]
        {
            "update rule_sets set rules = '[]'", "delete from rule_sets", "truncate rule_sets cascade",
            "update documents set content = ''", "delete from approvals",
        })
        {
            await using var command = owner.CreateCommand(sql);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("never changed or removed", error.MessageText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task One_tenant_cannot_see_or_touch_anothers_rows()
    {
        var (tenant, run) = await Seed();
        var (other, _) = await Seed();

        await database.AsApi.RunAsync(other, async db =>
        {
            Assert.DoesNotContain(await db.Runs.ToListAsync(), row => row.TenantId == tenant);
            Assert.Null(await db.Runs.FindAsync(run));
            Assert.Equal(1, await db.Documents.CountAsync());
            Assert.Equal(2, await db.ProposedRules.CountAsync());
            Assert.Equal(0, await db.Database.ExecuteSqlAsync($"update proposed_rules set decision = 'rejected', decided_by = 'x', decided_at = now() where run_id = {run}"));
        });

        // A row cannot be written into another tenant either.
        Assert.Equal("42501", (await Refused(() => database.AsApi.RunAsync(other, db =>
        {
            db.Approvals.Add(new() { RunId = run, TenantId = tenant, ApprovedBy = "mallory", ApprovedAt = Now });
            return Task.CompletedTask;
        }))).SqlState);
        Assert.Contains("no such run", (await Refused(() => Publish(database.AsWorker, other, run))).MessageText, StringComparison.Ordinal);
    }
}
