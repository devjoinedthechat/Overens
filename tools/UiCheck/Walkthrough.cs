using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

/// <summary>
/// Makes the walkthrough in the README: one animated SVG, built from stills of the console
/// in a real browser against the running system.
///
///   dotnet run --project tools/UiCheck -- --walkthrough docs/walkthrough/overens-walkthrough.svg
///
/// Start the system first on fresh data, with ANTHROPIC_API_KEY set so that the question is
/// answered by a model. Each still is the browser window scrolled to what its caption is about.
/// </summary>
internal static class Walkthrough
{
    private const string Console_ = "http://localhost:5058/app/";
    private const int Width = 1000;
    private const int Height = 600;
    private const int Hold = 6;

    public static async Task Record(string output)
    {
        var stills = new List<(string Caption, byte[] Png)>();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "chrome", Headless = true });

        async Task As(string user, Func<IPage, Task> work)
        {
            // Fixed, so that a still shows nothing about the machine it was made on.
            await using var context = await browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = Width, Height = Height },
                DeviceScaleFactor = 2,
                TimezoneId = "UTC",
                Locale = "en-US",
            });
            var page = await context.NewPageAsync();
            await page.GotoAsync(Console_);
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
            await page.Locator("input[name=username]").FillAsync(user);
            await page.Locator("input[name=password]").FillAsync("local-demo-password");
            await page.Locator("[type=submit]").First.ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).WaitForAsync();
            await work(page);
        }

        async Task Still(IPage page, ILocator target, string caption)
        {
            // Room below the page, so that a short page can still be scrolled to its target.
            await page.EvaluateAsync($"document.body.style.minHeight = '{Height * 4}px'");
            var box = await target.BoundingBoxAsync() ?? throw new InvalidOperationException($"Nothing to show for: {caption}");
            await page.EvaluateAsync($"window.scrollBy(0, {(box.Y - 24).ToString(CultureInfo.InvariantCulture)})");
            await page.WaitForTimeoutAsync(300);
            stills.Add((caption, await page.ScreenshotAsync(new() { Type = ScreenshotType.Png })));
            System.Console.WriteLine($"still {stills.Count}: {caption}");
        }

        await As("anna", async page =>
        {
            await page.GetByLabel("Agreement id").FillAsync("lager");
            await page.GetByLabel("Agreement PDF").SetInputFilesAsync(Path.Combine("data", "synthetic", "hard-2026", "agreement.pdf"));
            await Still(page, page.Locator("main"), "A payroll specialist uploads a collective agreement as a PDF.");
            await page.GetByRole(AriaRole.Button, new() { Name = "Upload" }).ClickAsync();
            await page.GetByText("Waiting for review").WaitForAsync(new() { Timeout = 90_000 });
            await Still(page, page.Locator("main"), "The pipeline read it and proposed 13 rules. Nothing is published yet.");
            await Still(page, page.Locator(".rule[data-rule='overtime-2026']"), "Each rule stands beside the clause it was read from, with the quoted sentence marked.");

            for (var rule = 0; rule < 13; rule++)
            {
                await page.Locator(".rule").Nth(rule).GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).ClickAsync();
                await page.Locator(".rule").Nth(rule).GetByText("Approved", new() { Exact = true }).WaitForAsync();
            }

            await page.GetByRole(AriaRole.Button, new() { Name = "Approve and publish" }).ClickAsync();
            await page.GetByText("Published", new() { Exact = true }).WaitForAsync(new() { Timeout = 90_000 });
            await Still(page, page.Locator("main"), "A person approved every rule. Only then will the database publish them.");
        });

        await As("eva", async page =>
        {
            var cases = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine("data", "synthetic", "hard-2026", "pay.json")));
            var answers = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine("data", "synthetic", "hard-2026", "pay-answers.json")));
            var wrong = answers.EnumerateArray().First(answer => answer.GetProperty("mistake").GetString() == "no_inconvenient_hours_supplement");
            var pay = cases.EnumerateArray().First(pay =>
                pay.GetProperty("timesheet").GetProperty("employee").GetProperty("id").GetString() == wrong.GetProperty("employeeId").GetString()
                && pay.GetProperty("timesheet").GetProperty("periodStart").GetString() == wrong.GetProperty("periodStart").GetString());

            await page.GetByLabel("Agreement id").FillAsync("lager");
            await page.GetByLabel("Timesheet and payslip").FillAsync(pay.GetRawText());
            await page.GetByRole(AriaRole.Button, new() { Name = "Check", Exact = true }).ClickAsync();
            await page.GetByText("to correct").WaitForAsync(new() { Timeout = 60_000 });
            await Still(page, page.GetByRole(AriaRole.Heading, new() { Name = "Result" }), "An employer checks a payslip. The finding is arithmetic, and it names the clause.");

            await page.GetByRole(AriaRole.Button, new() { Name = "Ask" }).ClickAsync();
            await page.GetByLabel("Agreement id").FillAsync("lager");
            await page.GetByLabel("Question").FillAsync("Hvad er mindstebetalingen pr. time den 15. april 2027 for en medarbejder, der lige er blevet ansat?");
            await page.GetByRole(AriaRole.Button, new() { Name = "Ask", Exact = true }).Last.ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Answer" }).WaitForAsync(new() { Timeout = 120_000 });
            await Still(page, page.GetByLabel("Question"), "A question is answered from the agreement, with the clause quoted, or not at all.");
        });

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, Svg(stills));
        System.Console.WriteLine($"wrote {output}: {stills.Count} stills, {new FileInfo(output).Length / 1024} KB");
    }

    /// <summary>The stills as one SVG that fades from each to the next, with a caption and a progress bar.</summary>
    private static string Svg(List<(string Caption, byte[] Png)> stills)
    {
        const int Caption = 60;
        const double Fade = 0.5;
        var total = stills.Count * Hold;
        string Percent(double seconds) => (seconds / total * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";
        var tick = (Width - 48 - (6.0 * (stills.Count - 1))) / stills.Count;
        string At(int index) => (24 + (index * (tick + 6))).ToString("0.##", CultureInfo.InvariantCulture);

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {Width} {Height + Caption}\" width=\"{Width}\" role=\"img\" aria-label=\"{SecurityElement.Escape(string.Join(' ', stills.Select(still => still.Caption)))}\">\n");
        svg.Append(CultureInfo.InvariantCulture, $"<style>\n.s{{opacity:0;animation:show {total}s linear infinite both}}\n.c{{font:500 16px/1 system-ui,-apple-system,\"Segoe UI\",Helvetica,Arial,sans-serif;fill:#fff}}\n@keyframes show{{0%{{opacity:0}}{Percent(Fade)}{{opacity:1}}{Percent(Hold)}{{opacity:1}}{Percent(Hold + Fade)}{{opacity:0}}100%{{opacity:0}}}}\n</style>\n");
        svg.Append(CultureInfo.InvariantCulture, $"<clipPath id=\"frame\"><rect width=\"{Width}\" height=\"{Height + Caption}\" rx=\"10\"/></clipPath>\n<g clip-path=\"url(#frame)\">\n<rect width=\"{Width}\" height=\"{Height}\" fill=\"#fff\"/>\n");
        for (var index = 0; index < stills.Count; index++)
        {
            svg.Append(CultureInfo.InvariantCulture, $"<image class=\"s\" style=\"animation-delay:{index * Hold}s\" width=\"{Width}\" height=\"{Height}\" href=\"data:image/png;base64,{Convert.ToBase64String(stills[index].Png)}\"/>\n");
        }

        svg.Append(CultureInfo.InvariantCulture, $"<rect y=\"{Height}\" width=\"{Width}\" height=\"{Caption}\" fill=\"#101216\"/>\n");
        for (var index = 0; index < stills.Count; index++)
        {
            var bar = $"x=\"{At(index)}\" y=\"{Height + 12}\" width=\"{tick.ToString("0.##", CultureInfo.InvariantCulture)}\" height=\"3\" rx=\"1.5\" fill=\"#fff\"";
            svg.Append(CultureInfo.InvariantCulture, $"<rect {bar} opacity=\".22\"/><rect class=\"s\" style=\"animation-delay:{index * Hold}s\" {bar}/>\n");
            svg.Append(CultureInfo.InvariantCulture, $"<text class=\"s c\" style=\"animation-delay:{index * Hold}s\" x=\"24\" y=\"{Height + 41}\">{SecurityElement.Escape(stills[index].Caption)}</text>\n");
        }

        svg.Append(CultureInfo.InvariantCulture, $"</g>\n<rect x=\".5\" y=\".5\" width=\"{Width - 1}\" height=\"{Height + Caption - 1}\" rx=\"10\" fill=\"none\" stroke=\"#8b949e\" stroke-opacity=\".4\"/>\n</svg>\n");
        return svg.ToString();
    }
}
