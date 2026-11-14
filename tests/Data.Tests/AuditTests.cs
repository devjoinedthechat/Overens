using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Overens.Data.Tests;

public sealed class AuditTests(Database database) : IClassFixture<Database>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static int counter;

    /// <summary>A company takes one agreement from upload to a published rule set.</summary>
    private async Task<(string Tenant, string Run)> Lifecycle()
    {
        var number = Interlocked.Increment(ref counter);
        var (tenant, run) = ($"audit-{number}", $"audit-run-{number}");
        var sha = new string('b', 64);
        await database.AsApi.RunAsync(tenant, "anna", db =>
        {
            db.Documents.Add(new() { TenantId = tenant, Sha256 = sha, FileName = "a.pdf", Content = [1, 2, 3, 4], UploadedBy = "anna", UploadedAt = Now });
            db.Runs.Add(new() { Id = run, TenantId = tenant, AgreementId = "lager", DocumentSha256 = sha, Status = "started", CreatedAt = Now });
            return Task.CompletedTask;
        });
        await database.AsWorker.RunAsync(tenant, async db =>
        {
            db.ProposedRules.Add(new() { RunId = run, RuleId = "holiday_allowance-2026", TenantId = tenant, Rule = """{"percent": 12.5}""", AlsoFrom = "[]", Problems = "[]" });
            var row = await db.Runs.SingleAsync();
            (row.Status, row.Model, row.PromptVersion) = ("awaiting_review", "stand-in", "v1");
        });
        await database.AsApi.RunAsync(tenant, "berit", async db =>
        {
            var rule = await db.ProposedRules.SingleAsync();
            (rule.Decision, rule.DecidedBy, rule.DecidedAt) = ("approved", "berit", Now);
        });
        await database.AsApi.RunAsync(tenant, "berit", db =>
        {
            db.Approvals.Add(new() { RunId = run, TenantId = tenant, ApprovedBy = "berit", ApprovedAt = Now });
            return Task.CompletedTask;
        });
        await database.AsWorker.RunAsync(tenant, db => db.Database.SqlQuery<int>($"select publish_rule_set({run}, {Now}) as \"Value\"").SingleAsync());
        return (tenant, run);
    }

    private Task<List<AuditRow>> Log(string tenant) => database.AsApi.RunAsync(tenant, db => db.Audit.OrderBy(entry => entry.Seq).ToListAsync());

    [Fact]
    public async Task Every_step_of_an_agreements_way_is_logged_by_the_database_with_who_did_it()
    {
        var (tenant, run) = await Lifecycle();

        var log = await Log(tenant);

        Assert.Equal(
            [
                ("document_stored", "anna"), ("run_started", "anna"), ("run_awaiting_review", "pipeline"), ("rule_approved", "berit"),
                ("run_approved", "berit"), ("rule_set_published", "berit"), ("run_published", "pipeline"),
            ],
            log.Select(entry => (entry.Event, entry.Actor)));
        Assert.All(log.Skip(1), entry => Assert.Equal(run, entry.Subject));
        Assert.Contains("\"version\": 1", log[5].Detail, StringComparison.Ordinal);
        Assert.Contains("\"model\": \"stand-in\"", log[2].Detail, StringComparison.Ordinal);
        Assert.Null(await Migrator.VerifyAuditAsync(database.Owner));
    }

    [Fact]
    public async Task The_log_cannot_be_written_edited_or_emptied_by_a_service_and_an_edit_by_the_owner_is_found()
    {
        var (tenant, _) = await Lifecycle();
        static async Task<string> Refused(Func<Task> attempt)
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(attempt);
            var postgres = Assert.IsType<PostgresException>(error as PostgresException ?? error.InnerException);
            return $"{postgres.SqlState} {postgres.MessageText}";
        }

        // Neither role can add an entry of its own choosing, change one or remove one.
        foreach (var scope in new[] { database.AsApi, database.AsWorker })
        {
            Assert.StartsWith("42501", await Refused(() => scope.RunAsync(tenant, db => db.Database.ExecuteSqlAsync($"select audit_append({tenant}, 'mallory', 'run_approved', 'x', '{{}}'::jsonb)"))), StringComparison.Ordinal);
            Assert.StartsWith("42501", await Refused(() => scope.RunAsync(tenant, db => db.Database.ExecuteSqlAsync($"update audit_log set actor = 'nobody'"))), StringComparison.Ordinal);
            Assert.StartsWith("42501", await Refused(() => scope.RunAsync(tenant, db => db.Database.ExecuteSqlAsync($"delete from audit_log"))), StringComparison.Ordinal);
        }

        // The API may note a pay check or a question for its own tenant and actor, and nothing else.
        await database.AsApi.RunAsync(tenant, "eva", db => db.Database.ExecuteSqlAsync($"select audit_note('pay_checked', 'lager', '{{\"findings\": 2}}'::jsonb)"));
        Assert.Contains("cannot be noted", await Refused(() => database.AsApi.RunAsync(tenant, "eva", db => db.Database.ExecuteSqlAsync($"select audit_note('run_approved', 'x', '{{}}'::jsonb)"))), StringComparison.Ordinal);
        Assert.Contains("needs a tenant and an actor", await Refused(() => database.AsApi.RunAsync(tenant, db => db.Database.ExecuteSqlAsync($"select audit_note('pay_checked', 'lager', '{{}}'::jsonb)"))), StringComparison.Ordinal);
        Assert.StartsWith("42501", await Refused(() => database.AsWorker.RunAsync(tenant, "x", db => db.Database.ExecuteSqlAsync($"select audit_note('pay_checked', 'lager', '{{}}'::jsonb)"))), StringComparison.Ordinal);
        Assert.Equal(("pay_checked", "eva"), ((await Log(tenant))[^1].Event, (await Log(tenant))[^1].Actor));

        // The owner is stopped by the trigger. With the trigger out of the way, the edit shows in the chain.
        await using var owner = NpgsqlDataSource.Create(database.Owner);
        async Task Sql(string sql)
        {
            await using var command = owner.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Contains("never changed or removed", (await Assert.ThrowsAsync<PostgresException>(() => Sql("update audit_log set actor = 'nobody'"))).MessageText, StringComparison.Ordinal);
        Assert.Null(await Migrator.VerifyAuditAsync(database.Owner));
        var edited = (await Log(tenant))[3].Seq;
        await Sql("alter table audit_log disable trigger audit_log_is_immutable");
        await Sql($"update audit_log set actor = 'someone else' where seq = {edited}");
        Assert.Equal(edited, await Migrator.VerifyAuditAsync(database.Owner));
        await Sql($"update audit_log set actor = 'berit' where seq = {edited}");
        Assert.Null(await Migrator.VerifyAuditAsync(database.Owner));
        await Sql($"delete from audit_log where seq = {edited}");
        Assert.Equal(edited + 1, await Migrator.VerifyAuditAsync(database.Owner));
    }

    [Fact]
    public async Task One_company_reads_only_its_own_entries()
    {
        var (tenant, _) = await Lifecycle();
        var (other, _) = await Lifecycle();

        Assert.All(await Log(other), entry => Assert.Equal(other, entry.TenantId));
        Assert.DoesNotContain(await Log(other), entry => entry.TenantId == tenant);
    }
}

