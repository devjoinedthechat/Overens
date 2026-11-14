using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Api.Tests;

public sealed class ApiTests(Site site) : IClassFixture<Site>
{
    private static int counter;

    private static string NewTenant() => $"tenant-{Interlocked.Increment(ref counter)}";

    private static ByteArrayContent PdfBody(byte[]? bytes = null, string type = "application/pdf")
    {
        var content = new ByteArrayContent(bytes ?? Site.Pdf);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return content;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    private static async Task<string> Upload(HttpClient client, string agreement = "lager")
    {
        var response = await client.PostAsync($"/agreements/{agreement}/runs", PdfBody());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> Run(HttpClient client, string run) => await Json(await client.GetAsync($"/runs/{run}"));

    private static Task<HttpResponseMessage> Decide(HttpClient client, string run, string rule, string decision) =>
        client.PostAsJsonAsync($"/runs/{run}/rules/{rule}/decision", new { decision });

    private static async Task DecideAll(HttpClient client, string run)
    {
        foreach (var rule in (await Run(client, run)).GetProperty("rules").EnumerateArray())
        {
            var decision = rule.GetProperty("problems").GetArrayLength() == 0 ? "approved" : "rejected";
            Assert.Equal(HttpStatusCode.OK, (await Decide(client, run, rule.GetProperty("id").GetString()!, decision)).StatusCode);
        }
    }

    [Fact]
    public async Task A_specialist_uploads_an_agreement_reviews_each_rule_and_publishes()
    {
        var anna = site.As(NewTenant());

        var run = await Upload(anna);
        var review = await Run(anna, run);

        Assert.Equal("awaiting_review", review.GetProperty("status").GetString());
        Assert.Equal(Site.Agreement.Document.Title, review.GetProperty("title").GetString());
        Assert.Equal(Site.Agreement.Truth.Rules.Count, review.GetProperty("rules").GetArrayLength());

        // A first version changes everything from nothing: one change per kind of rule and period.
        var changes = review.GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(Site.Agreement.Truth.Rules.Count, changes.Count);
        Assert.All(changes, change => Assert.Equal(JsonValueKind.Null, change.GetProperty("before").ValueKind));

        // Each rule comes with the words it was read from, and where they stand.
        var overtime = review.GetProperty("rules").EnumerateArray().Single(rule => rule.GetProperty("id").GetString() == "overtime-2026");
        var clauses = overtime.GetProperty("clauses").EnumerateArray().ToList();
        Assert.Contains(overtime.GetProperty("quote").GetString()!, clauses[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains(clauses, clause => clause.GetProperty("text").GetString()!.Contains("7 timer og 24 minutter", StringComparison.Ordinal));
        Assert.All(clauses, clause => Assert.InRange(clause.GetProperty("firstPage").GetInt32(), 3, 6));

        await DecideAll(anna, run);
        Assert.Equal(HttpStatusCode.Accepted, (await anna.PostAsync($"/runs/{run}/approval", null)).StatusCode);

        Assert.Equal("published", (await Run(anna, run)).GetProperty("status").GetString());
        var set = Assert.Single((await Json(await anna.GetAsync("/agreements/lager/rule-sets"))).EnumerateArray());
        Assert.Equal((1, "anna"), (set.GetProperty("version").GetInt32(), set.GetProperty("approvedBy").GetString()));
        var rules = RuleJson.Read<List<Rule>>(set.GetProperty("rules").GetRawText());
        Assert.Equal(
            RuleJson.Write(Site.Agreement.Truth.Rules.OrderBy(rule => rule.Id, StringComparer.Ordinal)),
            RuleJson.Write(rules.OrderBy(rule => rule.Id, StringComparer.Ordinal)));

        // The company's runs are listed, newest first.
        var listed = Assert.Single((await Json(await anna.GetAsync("/runs"))).EnumerateArray());
        Assert.Equal((run, "published"), (listed.GetProperty("id").GetString(), listed.GetProperty("status").GetString()));

        // Every step is in the company's audit log, with the person who took it.
        var log = (await Json(await anna.GetAsync("/audit"))).EnumerateArray().Reverse().Select(entry => $"{entry.GetProperty("event").GetString()} {entry.GetProperty("actor").GetString()}").ToList();
        Assert.Equal(["document_stored anna", "run_started anna", "run_awaiting_review pipeline"], log.Take(3));
        Assert.Equal(13, log.Count(entry => entry == "rule_approved anna"));
        Assert.Equal(["run_approved anna", "rule_set_published anna", "run_published pipeline"], log.TakeLast(3));

        // A second upload of the same agreement becomes version 2.
        var again = await Upload(anna);
        await DecideAll(anna, again);
        await anna.PostAsync($"/runs/{again}/approval", null);
        Assert.Equal([2, 1], (await Json(await anna.GetAsync("/agreements/lager/rule-sets"))).EnumerateArray().Select(version => version.GetProperty("version").GetInt32()));
    }

    [Fact]
    public async Task Nobody_gets_in_without_a_valid_token_for_a_specialist_of_some_company()
    {
        var tenant = NewTenant();
        async Task<HttpStatusCode> Get(string? token) => (await site.Client(token).GetAsync("/agreements/lager/rule-sets")).StatusCode;

        Assert.Equal(HttpStatusCode.OK, await Get(site.Token(tenant)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Get(null));
        Assert.Equal(HttpStatusCode.Unauthorized, await Get("not-a-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, await Get(site.Token(tenant, signedWith: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test" })));
        Assert.Equal(HttpStatusCode.Unauthorized, await Get(site.Token(tenant, validFor: TimeSpan.FromMinutes(-10))));
        Assert.Equal(HttpStatusCode.Unauthorized, await Get(site.Token(tenant, audience: "another-api")));
        Assert.Equal(HttpStatusCode.Forbidden, await Get(site.Token(tenant, roles: ["employer"])));
        Assert.Equal(HttpStatusCode.Forbidden, await Get(site.Token(tenant: "")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Client(null).PostAsync("/agreements/lager/runs", PdfBody())).StatusCode);
    }

    [Fact]
    public async Task One_company_cannot_see_or_decide_on_anothers_run()
    {
        var anna = site.As(NewTenant());
        var mallory = site.As(NewTenant());
        var run = await Upload(anna);

        Assert.Equal(HttpStatusCode.NotFound, (await mallory.GetAsync($"/runs/{run}")).StatusCode);
        Assert.Empty((await Json(await mallory.GetAsync("/runs"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Decide(mallory, run, "pension-2026", "rejected")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.PostAsync($"/runs/{run}/approval", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.PostAsync($"/runs/{run}/rejection", null)).StatusCode);

        await DecideAll(anna, run);
        await anna.PostAsync($"/runs/{run}/approval", null);
        Assert.Empty((await Json(await mallory.GetAsync("/agreements/lager/rule-sets"))).EnumerateArray());
        Assert.All((await Run(anna, run)).GetProperty("rules").EnumerateArray(), rule => Assert.Equal("anna", rule.GetProperty("decidedBy").GetString()));
    }

    [Fact]
    public async Task A_run_is_not_approved_until_every_rule_is_decided_and_a_flagged_rule_cannot_be_approved()
    {
        site.Spoil = (kind, rules) =>
        {
            if (kind == "pension")
            {
                rules[0]!["employer_percent"] = 99.5m;
            }
        };
        var anna = site.As(NewTenant());
        var run = await Upload(anna);
        site.Spoil = null;

        Assert.Equal(0, (await Run(anna, run)).GetProperty("gaps").GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, (await anna.PostAsync($"/runs/{run}/approval", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(anna, run, "pension-2026", "approved")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Decide(anna, run, "pension-2026", "maybe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Decide(anna, run, "no-such-rule", "approved")).StatusCode);

        // With one rule approved and the rest undecided, the run still cannot be approved, and stays open.
        Assert.Equal(HttpStatusCode.OK, (await Decide(anna, run, "holiday_allowance-2026", "approved")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await anna.PostAsync($"/runs/{run}/approval", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Decide(anna, run, "holiday_allowance-2026", "rejected")).StatusCode);

        // A rejection carries the reviewer's reason, and is kept as a case for the evals.
        Assert.Equal(HttpStatusCode.OK, (await anna.PostAsJsonAsync($"/runs/{run}/rules/pension-2026/decision", new { decision = "rejected", reason = "Arbejdsgiverens andel er 8,0 pct., ikke 99,5." })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anna.PostAsJsonAsync($"/runs/{run}/rules/pension-2027/decision", new { decision = "rejected", reason = new string('x', 501) })).StatusCode);
        var feedback = Assert.Single((await Json(await anna.GetAsync("/feedback"))).EnumerateArray(), entry => entry.GetProperty("reason").ValueKind == JsonValueKind.String);
        Assert.Equal(("Arbejdsgiverens andel er 8,0 pct., ikke 99,5.", "stand-in", "anna"), (feedback.GetProperty("reason").GetString(), feedback.GetProperty("model").GetString(), feedback.GetProperty("rejectedBy").GetString()));
        Assert.Equal(99.5m, feedback.GetProperty("proposed").GetProperty("employerPercent").GetDecimal());
        Assert.NotEmpty(feedback.GetProperty("clauses").EnumerateArray());
        Assert.Contains(feedback.GetProperty("clauses").EnumerateArray(), clause => clause.GetProperty("text").GetString()!.Contains("Pensionsbidraget", StringComparison.Ordinal));

        await DecideAll(anna, run);
        Assert.Equal(HttpStatusCode.Accepted, (await anna.PostAsync($"/runs/{run}/approval", null)).StatusCode);

        // Once approved, the review is closed.
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(anna, run, "overtime-2026", "rejected")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await anna.PostAsync($"/runs/{run}/rejection", null)).StatusCode);
        var set = Assert.Single((await Json(await anna.GetAsync("/agreements/lager/rule-sets"))).EnumerateArray());
        Assert.DoesNotContain("99.5", set.GetProperty("rules").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reviewer_is_told_which_kinds_of_rule_the_model_left_out()
    {
        site.Spoil = (kind, rules) =>
        {
            if (kind == "special_savings")
            {
                rules.Clear();
            }
        };
        var anna = site.As(NewTenant());
        var run = await Upload(anna);
        site.Spoil = null;

        var gap = Assert.Single((await Run(anna, run)).GetProperty("gaps").EnumerateArray());

        Assert.Equal(("special_savings", "no_rule_of_kind"), (gap.GetProperty("ruleId").GetString(), gap.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task Two_rules_that_conflict_cannot_both_be_approved_and_rejecting_one_settles_it()
    {
        site.Spoil = (kind, rules) =>
        {
            if (kind == "holiday_allowance")
            {
                // The model returns both figures of an agreement that contradicts itself.
                rules.Add(JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonNode>(rules[0]!.ToJsonString()));
            }
        };
        var anna = site.As(NewTenant());
        var run = await Upload(anna);
        site.Spoil = null;

        var conflict = Assert.Single((await Run(anna, run)).GetProperty("gaps").EnumerateArray());
        Assert.Equal("conflicting_rules", conflict.GetProperty("code").GetString());

        // Both approved: refused, and the review stays open.
        await DecideAll(anna, run);
        var refused = await anna.PostAsync($"/runs/{run}/approval", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("cannot be published together", (await Json(refused)).GetProperty("detail").GetString(), StringComparison.Ordinal);

        // One rejected: published, with one holiday rule.
        Assert.Equal(HttpStatusCode.OK, (await Decide(anna, run, "holiday_allowance-2026b", "rejected")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await anna.PostAsync($"/runs/{run}/approval", null)).StatusCode);
        var set = Assert.Single((await Json(await anna.GetAsync("/agreements/lager/rule-sets"))).EnumerateArray());
        Assert.Single(RuleJson.Read<List<Rule>>(set.GetProperty("rules").GetRawText()).OfType<HolidayAllowanceRule>());
    }

    [Fact]
    public async Task An_upload_is_kept_when_the_pipeline_cannot_be_reached_and_is_marked_for_the_worker_to_start()
    {
        site.PipelineDown = true;
        await using var cutOff = site.Create(uploadsPerHour: 1000);
        site.PipelineDown = false;
        var tenant = NewTenant();

        var response = await site.Client(site.Token(tenant), cutOff).PostAsync("/agreements/lager/runs", PdfBody());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        // Stored, and visibly not started: this is what the worker looks for.
        var run = await Run(site.Client(site.Token(tenant)), (await Json(response)).GetProperty("id").GetString()!);
        Assert.Equal("started", run.GetProperty("status").GetString());
        Assert.False(run.GetProperty("dispatched").GetBoolean());
        Assert.True((await Run(site.Client(site.Token(tenant)), await Upload(site.Client(site.Token(tenant))))).GetProperty("dispatched").GetBoolean());
    }

    [Fact]
    public async Task A_rejected_run_publishes_nothing()
    {
        var anna = site.As(NewTenant());
        var run = await Upload(anna);

        Assert.Equal(HttpStatusCode.Accepted, (await anna.PostAsync($"/runs/{run}/rejection", null)).StatusCode);

        Assert.Equal("rejected", (await Run(anna, run)).GetProperty("status").GetString());
        Assert.Empty((await Json(await anna.GetAsync("/agreements/lager/rule-sets"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await anna.PostAsync($"/runs/{run}/approval", null)).StatusCode);
    }

    [Fact]
    public async Task Only_a_pdf_of_a_sensible_size_under_a_sensible_name_is_taken()
    {
        var anna = site.As(NewTenant());
        async Task<HttpStatusCode> Post(string agreement, HttpContent body) => (await anna.PostAsync($"/agreements/{agreement}/runs", body)).StatusCode;

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await Post("lager", PdfBody(type: "text/plain")));
        Assert.Equal(HttpStatusCode.BadRequest, await Post("lager", PdfBody("<html>not a pdf</html>"u8.ToArray())));
        Assert.Equal(HttpStatusCode.BadRequest, await Post("Lager_2026!", PdfBody()));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Post("lager", PdfBody([.. "%PDF-"u8, .. new byte[Endpoints.MaxPdfBytes]])));

        // A PDF with no clauses in it is taken, and the run says what went wrong.
        var empty = Synthetic.AgreementPdf.Render(Site.Agreement.Document with { Sections = [], Protocols = [] });
        var response = await anna.PostAsync("/agreements/lager/runs", PdfBody(empty));
        var run = await Run(anna, (await Json(response)).GetProperty("id").GetString()!);
        Assert.Equal("failed", run.GetProperty("status").GetString());
        Assert.Contains("No numbered clauses", run.GetProperty("failure").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_company_cannot_use_up_the_pipeline_for_the_others()
    {
        await using var limited = site.Create(uploadsPerHour: 2);
        var busy = site.Client(site.Token(NewTenant()), limited);
        var other = site.Client(site.Token(NewTenant()), limited);

        Assert.Equal(HttpStatusCode.Accepted, (await busy.PostAsync("/agreements/lager/runs", PdfBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await busy.PostAsync("/agreements/lager/runs", PdfBody())).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await busy.PostAsync("/agreements/lager/runs", PdfBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await other.PostAsync("/agreements/lager/runs", PdfBody())).StatusCode);
    }

    private static async Task Publish(HttpClient specialist, byte[]? pdf = null)
    {
        var run = (await Json(await specialist.PostAsync("/agreements/lager/runs", PdfBody(pdf)))).GetProperty("id").GetString()!;
        await DecideAll(specialist, run);
        Assert.Equal(HttpStatusCode.Accepted, (await specialist.PostAsync($"/runs/{run}/approval", null)).StatusCode);
    }

    private static Task<HttpResponseMessage> CheckPay(HttpClient client, object body, string agreement = "lager") =>
        client.PostAsync($"/agreements/{agreement}/pay-checks", new StringContent(RuleJson.Write(body), System.Text.Encoding.UTF8, "application/json"));

    [Fact]
    public async Task An_employer_checks_payslips_and_gets_exactly_the_known_errors_each_with_its_clause()
    {
        var tenant = NewTenant();
        await Publish(site.Client(site.Token(tenant)));
        var employer = site.Client(site.Token(tenant, "eva", roles: [Caller.Employer]));
        var pay = Synthetic.Corpus.Pay(Synthetic.Corpus.Agreements[2], Site.Agreement);

        foreach (var payCase in pay.Cases.Take(30))
        {
            var answer = pay.Errors.SingleOrDefault(error => error.EmployeeId == payCase.Timesheet.Employee.Id && error.PeriodStart == payCase.Timesheet.PeriodStart);
            var response = await CheckPay(employer, payCase);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var findings = (await Json(response)).GetProperty("findings").EnumerateArray().ToList();

            Assert.Equal(
                (answer?.Lines ?? []).Select(line => (line.Line.ToString(), line.Expected, line.Actual)).Order(),
                findings.Where(finding => finding.GetProperty("line").ValueKind == JsonValueKind.String)
                    .Select(finding => (finding.GetProperty("line").GetString()!, finding.GetProperty("expected").GetDecimal(), finding.GetProperty("actual").GetDecimal())).Order());
            Assert.All(findings, finding =>
            {
                // No model key in the tests: the wording is the fixed one, and says so.
                Assert.False(finding.GetProperty("explainedByModel").GetBoolean());
                Assert.Contains("kr.", finding.GetProperty("explanation").GetString(), StringComparison.Ordinal);
            });
        }

        // Each check is logged with its size and who asked, and with nothing about the employee.
        var checks = (await Json(await site.Client(site.Token(tenant)).GetAsync("/audit"))).EnumerateArray().Where(entry => entry.GetProperty("event").GetString() == "pay_checked").ToList();
        Assert.Equal(30, checks.Count);
        Assert.All(checks, entry =>
        {
            Assert.Equal("eva", entry.GetProperty("actor").GetString());
            Assert.DoesNotContain("emp-", entry.GetRawText(), StringComparison.Ordinal);
        });

        // A line that is too low names the clause the rule came from.
        var wrong = pay.Cases.First(payCase => pay.Errors.Any(error => error.EmployeeId == payCase.Timesheet.Employee.Id && error.PeriodStart == payCase.Timesheet.PeriodStart && error.Mistake == "special_savings_left_out"));
        var missing = (await Json(await CheckPay(employer, wrong))).GetProperty("findings").EnumerateArray().Single();
        Assert.Equal("MissingLine", missing.GetProperty("kind").GetString());
        Assert.StartsWith("§ ", missing.GetProperty("clauses")[0].GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.Contains("Fritvalgskonto står ikke på lønsedlen", missing.GetProperty("explanation").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_a_renewal_each_payslip_is_checked_against_the_rules_of_its_own_date()
    {
        var tenant = NewTenant();
        var anna = site.Client(site.Token(tenant));
        await Publish(anna);
        var employee = new Employee("emp-1", new(2020, 1, 15));

        async Task<int> Findings(SyntheticAgreement paidUnder, DateOnly day)
        {
            var timesheet = new Timesheet(employee, new(day.Year, day.Month, 1), new(day.Year, day.Month, 28), [new(day, new(14, 0), new(23, 30))]);
            var lines = PayEngine.Expect(paidUnder.Truth, timesheet, 250m).Components
                .GroupBy(part => part.Line).Select(line => new PayLine(line.Key, line.Sum(part => part.Amount)));
            var payslip = new Payslip(employee.Id, timesheet.PeriodStart, timesheet.PeriodEnd, 250m, [.. lines]);
            var response = await CheckPay(anna, new { timesheet, payslip });
            Assert.Equal(2, (await Json(response)).GetProperty("version").GetInt32());
            return (await Json(response)).GetProperty("findings").GetArrayLength();
        }

        // While the renewal waited for review, the reviewer was shown what it would change: rates from March 2029 on, and nothing earlier.
        var renewalRun = (await Json(await anna.PostAsync("/agreements/lager/runs", PdfBody(Site.RenewalPdf)))).GetProperty("id").GetString()!;
        var wouldChange = (await Run(anna, renewalRun)).GetProperty("changes").EnumerateArray().ToList();
        Assert.NotEmpty(wouldChange);
        Assert.All(wouldChange, change => Assert.True(string.CompareOrdinal(change.GetProperty("from").GetString(), "2029-03-01") >= 0));
        Assert.Contains(wouldChange, change => change.GetProperty("kind").GetString() == "minimum_wage" && change.GetProperty("before").ValueKind == JsonValueKind.Object);
        Assert.DoesNotContain(wouldChange, change => change.GetProperty("kind").GetString() == "overtime");
        await DecideAll(anna, renewalRun);
        Assert.Equal(HttpStatusCode.Accepted, (await anna.PostAsync($"/runs/{renewalRun}/approval", null)).StatusCode);

        // Paid by the first agreement in 2027 and by the renewal in 2030: both right.
        Assert.Equal(0, await Findings(Site.Agreement, new(2027, 6, 8)));
        Assert.Equal(0, await Findings(Site.Renewal, new(2030, 6, 11)));
        // Paid by the old rates after the renewal took over: wrong.
        Assert.NotEqual(0, await Findings(Site.Agreement, new(2030, 6, 11)));
    }

    [Fact]
    public async Task Pay_is_checked_only_for_ones_own_company_by_a_specialist_or_an_employer_and_never_under_a_cpr_number()
    {
        var tenant = NewTenant();
        await Publish(site.Client(site.Token(tenant)));
        var payCase = Synthetic.Corpus.Pay(Synthetic.Corpus.Agreements[2], Site.Agreement).Cases[0];
        var employer = site.Client(site.Token(tenant, "eva", roles: [Caller.Employer]));

        Assert.Equal(HttpStatusCode.OK, (await CheckPay(employer, payCase)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CheckPay(site.Client(site.Token(tenant)), payCase)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CheckPay(site.Client(site.Token(tenant, roles: [])), payCase)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CheckPay(site.Client(null), payCase)).StatusCode);
        // Another company has no such agreement, and an employer cannot do a specialist's work.
        Assert.Equal(HttpStatusCode.NotFound, (await CheckPay(site.Client(site.Token(NewTenant(), roles: [Caller.Employer])), payCase)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CheckPay(employer, payCase, agreement: "no-such")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employer.PostAsync("/agreements/lager/runs", PdfBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employer.GetAsync("/agreements/lager/rule-sets")).StatusCode);

        async Task<HttpStatusCode> With(Func<Synthetic.PayCase, object> change) => (await CheckPay(employer, change(payCase))).StatusCode;
        var cpr = payCase.Timesheet.Employee with { Id = "010190-1234" };
        Assert.Equal(HttpStatusCode.BadRequest, await With(pay => pay with { Timesheet = pay.Timesheet with { Employee = cpr }, Payslip = pay.Payslip with { EmployeeId = cpr.Id } }));
        Assert.Equal(HttpStatusCode.BadRequest, await With(pay => pay with { Timesheet = pay.Timesheet with { Employee = cpr with { Id = "0101901234" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await With(pay => pay with { Payslip = pay.Payslip with { EmployeeId = "someone-else" } }));
        Assert.Equal(HttpStatusCode.BadRequest, await With(pay => pay with { Timesheet = pay.Timesheet with { PeriodEnd = pay.Timesheet.PeriodStart.AddDays(90) } }));
        Assert.Equal(HttpStatusCode.BadRequest, await With(_ => new { hello = "world" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await employer.PostAsync("/agreements/lager/pay-checks", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task A_question_is_answered_from_the_companys_own_published_agreement_or_not_at_all()
    {
        var holiday = Site.Agreement.Truth.Rules.OfType<HolidayAllowanceRule>().Single().Source;
        site.QuestionModel = new Calls(
            ("read_clause", new { clause_id = holiday.ClauseId }),
            ("give_answer", new { answered = true, text = "Feriegodtgørelsen udgør 12,5 pct. af lønnen.", citations = new[] { new { clause_id = holiday.ClauseId, quote = holiday.Quote } } }));
        await using var asking = site.Create(uploadsPerHour: 1000);
        site.QuestionModel = null;
        var tenant = NewTenant();
        await Publish(site.Client(site.Token(tenant), asking));
        var employer = site.Client(site.Token(tenant, "eva", roles: [Caller.Employer]), asking);
        Task<HttpResponseMessage> Ask(HttpClient client, string question) => client.PostAsJsonAsync("/agreements/lager/questions", new { question });

        var answer = await Json(await Ask(employer, "Hvad er feriegodtgørelsen?"));

        Assert.Equal(("Answered", "Feriegodtgørelsen udgør 12,5 pct. af lønnen."), (answer.GetProperty("outcome").GetString(), answer.GetProperty("text").GetString()));
        Assert.Equal(holiday.ClauseId, answer.GetProperty("citations")[0].GetProperty("clauseId").GetString());
        Assert.Equal(2, answer.GetProperty("steps").GetArrayLength());

        Assert.Equal(HttpStatusCode.NotFound, (await Ask(site.Client(site.Token(NewTenant()), asking), "Hvad er feriegodtgørelsen?")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(employer, new string('a', 501))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Ask(site.Client(site.Token(tenant, roles: []), asking), "Hvad?")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Ask(site.Client(null, asking), "Hvad?")).StatusCode);
        // With no model configured, questions are refused, not guessed at.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Ask(site.Client(site.Token(tenant, "eva", roles: [Caller.Employer])), "Hvad er feriegodtgørelsen?")).StatusCode);
    }

    /// <summary>A model that makes the tool calls it was given, one per turn.</summary>
    private sealed class Calls(params (string Name, object Arguments)[] turns) : Microsoft.Extensions.AI.IChatClient
    {
        private int turn;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var (name, arguments) = turns[Math.Min(turn++, turns.Length - 1)];
            var call = new Microsoft.Extensions.AI.FunctionCallContent(Guid.NewGuid().ToString("N"), name, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments)));
            return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, [call])));
        }

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task The_console_is_served_with_a_policy_that_lets_it_load_only_its_own_code()
    {
        var page = await site.Client(null).GetAsync("/app/");
        var policy = string.Join(' ', page.Headers.GetValues("Content-Security-Policy"));

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<script src=\"app.js\"", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self';", policy, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self' https://identity.test;", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe", policy, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await site.Client(null).GetAsync("/app/app.js")).StatusCode);
        Assert.Equal("overens-web", (await Json(await site.Client(null).GetAsync("/app/config.json"))).GetProperty("clientId").GetString());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task The_api_answers_health_checks_without_a_token(string path)
    {
        Assert.Equal(HttpStatusCode.OK, (await site.Client(null).GetAsync(path)).StatusCode);
    }
}
