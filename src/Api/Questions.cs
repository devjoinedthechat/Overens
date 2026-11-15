using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Overens.Agent.Questions;
using Overens.Data;
using Overens.Gateway;
using Overens.Ingestion.Reading;
using Overens.Rules;

namespace Overens.Api;

public sealed record QuestionRequest(string Question);

/// <summary>The question agent, when a model is configured for it. Without one, questions are not answered at all.</summary>
public sealed record QuestionService(QuestionAgent? Agent);

public static partial class QuestionEndpoints
{
    public const string Limit = "questions";

    private static readonly JsonSerializerOptions Json = new(RuleJson.Options);

    public static void MapQuestions(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // A question about an agreement whose rules are published. The agent reads the
        // agreement and the published rules, and its answer is shown only if it passes the
        // checks. Nothing is stored.
        app.MapPost("/agreements/{agreementId}/questions", async (
            string agreementId, QuestionRequest request, HttpContext http, TenantScope database, QuestionService questions, ModelBudget budget, TimeProvider clock, ILoggerFactory logs, CancellationToken cancellationToken) =>
        {
            if (Caller.From(http.User) is not { } caller)
            {
                return Results.Forbid();
            }

            if (string.IsNullOrWhiteSpace(request?.Question) || request.Question.Length > 500)
            {
                return Results.Problem("A question is 1 to 500 characters.", statusCode: 400);
            }

            if (questions.Agent is null)
            {
                return Results.Problem("No model is configured, so questions cannot be answered.", statusCode: 503);
            }

            // Every published version's agreement is read, newest first: a rule from an earlier
            // agreement that still applies cites that agreement's words, not the newest one's.
            var (versions, documents) = await database.RunAsync(caller.TenantId, async db =>
            {
                var sets = await db.RuleSets.Where(set => set.AgreementId == agreementId).OrderBy(set => set.Version).ToListAsync(cancellationToken);
                var runs = sets.Select(set => set.RunId).ToList();
                var read = await db.Runs.Where(run => runs.Contains(run.Id)).Select(run => new { run.Id, run.Clauses }).ToListAsync(cancellationToken);
                return (sets, sets.AsEnumerable().Reverse().Select(set => read.Single(run => run.Id == set.RunId).Clauses).OfType<string>().ToList());
            }, cancellationToken);
            if (versions.Count == 0 || documents.Count == 0)
            {
                return Results.NotFound();
            }

            if (!await budget.HasRoomAsync(caller.TenantId, cancellationToken))
            {
                return Results.Problem(ModelBudget.UsedUp, statusCode: 429);
            }

            var rules = EffectiveRules.From(versions.Select(set => new RuleSet(set.AgreementId, set.Version, RuleJson.Read<List<Rule>>(set.Rules))));
            var agreements = documents.Select(document => RuleJson.Read<ReadAgreement>(document).Sections).ToList();
            var index = new ClauseIndex(agreements[0], agreements.Skip(1));
            using var span = Telemetry.Start("question").With(Field.Tenant, caller.TenantId).With(Field.Agreement, agreementId);
            QuestionAnswer answer;
            using (var usage = Usage.Begin())
            {
                try
                {
                    answer = await questions.Agent.AskAsync(request.Question, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), rules, index, cancellationToken);
                }
                catch (Exception failed) when (failed is not OperationCanceledException)
                {
                    // The model is away, slow or out of budget. That is not the person's fault and not a broken API: say so, and say it will pass.
                    NotAnswered(logs.CreateLogger("Overens.Api.Questions"), agreementId, failed);
                    span.With(Field.Outcome, "unavailable");
                    return Results.Problem("The model that answers questions cannot be reached right now. Try again shortly.", statusCode: 503);
                }
                finally
                {
                    // What was spent is counted whether or not an answer came of it.
                    await budget.ChargeAsync(caller.TenantId, usage, CancellationToken.None);
                    span.With(Field.ModelCalls, usage.Calls).With(Field.InputTokens, usage.InputTokens).With(Field.OutputTokens, usage.OutputTokens).With(Field.CostMicros, usage.CostMicros);
                }
            }

            span.With(Field.Outcome, answer.Outcome.ToString()).With(Field.Steps, answer.Trace.Count);

            // That a question was asked is logged, with how it ended. The question itself is not.
            var note = JsonSerializer.Serialize(new { outcome = answer.Outcome.ToString(), steps = answer.Trace.Count });
            await database.RunAsync(caller.TenantId, caller.Name, db =>
                db.Database.ExecuteSqlAsync($"select audit_note('question_asked', {agreementId}, {note}::jsonb)", cancellationToken), cancellationToken);

            return Results.Json(
                new QuestionResult(answer.Outcome.ToString(), answer.Text, [.. answer.Citations.Select(citation => new CitationView(citation.ClauseId, citation.Quote))], answer.Trace),
                Json);
        }).RequireAuthorization(Caller.ChecksPay).RequireRateLimiting(Limit)
            .WithSummary("Ask a question about an agreement whose rules are published")
            .Produces<QuestionResult>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A question about {AgreementId} was not answered: the model failed")]
    private static partial void NotAnswered(ILogger logger, string agreementId, Exception failed);
}
