using System.Text;

namespace AxonHub.Llm.Json;

/// <summary>
/// Port of the <c>sjson.SetBytes(body, key, value)</c> call the orchestrator's
/// decisions prompt injection makes (internal/server/orchestrator/prompt.go),
/// exposed for the API layer. Like sjson it splices the original bytes: the
/// first occurrence of <paramref name="key"/> keeps every other byte of the
/// document, and a missing key is appended just before the closing brace.
/// </summary>
/// <remarks>
/// <paramref name="body"/> may be a whole JSON document or a nested object
/// slice; the reference uses the latter to patch a single content part.
/// </remarks>
public static class RawJsonStringSet
{
    /// <summary>Sets a string value at a top-level object key.</summary>
    public static byte[] SetKey(ReadOnlySpan<byte> body, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        return RawJsonPatch.SetTopLevelRaw(body, Encoding.UTF8.GetBytes(key), GoJson.Marshal(value));
    }
}
