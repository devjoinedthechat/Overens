using System.Text.RegularExpressions;

namespace Overens.Data;

/// <summary>Turns <c>DocumentSha256</c> into <c>document_sha256</c>, as the SQL schema names its columns.</summary>
internal static partial class SnakeCase
{
    public static string Of(string name) => Boundary().Replace(name, "$1_$2").ToLowerInvariant();

    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex Boundary();
}
