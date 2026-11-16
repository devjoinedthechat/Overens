using System.Text.Json;
using Overens.Rules;

namespace Overens.Api;

// What the API answers with, one record per answer, so the contract can be read from the
// OpenAPI document and not only from the code.

public sealed record RunAccepted(string Id, string Status);

public sealed record RunSummary(string Id, string AgreementId, string Status, string? Title, DateTimeOffset CreatedAt);

/// <summary>What a reviewer needs to judge a run: each proposed rule beside the words it came from.</summary>
public sealed record RunDetail(
    string Id,
    string AgreementId,
    string Status,
    string? Title,
    string? Model,
    string? PromptVersion,
    string? Failure,
    bool Dispatched,
    decimal? Cost,
    JsonElement? Gaps,
    JsonElement? Changes,
    IReadOnlyList<ProposedRuleView> Rules);

public sealed record ProposedRuleView(
    string Id,
    JsonElement Rule,
    string Quote,
    IReadOnlyList<ClauseView> Clauses,
    JsonElement Problems,
    string Decision,
    string? DecidedBy,
    string? Reason);

public sealed record ClauseView(string Id, string Text, int FirstPage, int LastPage);

public sealed record DecisionMade(string Id, string Decision);

/// <summary>A rule a reviewer rejected, with what is needed to turn it into an eval case.</summary>
public sealed record FeedbackItem(
    string RunId,
    string AgreementId,
    string Document,
    string? Model,
    string? PromptVersion,
    JsonElement Proposed,
    JsonElement Problems,
    string? Reason,
    string? RejectedBy,
    IReadOnlyList<ClauseWords> Clauses);

public sealed record ClauseWords(string Id, string Text);

public sealed record AuditEntry(DateTimeOffset At, string Actor, string Event, string Subject, JsonElement Detail);

public sealed record RuleSetView(string AgreementId, int Version, string ApprovedBy, DateTimeOffset PublishedAt, JsonElement Rules);

/// <param name="AgreementId">The agreement.</param>
/// <param name="Version">The newest version of its rules the check used.</param>
/// <param name="Expected">What each line should be, part by part.</param>
/// <param name="Findings">Where the payslip differs, and lines that could not be checked (kind <c>NotCovered</c>).</param>
/// <param name="NotChecked">The lines no approved rule covers in the period. Nothing about them was checked.</param>
public sealed record PayCheckResult(string AgreementId, int Version, IReadOnlyList<Component> Expected, IReadOnlyList<FindingView> Findings, IReadOnlyList<string> NotChecked);

public sealed record FindingView(
    string Kind,
    string? Line,
    decimal Expected,
    decimal Actual,
    decimal Difference,
    IReadOnlyList<FindingClause> Clauses,
    string Explanation,
    bool ExplainedByModel);

public sealed record FindingClause(string Id, string Quote, string RuleId);

public sealed record QuestionResult(string Outcome, string Text, IReadOnlyList<CitationView> Citations, IReadOnlyList<string> Steps);

public sealed record CitationView(string ClauseId, string Quote);

public sealed record ServiceInfo(string Service);

public sealed record ConsoleConfig(string Authority, string ClientId, string Scope, string TenantClaim);
