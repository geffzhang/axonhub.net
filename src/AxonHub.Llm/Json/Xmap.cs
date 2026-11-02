using System.Text.Json;

namespace AxonHub.Llm.Json;

/// <summary>
/// Port of llm/internal/pkg/xmap.GetPtr (xmap.go:120-137): look a key up in a
/// metadata map and return the stored value as T, or null when the map, the
/// key, or the value's shape do not match.
/// </summary>
/// <remarks>
/// The reference stores typed pointers in <c>map[string]any</c>; the port's
/// unified models store <see cref="JsonElement"/> instead, so the type
/// assertion becomes a deserialize attempt that fails closed.
/// </remarks>
public static class Xmap
{
    /// <summary>Port of xmap.GetPtr[T].</summary>
    public static T? GetPtr<T>(Dictionary<string, JsonElement>? m, string key)
    {
        if (m is null || !m.TryGetValue(key, out var value))
        {
            return default;
        }

        try
        {
            return LlmJson.Deserialize<T>(value.GetRawText());
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
