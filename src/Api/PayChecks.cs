using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Overens.Agent;
using Overens.Data;
using Overens.Gateway;
using Overens.Rules;

namespace Overens.Api;

/// <summary>One employee's pay period to check: what they worked and what the payslip says.</summary>
public sealed record PayCheckRequest(Timesheet Timesheet, Payslip Payslip);

public static partial class PayChecks
{
    public const string Limit = "pay-checks";

    private const int MaxBodyBytes = 512 * 1024;

    /// <summary>Above any amount on a real payslip, and far below where the arithmetic could overflow.</summary>
    private const decimal MaxAmount = 100_000_000m;

    public static void MapPayChecks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // A payslip is checked against the agreement's published rules. The decision is
        // arithmetic. Nothing about the employee is stored, and nothing but the finding
        // itself, with no employee id, is ever shown to a model.
        app.MapPost("/agreements/{agreementId}/pay-checks", async (
            string agreementId, HttpContext http, TenantScope database, FindingExplainer explainer, ModelBudget budget, CancellationToken cancellationToken) =>
        {
            if (Caller.From(http.User) is not { } caller)
            {
                return Results.Forbid();
            }

            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            {
                size.MaxRequestBodySize = MaxBodyBytes;
            }

            PayCheckRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<PayCheckRequest>(http.Request.Body, RuleJson.Options, cancellationToken);
            }
            catch (JsonException)
            {
                return Refuse(400, "The request is not a timesheet and a payslip.");
            }

            // Every part is checked for being there before anything reads it: JSON can leave any of them out, or put null in a list.
            if (request?.Timesheet?.Employee?.Id is null || request.Payslip?.Lines is null || request.Payslip.EmployeeId is null || request.Timesheet.Shifts is null
                || request.Timesheet.Shifts.Any(shift => shift is null) || request.Payslip.Lines.Any(line => line is null))
            {
                return Refuse(400, "The request is not a timesheet and a payslip.");
            }

            if (request.Payslip.HourlyRate is < 0 or > PayEngine.MaxHourlyRate || request.Payslip.Lines.Any(line => Math.Abs(line.Amount) > MaxAmount))
            {
                return Refuse(400, "An amount on the payslip is outside what a payslip can hold.");
            }

            // An employee is known here by an id that says nothing about the person.
            if (!EmployeeId().IsMatch(request.Timesheet.Employee.Id) || PersonalNumber().IsMatch(request.Timesheet.Employee.Id))
            {
                return Refuse(400, "An employee id is 1 to 40 letters, digits or hyphens, and must not be a CPR number.");
            }

            if (request.Timesheet.Shifts.Count > 200 || request.Payslip.Lines.Count > 20
                || request.Timesheet.PeriodEnd.DayNumber - request.Timesheet.PeriodStart.DayNumber is < 0 or > 62)
            {
                return Refuse(400, "A pay period is at most two months, 200 shifts and 20 lines.");
            }

            var versions = await database.RunAsync(caller.TenantId, db =>
                db.RuleSets.Where(set => set.AgreementId == agreementId).ToListAsync(cancellationToken), cancellationToken);
            if (versions.Count == 0)
            {
                return Results.NotFound();
            }

            var rules = EffectiveRules.From(versions.Select(set => new RuleSet(set.AgreementId, set.Version, RuleJson.Read<List<Rule>>(set.Rules))));
            PayCheck check;
            try
            {
                check = PayEngine.Check(rules, request.Timesheet, request.Payslip);
            }
            catch (Exception invalid) when (invalid is ArgumentException or OverflowException)
            {
                return Refuse(400, invalid is ArgumentException ? invalid.Message : "An amount in the request is outside what can be worked out.");
            }

            using var span = Telemetry.Start("pay.check").With(Field.Tenant, caller.TenantId).With(Field.Agreement, agreementId).With(Field.Version, rules.Version).With(Field.Findings, check.Findings.Count);

            // The findings are worded at the same time, so a slow model costs the person one wait and not one per finding.
            // A company that has spent its day's budget gets the fixed wording, which costs nothing.
            var worded = check.Findings.Count > 0 && await budget.HasRoomAsync(caller.TenantId, cancellationToken);
            Explanation[] explanations;
            using (var usage = Usage.Begin())
            {
                explanations = await Task.WhenAll(check.Findings.Select(finding => worded
                    ? explainer.ExplainAsync(finding, check.Expected, cancellationToken)
                    : Task.FromResult(new Explanation(FindingExplainer.Fixed(finding), false))));
                await budget.ChargeAsync(caller.TenantId, usage, cancellationToken);
                span.With(Field.ModelCalls, usage.Calls).With(Field.CostMicros, usage.CostMicros);
            }

            var findings = check.Findings.Zip(explanations, (finding, explanation) => new FindingView(
                finding.Kind.ToString(),
                finding.Line?.ToString(),
                finding.Expected,
                finding.Actual,
                finding.Difference,
                [.. finding.Rules.Select(rule => new FindingClause(rule.Source.ClauseId, rule.Source.Quote, rule.Id))],
                explanation.Text,
                explanation.ByModel)).ToList();

            // That a check was made is logged, with its size. Who was checked and what they were paid is not.
            var note = JsonSerializer.Serialize(new { version = rules.Version, findings = findings.Count });
            await database.RunAsync(caller.TenantId, caller.Name, db =>
                db.Database.ExecuteSqlAsync($"select audit_note('pay_checked', {agreementId}, {note}::jsonb)", cancellationToken), cancellationToken);

            return Results.Json(new PayCheckResult(agreementId, rules.Version, check.Expected, findings), RuleJson.Options);
        }).RequireAuthorization(Caller.ChecksPay).RequireRateLimiting(Limit)
            .WithSummary("Check one employee's payslip against the agreement's published rules")
            .Accepts<PayCheckRequest>("application/json")
            .Produces<PayCheckResult>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static IResult Refuse(int status, string detail) => Results.Problem(detail, statusCode: status);

    [GeneratedRegex("^[A-Za-z0-9-]{1,40}$")]
    private static partial Regex EmployeeId();

    /// <summary>Ten digits, with or without the hyphen after the sixth: the shape of a CPR number.</summary>
    [GeneratedRegex(@"^\d{6}-?\d{4}$")]
    private static partial Regex PersonalNumber();
}
