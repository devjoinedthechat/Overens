using Microsoft.Extensions.AI;
using Overens.Agent;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Evals;

/// <summary>How a model's wordings of findings fared against the guard.</summary>
/// <param name="Findings">Findings explained.</param>
/// <param name="Shown">Wordings that passed the guard and would be shown.</param>
/// <param name="Replaced">The wordings the guard refused, as the model gave them.</param>
public sealed record ExplanationOutcome(int Findings, int Shown, IReadOnlyList<string> Replaced);

public static class Explanations
{
    /// <summary>Explains every finding in the hardest corpus agreement's pay data, using its true rules.</summary>
    public static async Task<ExplanationOutcome> RunAsync(IChatClient model, string modelId)
    {
        var spec = Corpus.Agreements[2];
        var agreement = Corpus.Generate(spec);
        using var listening = new Listening(model);
        var explainer = new FindingExplainer(listening, modelId);
        var (findings, shown) = (0, 0);
        var replaced = new List<string>();
        foreach (var (timesheet, payslip) in Corpus.Pay(spec, agreement).Cases)
        {
            var check = PayEngine.Check(agreement.Truth, timesheet, payslip);
            foreach (var finding in check.Findings)
            {
                findings++;
                var explanation = await explainer.ExplainAsync(finding, check.Expected);
                if (explanation.ByModel)
                {
                    shown++;
                }
                else
                {
                    replaced.Add($"[{explanation.Refusal ?? "no answer"}] {finding.Kind} {finding.Line}: {listening.Last}");
                }
            }
        }

        return new(findings, shown, replaced);
    }

    /// <summary>Keeps the model's last answer, so a refused wording can be reported.</summary>
    private sealed class Listening(IChatClient inner) : DelegatingChatClient(inner)
    {
        public string? Last { get; private set; }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Last = null;
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            Last = response.Text;
            return response;
        }
    }
}
