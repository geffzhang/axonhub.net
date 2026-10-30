namespace AxonHub.Llm.Json;

/// <summary>
/// Reproduces the two root-value behaviours of Go's <c>json.Unmarshal</c> into a
/// struct: a JSON <c>null</c> leaves the zero value in place (no error), and any
/// non-object root yields <c>json: cannot unmarshal &lt;kind&gt; into Go value of
/// type &lt;T&gt;</c>.
/// </summary>
public static class GoStructJson
{
    /// <summary>
    /// Classifies the root token of an already syntax-validated document.
    /// Returns Go's kind word, <c>"null"</c> for a null literal, or
    /// <see langword="null"/> when the root is an object.
    /// </summary>
    public static string? RootKind(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            switch (b)
            {
                case (byte)' ':
                case (byte)'\t':
                case (byte)'\r':
                case (byte)'\n':
                    continue;
                case (byte)'{':
                    return null;
                case (byte)'n':
                    return "null";
                case (byte)'[':
                    return "array";
                case (byte)'"':
                    return "string";
                case (byte)'t':
                case (byte)'f':
                    return "bool";
                default:
                    return "number";
            }
        }

        return null;
    }

    /// <summary>Go's wording for a root-type mismatch.</summary>
    public static string TypeMismatch(string kind, string goType)
        => "json: cannot unmarshal " + kind + " into Go value of type " + goType;
}
