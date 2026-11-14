namespace Overens.Synthetic.Tests;

public sealed class CorpusTests
{
    /// <summary>
    /// The corpus in the repository is the generator's output and nothing else. After a change
    /// to a generator, write it again: <c>dotnet run --project tools/Corpus</c>.
    /// </summary>
    [Fact]
    public void The_files_in_the_repository_are_exactly_what_the_generator_writes()
    {
        var root = Path.Combine(RepositoryRoot(), "data", "synthetic");
        var generated = Corpus.Files();

        var onDisk = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

        Assert.Equal(generated.Keys.Concat(Corpus.Pdfs().Keys).Order(StringComparer.Ordinal), onDisk);
        Assert.All(generated, file => Assert.Equal(file.Value, File.ReadAllText(Path.Combine(root, file.Key))));
    }

    /// <summary>PDF bytes differ from run to run, so the committed PDFs are compared by what they say.</summary>
    [Fact]
    public void The_pdfs_in_the_repository_say_what_the_generator_renders()
    {
        var root = Path.Combine(RepositoryRoot(), "data", "synthetic");

        Assert.All(Corpus.Pdfs(), pdf =>
            Assert.Equal(PdfText.Pages(pdf.Value), PdfText.Pages(File.ReadAllBytes(Path.Combine(root, pdf.Key)))));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Overens.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
