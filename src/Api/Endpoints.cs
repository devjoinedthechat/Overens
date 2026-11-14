using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Overens.Data;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Pipeline;
using Overens.Ingestion.Reading;
using Overens.Rules;

namespace Overens.Api;

public static partial class Endpoints
{
    public const int MaxPdfBytes = 20 * 1024 * 1024;
    public const string UploadLimit = "uploads";

    public static void MapOverens(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup("").RequireAuthorization(Caller.Specialist);

        // An agreement's PDF is uploaded. It is stored, a run is opened, and the pipeline starts.
        api.MapPost("/agreements/{agreementId}/runs", async (
            string agreementId, HttpContext http, TenantScope database, IPipeline pipeline, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (Caller.From(http.User) is not { } caller)
            {
                return Results.Forbid();
            }

            if (!AgreementId().IsMatch(agreementId))
            {
                return Problem(400, "An agreement id is 1 to 64 lower-case letters, digits or hyphens.");
            }

            if (http.Request.ContentType != "application/pdf")
            {
                return Problem(415, "Send the agreement as application/pdf.");
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await http.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxPdfBytes)
                {
                    return Problem(413, "The file is larger than 20 MB.");
                }

                buffer.Write(chunk, 0, read);
            }

            var pdf = buffer.ToArray();
            if (!pdf.AsSpan().StartsWith("%PDF-"u8))
            {
                return Problem(400, "The file is not a PDF.");
            }

            var sha = Convert.ToHexStringLower(SHA256.HashData(pdf));
            var run = new RunRef(caller.TenantId, $"run_{Guid.CreateVersion7():N}");
            await database.RunAsync(caller.TenantId, async db =>
            {
                // The same bytes are stored once, however often they are uploaded.
                if (!await db.Documents.AnyAsync(document => document.Sha256 == sha, cancellationToken))
                {
                    db.Documents.Add(new() { TenantId = caller.TenantId, Sha256 = sha, FileName = $"{agreementId}.pdf", Content = pdf, UploadedBy = caller.Name, UploadedAt = clock.GetUtcNow() });
                }

                db.Runs.Add(new() { Id = run.RunId, TenantId = caller.TenantId, AgreementId = agreementId, DocumentSha256 = sha, Status = "started", CreatedAt = clock.GetUtcNow() });
            }, cancellationToken);
            await pipeline.StartAsync(run, cancellationToken);
            return Results.Accepted($"/runs/{run.RunId}", new { id = run.RunId, status = "started" });
        }).RequireRateLimiting(UploadLimit);

        // The company's runs, newest first.
        api.MapGet("/runs", (HttpContext http, TenantScope database, CancellationToken cancellationToken) =>
            AsCaller(http, caller => database.RunAsync(caller.TenantId, async db =>
            {
                var runs = await db.Runs.OrderByDescending(run => run.CreatedAt).Take(50)
                    .Select(run => new { id = run.Id, agreementId = run.AgreementId, status = run.Status, title = run.Title, createdAt = run.CreatedAt })
                    .ToListAsync(cancellationToken);
                return Results.Ok(runs);
            }, cancellationToken)));

        // What a reviewer needs to judge a run: each proposed rule beside the words it came from.
        api.MapGet("/runs/{runId}", (string runId, HttpContext http, TenantScope database, CancellationToken cancellationToken) =>
            AsCaller(http, caller => database.RunAsync(caller.TenantId, async db =>
            {
                if (await db.Runs.SingleOrDefaultAsync(row => row.Id == runId, cancellationToken) is not { } run)
                {
                    return Results.NotFound();
                }

                var agreement = run.Clauses is null ? null : RuleJson.Read<ReadAgreement>(run.Clauses);
                var clauses = agreement?.Sections.SelectMany(section => section.Clauses).ToDictionary(clause => clause.Id);
                var rules = await db.ProposedRules.Where(rule => rule.RunId == runId).OrderBy(rule => rule.RuleId).ToListAsync(cancellationToken);
                return Results.Ok(new
                {
                    id = run.Id,
                    agreementId = run.AgreementId,
                    status = run.Status,
                    title = run.Title,
                    model = run.Model,
                    promptVersion = run.PromptVersion,
                    failure = run.Failure,
                    gaps = run.Gaps is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(run.Gaps),
                    rules = rules.Select(row =>
                    {
                        var rule = RuleJson.Read<Rule>(row.Rule);
                        var cited = new[] { rule.Source.ClauseId }.Concat(RuleJson.Read<List<string>>(row.AlsoFrom)).Distinct();
                        return new
                        {
                            id = row.RuleId,
                            rule = JsonSerializer.SerializeToElement(rule, RuleJson.Options),
                            quote = rule.Source.Quote,
                            clauses = cited.Where(id => clauses is not null && clauses.ContainsKey(id)).Select(id => new
                            {
                                id,
                                text = ExtractionPrompt.Text(clauses![id]),
                                firstPage = agreement!.Pages[id].First,
                                lastPage = agreement.Pages[id].Last,
                            }),
                            problems = JsonSerializer.Deserialize<JsonElement>(row.Problems),
                            decision = row.Decision,
                            decidedBy = row.DecidedBy,
                        };
                    }),
                });
            }, cancellationToken)));

