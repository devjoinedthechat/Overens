using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Overens.Agent.Questions;
using Overens.Data;
using Overens.Ingestion.Reading;
using Overens.Rules;

namespace Overens.Api;

public sealed record QuestionRequest(string Question);

/// <summary>The question agent, when a model is configured for it. Without one, questions are not answered at all.</summary>
public sealed record QuestionService(QuestionAgent? Agent);

public static class QuestionEndpoints
{
    public const string Limit = "questions";

    public static void MapQuestions(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // A question about an agreement whose rules are published. The agent reads the
        // agreement and the published rules, and its answer is shown only if it passes the
        // checks. Nothing is stored.
        app.MapPost("/agreements/{agreementId}/questions", async (
            string agreementId, QuestionRequest request, HttpContext http, TenantScope database, QuestionService questions, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (Caller.From(http.User) is not { } caller)
            {
                return Results.Forbid();
            }

            if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 500)
            {
                return Results.Problem("A question is 1 to 500 characters.", statusCode: 400);
            }

            if (questions.Agent is null)
            {
                return Results.Problem("No model is configured, so questions cannot be answered.", statusCode: 503);
            }

            var (versions, clauses) = await database.RunAsync(caller.TenantId, async db =>
            {
                var sets = await db.RuleSets.Where(set => set.AgreementId == agreementId).OrderBy(set => set.Version).ToListAsync(cancellationToken);
                var newest = sets.Count == 0 ? null : sets[^1].RunId;
                return (sets, await db.Runs.Where(run => run.Id == newest).Select(run => run.Clauses).SingleOrDefaultAsync(cancellationToken));
            }, cancellationToken);
            if (versions.Count == 0 || clauses is null)
            {
                return Results.NotFound();
            }

            var rules = EffectiveRules.From(versions.Select(set => new RuleSet(set.AgreementId, set.Version, RuleJson.Read<List<Rule>>(set.Rules))));
            var index = new ClauseIndex(RuleJson.Read<ReadAgreement>(clauses).Sections);
            var answer = await questions.Agent.AskAsync(request.Question, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), rules, index, cancellationToken);
            // That a question was asked is logged, with how it ended. The question itself is not.
            var note = JsonSerializer.Serialize(new { outcome = answer.Outcome.ToString(), steps = answer.Trace.Count });
            await database.RunAsync(caller.TenantId, caller.Name, db =>
                db.Database.ExecuteSqlAsync($"select audit_note('question_asked', {agreementId}, {note}::jsonb)", cancellationToken), cancellationToken);

            return Results.Json(
                new
                {
                    outcome = answer.Outcome.ToString(),
                    text = answer.Text,
                    citations = answer.Citations.Select(citation => new { clauseId = citation.ClauseId, quote = citation.Quote }),
                    steps = answer.Trace,
                },
                new JsonSerializerOptions(RuleJson.Options));
        }).RequireAuthorization(Caller.ChecksPay).RequireRateLimiting(Limit);
    }
}
