using Overens.Synthetic;

// Writes the corpus to data/synthetic, or to the directory given as the first argument.
var root = args.Length > 0 ? args[0] : Path.Combine("data", "synthetic");
foreach (var (path, content) in Corpus.Files())
{
    var file = Path.Combine(root, path);
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    await File.WriteAllTextAsync(file, content);
    Console.WriteLine(file);
}

foreach (var (path, content) in Corpus.Pdfs())
{
    var file = Path.Combine(root, path);
    await File.WriteAllBytesAsync(file, content);
    Console.WriteLine(file);
}
