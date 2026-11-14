using System.Text.Json;
using Microsoft.Playwright;

// Uses the console as its people would, in the Chrome already on the machine:
//
//   dotnet run --project tools/UiCheck -- [screenshot directory]
//
// The system must be running (`dotnet run --project src/AppHost`). It fails on any script
// or console error, so a page that breaks its own content policy fails here.
const string Console_ = "http://localhost:5058/app/";
const string Password = "local-demo-password";
var shots = args.Length > 0 ? args[0] : null;
var problems = new List<string>();

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "chrome", Headless = true });

async Task As(string user, Func<IPage, Task> work)
{
    // Fixed, so that a screenshot shows nothing about the machine it was made on.
    await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1180, Height = 860 }, TimezoneId = "UTC", Locale = "en-US" });
    var page = await context.NewPageAsync();
    page.PageError += (_, error) => problems.Add($"{user}: script error: {error}");
    page.Console += (_, message) =>
    {
        if (message.Type == "error")
        {
            problems.Add($"{user}: console error: {message.Text}");
        }
    };
    await page.GotoAsync(Console_);
    await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
    await page.Locator("input[name=username]").FillAsync(user);
    await page.Locator("input[name=password]").FillAsync(Password);
    await page.Locator("[type=submit]").First.ClickAsync();
    try
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).WaitForAsync();
        await work(page);
    }
    catch (Exception)
    {
        // What the person would have been looking at when it went wrong.
        await Shot(page, $"failed-{user}");
        throw;
    }
}

async Task Shot(IPage page, string name)
{
    if (shots is not null)
    {
        Directory.CreateDirectory(shots);
        await page.ScreenshotAsync(new() { Path = Path.Combine(shots, $"{name}.png") });
    }
}

void Ok(string what) => Console.WriteLine($"  ok    {what}");

async Task Expect(bool condition, string what)
{
    await Task.CompletedTask;
    if (condition)
    {
        Ok(what);
    }
    else
    {
        problems.Add(what);
    }
}

// A specialist uploads an agreement, reads each proposed rule beside its clause, and publishes.
await As("anna", async page =>
{
    await page.GetByLabel("Agreement id").FillAsync("konsol");
    await page.GetByLabel("Agreement PDF").SetInputFilesAsync(Path.Combine("data", "synthetic", "hard-2026", "agreement.pdf"));
    await page.GetByRole(AriaRole.Button, new() { Name = "Upload" }).ClickAsync();
    await page.GetByText("Waiting for review").WaitForAsync(new() { Timeout = 60_000 });
    await Expect(await page.Locator(".rule").CountAsync() == 13, "a specialist sees the 13 proposed rules");

    var overtime = page.Locator(".rule[data-rule='overtime-2026']");
    var marked = await overtime.Locator("mark").InnerTextAsync();
    await Expect(marked.Contains("overarbejde pr. dag betales et tillæg på 50,0 pct.", StringComparison.Ordinal), "the quoted sentence is marked in its clause");
    await Expect((await overtime.Locator(".clause").AllInnerTextsAsync()).Any(text => text.Contains("7 timer og 24 minutter", StringComparison.Ordinal)), "the clause a rule refers to is shown beside it");
    await Expect(await page.GetByRole(AriaRole.Button, new() { Name = "Approve and publish" }).IsDisabledAsync(), "the run cannot be published before every rule is decided");
    await Shot(page, "1-review");

    for (var rule = 0; rule < 13; rule++)
    {
        await page.Locator(".rule").Nth(rule).GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).ClickAsync();
        await page.Locator(".rule").Nth(rule).GetByText("Approved", new() { Exact = true }).WaitForAsync();
    }

    await page.GetByRole(AriaRole.Button, new() { Name = "Approve and publish" }).ClickAsync();
    await page.GetByText("Published", new() { Exact = true }).WaitForAsync(new() { Timeout = 60_000 });
    Ok("a specialist approves each rule and publishes the run");
    await Shot(page, "2-published");
});

// An employer checks a wrong payslip and a correct one. They cannot reach agreements.
await As("eva", async page =>
{
    await Expect(await page.GetByRole(AriaRole.Button, new() { Name = "Agreements" }).CountAsync() == 0, "an employer is not offered agreements");
    var cases = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine("data", "synthetic", "hard-2026", "pay.json")));
    var answers = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine("data", "synthetic", "hard-2026", "pay-answers.json")));
    bool IsWrong(JsonElement pay) => answers.EnumerateArray().Any(answer =>
        answer.GetProperty("employeeId").GetString() == pay.GetProperty("timesheet").GetProperty("employee").GetProperty("id").GetString()
        && answer.GetProperty("periodStart").GetString() == pay.GetProperty("timesheet").GetProperty("periodStart").GetString());

    await page.GetByLabel("Agreement id").FillAsync("konsol");
    await page.GetByLabel("Timesheet and payslip").FillAsync(cases.EnumerateArray().First(IsWrong).GetRawText());
    await page.GetByRole(AriaRole.Button, new() { Name = "Check", Exact = true }).ClickAsync();
    await page.GetByText("to correct").WaitForAsync();
    await Expect((await page.Locator(".finding").First.InnerTextAsync()).Contains("kr.", StringComparison.Ordinal), "an employer sees what is wrong with a payslip, in words, with the clause");
    await Shot(page, "3-payslip");

    await page.GetByLabel("Timesheet and payslip").FillAsync(cases.EnumerateArray().First(pay => !IsWrong(pay)).GetRawText());
    await page.GetByRole(AriaRole.Button, new() { Name = "Check", Exact = true }).ClickAsync();
    await page.GetByText("Nothing to correct").WaitForAsync();
    Ok("a correct payslip has nothing to correct");

    await page.GetByLabel("Timesheet and payslip").FillAsync("<img src=x onerror=alert(1)>");
    await page.GetByRole(AriaRole.Button, new() { Name = "Check", Exact = true }).ClickAsync();
    await page.GetByText("That is not JSON.").WaitForAsync();
    Ok("what is typed is never run as markup");
});

// A signed-in person with no role gets nothing.
await As("erik", async page =>
{
    await page.GetByText("No access").WaitForAsync();
    Ok("a person with no role is shown nothing");
});

foreach (var problem in problems)
{
    Console.WriteLine($"  FAIL  {problem}");
}

return problems.Count == 0 ? 0 : 1;