        // A reviewer approves or rejects one proposed rule.
        api.MapPost("/runs/{runId}/rules/{ruleId}/decision", (
            string runId, string ruleId, DecisionRequest request, HttpContext http, TenantScope database, TimeProvider clock, CancellationToken cancellationToken) =>
            AsCaller(http, caller => database.RunAsync(caller.TenantId, async db =>
            {
                if (request.Decision is not ("approved" or "rejected"))
                {
                    return Problem(400, "A decision is \"approved\" or \"rejected\".");
                }

                var run = await db.Runs.SingleOrDefaultAsync(row => row.Id == runId, cancellationToken);
                var rule = await db.ProposedRules.SingleOrDefaultAsync(row => row.RunId == runId && row.RuleId == ruleId, cancellationToken);
                if (run is null || rule is null)
                {
                    return Results.NotFound();
                }

                if (run.Status != "awaiting_review" || await db.Approvals.AnyAsync(approval => approval.RunId == runId, cancellationToken))
                {
                    return Problem(409, "The run is not open for review.");
                }

                if (request.Decision == "approved" && rule.Problems != "[]")
                {
                    return Problem(409, "The checks found a problem with this rule, so it cannot be approved as it stands.");
                }

                (rule.Decision, rule.DecidedBy, rule.DecidedAt) = (request.Decision, caller.Name, clock.GetUtcNow());
                return Results.Ok(new { id = ruleId, decision = rule.Decision });
            }, cancellationToken)));

        // A reviewer approves the run as a whole. This is the person's approval the database requires before it publishes.
        api.MapPost("/runs/{runId}/approval", async (string runId, HttpContext http, TenantScope database, IPipeline pipeline, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (Caller.From(http.User) is not { } caller)
            {
                return Results.Forbid();
            }

            var refused = await database.RunAsync(caller.TenantId, async db =>
            {
                if (await db.Runs.SingleOrDefaultAsync(row => row.Id == runId, cancellationToken) is not { } run)
                {
                    return Results.NotFound();
                }

                if (run.Status != "awaiting_review")
                {
                    return Problem(409, "The run is not open for review.");
                }

                var decisions = await db.ProposedRules.Where(rule => rule.RunId == runId).Select(rule => rule.Decision).ToListAsync(cancellationToken);
                if (decisions.Contains("pending"))
                {
                    return Problem(409, "Every rule must be approved or rejected first.");
                }

                if (!decisions.Contains("approved"))
                {
                    return Problem(409, "No rule is approved. Reject the run instead.");
                }

                // Approving twice is harmless: the first approval stands.
                if (!await db.Approvals.AnyAsync(approval => approval.RunId == runId, cancellationToken))
                {
                    db.Approvals.Add(new() { RunId = runId, TenantId = caller.TenantId, ApprovedBy = caller.Name, ApprovedAt = clock.GetUtcNow() });
                }

                return null;
            }, cancellationToken);
            if (refused is not null)
            {
                return refused;
            }

            await pipeline.DecideAsync(new(caller.TenantId, runId), publish: true, cancellationToken);
            return Results.Accepted($"/runs/{runId}", new { id = runId, status = "publishing" });
        });

        // A reviewer rejects the run as a whole. Nothing is published.
        api.MapPost("/runs/{runId}/rejection", async (string runId, HttpContext http, TenantScope database, IPipeline pipeline, CancellationToken cancellationToken) =>
        {
            if (Caller.From(http.User) is not { } caller)
            {
                return Results.Forbid();
            }

            var status = await database.RunAsync(caller.TenantId, db => db.Runs.Where(row => row.Id == runId).Select(row => row.Status).SingleOrDefaultAsync(cancellationToken), cancellationToken);
            if (status is null)
            {
                return Results.NotFound();
            }

            if (status != "awaiting_review")
            {
                return Problem(409, "The run is not open for review.");
            }

            await pipeline.DecideAsync(new(caller.TenantId, runId), publish: false, cancellationToken);
            return Results.Accepted($"/runs/{runId}", new { id = runId, status = "rejecting" });
        });

        // The published versions of an agreement's rules, newest first.
        api.MapGet("/agreements/{agreementId}/rule-sets", (string agreementId, HttpContext http, TenantScope database, CancellationToken cancellationToken) =>
            AsCaller(http, caller => database.RunAsync(caller.TenantId, async db =>
            {
                var sets = await db.RuleSets.Where(set => set.AgreementId == agreementId).OrderByDescending(set => set.Version).ToListAsync(cancellationToken);
                return Results.Ok(sets.Select(set => new
                {
                    agreementId = set.AgreementId,
                    version = set.Version,
                    approvedBy = set.ApprovedBy,
                    publishedAt = set.PublishedAt,
                    rules = JsonSerializer.SerializeToElement(RuleJson.Read<List<Rule>>(set.Rules), RuleJson.Options),
                }));
            }, cancellationToken)));
    }

    private static async Task<IResult> AsCaller(HttpContext http, Func<Caller, Task<IResult>> work) =>
        Caller.From(http.User) is { } caller ? await work(caller) : Results.Forbid();

    private static IResult Problem(int status, string detail) => Results.Problem(detail, statusCode: status);

    [GeneratedRegex("^[a-z0-9-]{1,64}$")]
    private static partial Regex AgreementId();
}

public sealed record DecisionRequest(string Decision);
