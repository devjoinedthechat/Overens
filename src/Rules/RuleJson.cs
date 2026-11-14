using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Overens.Rules;

/// <summary>
/// Rule sets and pay data as JSON. Each rule carries a <c>kind</c>; amounts are numbers with
/// their decimals kept; dates and times are ISO text.
/// </summary>
public static class RuleJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        // Danish letters and the section sign stay readable in the files.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("The JSON holds nothing.");
}
