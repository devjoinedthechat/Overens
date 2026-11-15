using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Overens.Data.Tests;

public sealed class HardeningTests(Database database) : IClassFixture<Database>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static DocumentRow Document(string tenant, char fill) =>
        new() { TenantId = tenant, Sha256 = new string(fill, 64), FileName = "a.pdf", Content = [1, 2, 3], UploadedBy = "anna", UploadedAt = Now };

    /// <summary>A company stores a document and opens a run on it.</summary>
    private Task Store(string tenant, char fill, string run) => database.AsApi.RunAsync(tenant, "anna", db =>
    {
        db.Documents.Add(Document(tenant, fill));
        db.Runs.Add(new() { Id = run, TenantId = tenant, AgreementId = "lager", DocumentSha256 = new string(fill, 64), Status = "started", CreatedAt = Now });
        return Task.CompletedTask;
    });

    private async Task<T> AsOwner<T>(string sql)
    {
        await using var owner = NpgsqlDataSource.Create(database.Owner);
        await using var command = owner.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> Refused(Func<Task> attempt)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(attempt);
        var postgres = Assert.IsType<PostgresException>(error as PostgresException ?? error.InnerException);
        return $"{postgres.SqlState} {postgres.MessageText}";
    }

    [Fact]
    public async Task A_write_that_was_rolled_back_leaves_a_gap_in_the_numbers_and_raises_no_alarm()
    {
        await Store("gap", 'a', "gap-1");
        // The entry is written and numbered, and then the transaction never commits: a dropped connection, say.
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.AsApi.RunAsync("gap", "anna", async db =>
        {
            db.Documents.Add(Document("gap", 'b'));
            await db.SaveChangesAsync();
            throw new InvalidOperationException("The request was dropped.");
        }));
        await Store("gap", 'c', "gap-2");

        var numbers = await database.AsApi.RunAsync("gap", db => db.Audit.OrderBy(entry => entry.Seq).Select(entry => entry.Seq).ToListAsync());

        Assert.Equal(4, numbers.Count);
        Assert.True(numbers[2] - numbers[1] > 1, "The rolled-back entry's number should be missing.");
        Assert.Null(await Migrator.VerifyAuditAsync(database.Owner));
    }

    [Fact]
    public async Task Each_company_has_a_chain_of_its_own()
    {
        // Written turn and turn about, so that in one chain each would follow the other.
        await Store("chain-a", 'a', "chain-a-1");
        await Store("chain-b", 'a', "chain-b-1");
        await Store("chain-a", 'b', "chain-a-2");
        await Store("chain-b", 'b', "chain-b-2");

        // Each company's first entry follows nothing, and each later one follows the same company's entry before it.
        Assert.Equal(2L, await AsOwner<long>("select count(*) from audit_log where tenant_id like 'chain-%' and prev_hash = ''"));
        Assert.Equal(0L, await AsOwner<long>("""
            select count(*) from audit_log later
              join audit_log earlier on earlier.hash = later.prev_hash
             where later.tenant_id like 'chain-%' and earlier.tenant_id <> later.tenant_id
            """));
        Assert.Null(await Migrator.VerifyAuditAsync(database.Owner));
    }

    [Fact]
    public async Task A_rule_an_approval_and_a_rule_set_cannot_point_at_another_companys_run()
    {
        await Store("mine", 'a', "mine-run");

        // Even the owner, who is not held by row-level security, is stopped by the keys.
        Assert.StartsWith("23503", await Refused(() => AsOwner<object>(
            """insert into proposed_rules (run_id, rule_id, tenant_id, rule, also_from, problems) values ('mine-run', 'r', 'theirs', '{}', '[]', '[]') returning 1""")), StringComparison.Ordinal);
        Assert.StartsWith("23503", await Refused(() => AsOwner<object>(
            """insert into approvals (run_id, tenant_id, approved_by, approved_at) values ('mine-run', 'theirs', 'mallory', now()) returning 1""")), StringComparison.Ordinal);
        Assert.StartsWith("23503", await Refused(() => AsOwner<object>(
            """insert into rule_sets (tenant_id, agreement_id, version, run_id, rules, approved_by, published_at) values ('theirs', 'lager', 1, 'mine-run', '[]', 'mallory', now()) returning 1""")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_a_companys_answers_cost_is_added_up_by_day_and_only_ever_added()
    {
        var day = new DateOnly(2026, 11, 15);
        Task<long> Note(string tenant, int calls, long micros) => database.AsApi.RunAsync(tenant, db =>
            db.Database.SqlQuery<long>($"select note_model_spend({day}, {calls}, {micros}) as \"Value\"").SingleAsync());
        Task<long> Spent(string tenant) => database.AsApi.RunAsync(tenant, db =>
            db.Database.SqlQuery<long>($"select coalesce(sum(cost_micros), 0)::bigint as \"Value\" from model_spend").SingleAsync());

        Assert.Equal(1_500, await Note("spender", 2, 1_500));
        Assert.Equal(4_000, await Note("spender", 3, 2_500));

        Assert.Equal(4_000, await Spent("spender"));
        // Another company sees none of it and starts from nothing.
        Assert.Equal(0, await Spent("frugal"));
        Assert.Equal(10, await Note("frugal", 1, 10));
        // It cannot be taken back, written past the function, or written by the worker.
        Assert.Contains("only ever added", await Refused(() => Note("spender", 1, -4_000)), StringComparison.Ordinal);
        Assert.StartsWith("42501", await Refused(() => database.AsApi.RunAsync("spender", db => db.Database.ExecuteSqlAsync($"update model_spend set cost_micros = 0"))), StringComparison.Ordinal);
        Assert.StartsWith("42501", await Refused(() => database.AsWorker.RunAsync("spender", db => db.Database.ExecuteSqlAsync($"select note_model_spend({day}, 1, 1)"))), StringComparison.Ordinal);

        // Erasing the company removes its spend with the rest.
        await Migrator.EraseTenantAsync(database.Owner, "spender");
        Assert.Equal(0, await Spent("spender"));
        Assert.Equal(10, await Spent("frugal"));
    }

    [Fact]
    public async Task The_same_document_stored_twice_at_once_is_stored_once()
    {
        var sha = new string('d', 64);
        Task Insert() => database.AsApi.RunAsync("twice", "anna", db => db.Database.ExecuteSqlAsync(
            $"insert into documents (tenant_id, sha256, file_name, content, uploaded_by, uploaded_at) values ('twice', {sha}, 'a.pdf', {new byte[] { 1 }}, 'anna', {Now}) on conflict (tenant_id, sha256) do nothing"));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Insert()));

        Assert.Equal(1, await database.AsApi.RunAsync("twice", db => db.Documents.CountAsync()));
        // Stored once, logged once.
        Assert.Equal(1, await database.AsApi.RunAsync("twice", db => db.Audit.CountAsync(entry => entry.Event == "document_stored")));
    }
}
