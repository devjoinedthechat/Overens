using Microsoft.Extensions.AI;
using Overens.Gateway;

namespace Overens.Evals;

/// <summary>What every command of the eval runner is given: its arguments, the model, and how to reach it.</summary>
/// <param name="Args">The command line.</param>
/// <param name="ModelId">The model whose answers are replayed or recorded.</param>
/// <param name="Only">One agreement's id, or empty for all.</param>
/// <param name="Record">Whether answers not yet recorded may be asked of the model.</param>
/// <param name="Live">The model itself when recording, or null to replay only.</param>
/// <param name="Spend">What recording has cost.</param>
/// <param name="Recordings">Where the model's extraction answers are kept.</param>
internal sealed record Run(string[] Args, string ModelId, string Only, bool Record, IChatClient? Live, Spend Spend, string Recordings)
{
    public string Option(string name, string fallback) => Array.IndexOf(Args, name) is >= 0 and var at && at + 1 < Args.Length ? Args[at + 1] : fallback;
}