public sealed class ErasureTests(Database database) : IClassFixture<Database>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private async Task Seed(string tenant)
    {
        var sha = new string('c', 64);
        await database.AsApi.RunAsync(tenant, "anna", db =>
        {
            db.Documents.Add(new() { TenantId = tenant, Sha256 = sha, FileName = "a.pdf", Content = [1], UploadedBy = "anna", UploadedAt = Now });
            db.Runs.Add(new() { Id = $"run-{tenant}", TenantId = tenant, AgreementId = "lager", DocumentSha256 = sha, Status = "started", CreatedAt = Now });
            return Task.CompletedTask;
        });
        await database.AsWorker.RunAsync(tenant, async db =>
        {
            db.ProposedRules.Add(new() { RunId = $"run-{tenant}", RuleId = "r", TenantId = tenant, Rule = "{}", AlsoFrom = "[]", Problems = "[]" });
            (await db.Runs.SingleAsync()).Status = "awaiting_review";
        });
        await database.AsApi.RunAsync(tenant, "anna", async db =>
        {
            var rule = await db.ProposedRules.SingleAsync();
            (rule.Decision, rule.DecidedBy, rule.DecidedAt) = ("approved", "anna", Now);
        });
        await database.AsApi.RunAsync(tenant, "anna", db =>
        {
            db.Approvals.Add(new() { RunId = $"run-{tenant}", TenantId = tenant, ApprovedBy = "anna", ApprovedAt = Now });
            return Task.CompletedTask;
        });
        await database.AsWorker.RunAsync(tenant, db => db.Database.SqlQuery<int>($"select publish_rule_set({"run-" + tenant}, {Now}) as \"Value\"").SingleAsync());
    }

    private Task<int[]> Counts(string tenant) => database.AsApi.RunAsync(tenant, async db => new[]
    {
        await db.Documents.CountAsync(), await db.Runs.CountAsync(), await db.ProposedRules.CountAsync(), await db.Approvals.CountAsync(), await db.RuleSets.CountAsync(),
    });

    [Fact]
    public async Task Erasing_a_company_removes_everything_it_stored_and_nothing_of_anyone_elses()
    {
        await Seed("erase-me");
        await Seed("keep-me");

        var removed = await Migrator.EraseTenantAsync(database.Owner, "erase-me");

        var (erased, kept) = (await Counts("erase-me"), await Counts("keep-me"));
        Assert.Equal([0, 0, 0, 0, 0], erased);
        Assert.Equal([1, 1, 1, 1, 1], kept);
        Assert.Contains("\"documents\": 1", removed, StringComparison.Ordinal);
        // The log still holds the company's history, ends with the erasure, and its chain holds.
        var log = await database.AsApi.RunAsync("erase-me", db => db.Audit.OrderBy(entry => entry.Seq).Select(entry => entry.Event).ToListAsync());
        Assert.Equal("tenant_erased", log[^1]);
        Assert.Contains("rule_set_published", log);
        Assert.Null(await Migrator.VerifyAuditAsync(database.Owner));
    }

    [Fact]
    public async Task Only_the_owner_can_erase_and_the_way_in_does_not_open_the_tables_to_a_service()
    {
        await Seed("stay");
        static async Task<string> Refused(Func<Task> attempt)
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(attempt);
            return Assert.IsType<PostgresException>(error as PostgresException ?? error.InnerException).SqlState;
        }

        foreach (var scope in new[] { database.AsApi, database.AsWorker })
        {
            Assert.Equal("42501", await Refused(() => scope.RunAsync("stay", db => db.Database.ExecuteSqlAsync($"select erase_tenant('stay')"))));
            // Claiming to be erasing does not give a role the right to delete.
            Assert.Equal("42501", await Refused(() => scope.RunAsync("stay", async db =>
            {
                await db.Database.ExecuteSqlAsync($"select set_config('app.erasing', 'stay', true)");
                await db.Database.ExecuteSqlAsync($"delete from rule_sets");
            })));
        }

        var still = await Counts("stay");
        Assert.Equal([1, 1, 1, 1, 1], still);

        // The owner erasing one company cannot take another's rows with it, even by hand.
        await using var owner = NpgsqlDataSource.Create(database.Owner);
        await using var connection = await owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var claim = new NpgsqlCommand("select set_config('app.erasing', 'someone-else', true)", connection, transaction))
        {
            await claim.ExecuteNonQueryAsync();
        }

        await using var delete = new NpgsqlCommand("delete from rule_sets where tenant_id = 'stay'", connection, transaction);
        Assert.Contains("never changed or removed", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).MessageText, StringComparison.Ordinal);
    }
}
