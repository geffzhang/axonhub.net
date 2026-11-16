using System.Reflection;
using System.Text.Json.Nodes;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Compares a store round-trip against the record that was written. Every property
/// must match exactly, except JSON columns: PostgreSQL renders jsonb back with a
/// space after ':' and ',' and orders keys by length then bytes, discarding the
/// original text formatting. Ent reads that same normalized text in the reference,
/// so JSON is compared semantically while all other columns stay exact.
/// </summary>
internal static class RecordAssert
{
    public static void Equivalent<T>(T expected, T actual)
        where T : class
    {
        Assert.NotNull(actual);

        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var expectedValue = property.GetValue(expected);
            var actualValue = property.GetValue(actual);

            if (expectedValue is string expectedJson
                && actualValue is string actualJson
                && (LooksLikeJsonObjectOrArray(expectedJson) || LooksLikeJsonObjectOrArray(actualJson)))
            {
                Assert.True(
                    JsonNode.DeepEquals(JsonNode.Parse(expectedJson), JsonNode.Parse(actualJson)),
                    $"{property.Name} differs: expected {expectedJson}, actual {actualJson}");
            }
            else
            {
                Assert.Equal(expectedValue, actualValue);
            }
        }
    }

    /// <summary>Asserts two JSON documents carry the same values, ignoring formatting.</summary>
    public static void Json(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            $"JSON differs: expected {expected}, actual {actual}");
    }

    private static bool LooksLikeJsonObjectOrArray(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }
}
