using Overens.Ingestion.Extraction;
using Overens.Rules;

namespace Overens.Evals.Tests;

/// <summary>
/// The hand-written agreements' answer keys were written by the agreements' own authors. Each
/// was checked by a second reader who saw the agreement and nothing else, and wrote down the
/// rules it states: <c>audit.json</c> beside each key, with the reader's notes on what was
/// unclear in <c>audit-notes.md</c>. A key that changes must still say what the second reader
/// read, or the change must be argued in those notes.
/// </summary>
public sealed class AnswerKeyAuditTests
{
    public static TheoryData<string, string> Agreements
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var set in Independent.Sets)
            {
                foreach (var id in Independent.Ids(set, Root()))
                {
                    data.Add(set, id);
                }
            }

            return data;
        }
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Overens.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [Theory]
    [MemberData(nameof(Agreements))]
    public void The_key_says_on_every_day_what_a_second_reader_read(string set, string id)
    {
        var directory = Path.Combine(Root(), "data", set, id);
        var key = Independent.Load(set, id, Root()).Truth.Rules;
        var audit = RuleJson.Read<RuleSet>(File.ReadAllText(Path.Combine(directory, "audit.json"))).Rules;

        Assert.Empty(SecondReading.Compare(key, audit));
        Assert.Empty(SecondReading.Compare(audit, key));
        Assert.True(new FileInfo(Path.Combine(directory, "audit-notes.md")).Length > 200, "The second reader's notes are missing.");
    }

    [Fact]
    public void Every_hand_written_agreement_has_been_audited()
    {
        Assert.Equal(8, Agreements.Count);
    }
}
