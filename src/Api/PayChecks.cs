using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Overens.Agent;
using Overens.Data;
using Overens.Rules;

namespace Overens.Api;

/// <summary>One employee's pay period to check: what they worked and what the payslip says.</summary>
public sealed record PayCheckRequest(Timesheet Timesheet, Payslip Payslip);

public static partial class PayChecks
{
    private const int MaxBodyBytes = 512 * 1024;

    public static void MapPayChecks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // A payslip is checked against the agreement's published rules. The decision is
        // arithmetic. Nothing about the employee is stored, and nothing but the finding
        // itself, with no employee id, is ever shown to a model.
        app.MapPost("/agreements/{agreementId}/pay-checks", async (
            string agreementId, HttpContext http, TenantScope database, FindingExplainer explainer, CancellationToken cancellationToken) =>
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

            if (request?.Timesheet?.Employee is null || request.Payslip?.Lines is null || request.Timesheet.Shifts is null)
            {
                return Refuse(400, "The request is not a timesheet and a payslip.");
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
            catch (ArgumentException invalid)
            {
                return Refuse(400, invalid.Message);
            }

            var findings = new List<object>();
            foreach (var finding in check.Findings)
            {
                var explanation = await explainer.ExplainAsync(finding, check.Expected, cancellationToken);
                findings.Add(new
                {
                    kind = finding.Kind.ToString(),
                    line = finding.Line?.ToString(),
                    expected = finding.Expected,
                    actual = finding.Actual,
                    difference = finding.Difference,
                    clauses = finding.Rules.Select(rule => new { id = rule.Source.ClauseId, quote = rule.Source.Quote, ruleId = rule.Id }),
                    explanation = explanation.Text,
                    explainedByModel = explanation.ByModel,
                });
            }

            // That a check was made is logged, with its size. Who was checked and what they were paid is not.
            var note = JsonSerializer.Serialize(new { version = rules.Version, findings = findings.Count });
            await database.RunAsync(caller.TenantId, caller.Name, db =>
                db.Database.ExecuteSqlAsync($"select audit_note('pay_checked', {agreementId}, {note}::jsonb)", cancellationToken), cancellationToken);

            return Results.Json(
                new
                {
                    agreementId,
                    version = rules.Version,
                    expected = check.Expected,
                    findings,
                },
                RuleJson.Options);
        }).RequireAuthorization(Caller.ChecksPay);
    }

    private static IResult Refuse(int status, string detail) => Results.Problem(detail, statusCode: status);

    [GeneratedRegex("^[A-Za-z0-9-]{1,40}$")]
    private static partial Regex EmployeeId();

    /// <summary>Ten digits, with or without the hyphen after the sixth: the shape of a CPR number.</summary>
    [GeneratedRegex(@"^\d{6}-?\d{4}$")]
    private static partial Regex PersonalNumber();
}
