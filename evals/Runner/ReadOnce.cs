using System.Collections.Concurrent;
using Overens.Documents;
using Overens.Ingestion.Reading;
using Overens.Synthetic;

namespace Overens.Evals;

/// <summary>
/// Each agreement printed as a PDF and read back, once per process. Every eval starts from
/// the PDF, and most run over the same agreements many times: the planted mistakes, the
/// repeat passes, the second reading. What is read is the same each time, and never changed.
/// </summary>
public static class ReadOnce
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<ReadAgreement>>> Read = new(StringComparer.Ordinal);

    /// <summary>The agreement as the system reads it from its PDF. The key is its id and the text it was printed from.</summary>
    public static Task<ReadAgreement> Agreement(AgreementDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        // The document itself is part of the key, so two documents with one id are never mistaken for each other.
        var key = $"{document.Id}\n{AgreementText.Render(document)}";
        return Read.GetOrAdd(key, _ => new(() => ReadAsync(document))).Value;
    }

    private static async Task<ReadAgreement> ReadAsync(AgreementDocument document) =>
        ClauseSegmenter.Segment(await new PdfLayoutReader().ReadAsync(AgreementPdf.Render(document)));
}
