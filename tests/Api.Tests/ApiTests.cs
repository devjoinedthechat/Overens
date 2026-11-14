using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Overens.Rules;

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

    [Theory]
    [InlineData("/")]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task The_api_answers_health_checks_without_a_token(string path)
    {
        Assert.Equal(HttpStatusCode.OK, (await site.Client(null).GetAsync(path)).StatusCode);
    }
}
