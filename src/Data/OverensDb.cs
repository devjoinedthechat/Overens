using Microsoft.EntityFrameworkCore;

namespace Overens.Data;

public sealed class DocumentRow
{
    public required string TenantId { get; init; }
    public required string Sha256 { get; init; }
    public required string FileName { get; init; }
    public required byte[] Content { get; init; }
    public required string UploadedBy { get; init; }
    public required DateTimeOffset UploadedAt { get; init; }
}

public sealed class IngestionRunRow
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string AgreementId { get; init; }
    public required string DocumentSha256 { get; init; }
    public required string Status { get; set; }
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public string? Title { get; set; }
    public string? Clauses { get; set; }
    public string? Gaps { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public int? ModelCalls { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? CostMicros { get; set; }
    public string? Failure { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class ProposedRuleRow
{
    public required string RunId { get; init; }
    public required string RuleId { get; init; }
    public required string TenantId { get; init; }
    public required string Rule { get; init; }
    public required string AlsoFrom { get; init; }
    public required string Problems { get; init; }
    public string Decision { get; set; } = "pending";
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Reason { get; set; }
}

public sealed class ApprovalRow
{
    public required string RunId { get; init; }
    public required string TenantId { get; init; }
    public required string ApprovedBy { get; init; }
    public required DateTimeOffset ApprovedAt { get; init; }
}

public sealed class RuleSetRow
{
    public required string TenantId { get; init; }
    public required string AgreementId { get; init; }
    public required int Version { get; init; }
    public required string RunId { get; init; }
    public required string Rules { get; init; }
    public required string ApprovedBy { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
}

public sealed class AuditRow
{
    public required long Seq { get; init; }
    public required string TenantId { get; init; }
    public required DateTimeOffset At { get; init; }
    public required string Actor { get; init; }
    public required string Event { get; init; }
    public required string Subject { get; init; }
    public required string Detail { get; init; }
}

/// <summary>Typed access to the tables. The schema itself is in <c>Schema/*.sql</c>, not generated from this.</summary>
public sealed class OverensDb(DbContextOptions<OverensDb> options) : DbContext(options)
{
    public DbSet<DocumentRow> Documents => Set<DocumentRow>();

    public DbSet<IngestionRunRow> Runs => Set<IngestionRunRow>();

    public DbSet<ProposedRuleRow> ProposedRules => Set<ProposedRuleRow>();

    public DbSet<ApprovalRow> Approvals => Set<ApprovalRow>();

    public DbSet<RuleSetRow> RuleSets => Set<RuleSetRow>();

    /// <summary>The audit log, for reading. Entries are written by the database itself.</summary>
    public DbSet<AuditRow> Audit => Set<AuditRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentRow>().ToTable("documents").HasKey(row => new { row.TenantId, row.Sha256 });
        modelBuilder.Entity<IngestionRunRow>(run =>
        {
            run.ToTable("ingestion_runs").HasKey(row => row.Id);
            run.Property(row => row.Clauses).HasColumnType("jsonb");
            run.Property(row => row.Gaps).HasColumnType("jsonb");
        });
        modelBuilder.Entity<ProposedRuleRow>(rule =>
        {
            rule.ToTable("proposed_rules").HasKey(row => new { row.RunId, row.RuleId });
            rule.Property(row => row.Rule).HasColumnType("jsonb");
            rule.Property(row => row.AlsoFrom).HasColumnType("jsonb");
            rule.Property(row => row.Problems).HasColumnType("jsonb");
        });
        modelBuilder.Entity<ApprovalRow>().ToTable("approvals").HasKey(row => row.RunId);
        modelBuilder.Entity<AuditRow>(audit =>
        {
            audit.ToTable("audit_log").HasKey(row => row.Seq);
            audit.Property(row => row.Detail).HasColumnType("jsonb");
        });
        modelBuilder.Entity<RuleSetRow>(set =>
        {
            set.ToTable("rule_sets").HasKey(row => new { row.TenantId, row.AgreementId, row.Version });
            set.Property(row => row.Rules).HasColumnType("jsonb");
        });

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
        {
            property.SetColumnName(SnakeCase.Of(property.Name));
        }
    }
}
