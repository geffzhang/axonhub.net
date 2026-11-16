using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;
using GoQuote = AxonHub.Api.Orchestration.GoJson;

namespace AxonHub.Api.Catalog;

/// <summary>
/// The catalog document decoded the way Go's <c>encoding/json</c> decodes it
/// into <c>biz.catalogFile</c>: the top-level object has one
/// <c>providers</c> map whose values are the provider struct, and each
/// provider's <c>models</c> / <c>metadata</c> hold arbitrary JSON decoded into
/// <c>any</c> (objects become maps with last-duplicate-key-wins, numbers become
/// float64).
/// </summary>
internal sealed class CatalogFile
{
    public Dictionary<string, CatalogProvider>? Providers { get; set; }

    /// <summary>Go <c>json.Marshal(catalogFile)</c>: provider keys sorted.</summary>
    public void WriteJson(StringBuilder builder)
    {
        builder.Append("{\"providers\":");
        if (Providers is null)
        {
            builder.Append("null");
        }
        else
        {
            builder.Append('{');
            var first = true;
            foreach (var key in Providers.Keys.OrderBy(static key => key, StringComparer.Ordinal))
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append(GoQuote.Quote(key)).Append(':');
                Providers[key].WriteJson(builder);
            }

            builder.Append('}');
        }

        builder.Append('}');
    }

    public byte[] ToUtf8Bytes()
    {
        var builder = new StringBuilder();
        WriteJson(builder);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}

/// <summary>
/// Port of <c>biz.catalogProvider</c> (catalog_filter.go:42-51). Struct field
/// order and <c>omitempty</c> tags drive the marshalled bytes.
/// </summary>
internal sealed class CatalogProvider
{
    public string Id { get; set; } = string.Empty;

    public string Api { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Doc { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool? Vision { get; set; }

    public List<CatalogValue>? Models { get; set; }

    public Dictionary<string, CatalogValue>? Metadata { get; set; }

    public CatalogProvider Clone()
    {
        var clone = new CatalogProvider
        {
            Id = Id,
            Api = Api,
            Name = Name,
            Doc = Doc,
            DisplayName = DisplayName,
            Vision = Vision,
        };

        if (Models is not null)
        {
            clone.Models = [.. Models.Select(static model => model.Clone())];
        }

        if (Metadata is not null)
        {
            clone.Metadata = new Dictionary<string, CatalogValue>(StringComparer.Ordinal);
            foreach (var (key, value) in Metadata)
            {
                clone.Metadata[key] = value.Clone();
            }
        }

        return clone;
    }

    public void WriteJson(StringBuilder builder)
    {
        builder.Append('{');
        var first = true;

        void Field(string name, string value)
        {
            if (value.Length == 0)
            {
                return;
            }

            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append(GoQuote.Quote(name)).Append(':').Append(GoQuote.Quote(value));
        }

        Field("id", Id);
        Field("api", Api);
        Field("name", Name);
        Field("doc", Doc);
        Field("display_name", DisplayName);

        if (Vision is { } vision)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append("\"vision\":").Append(vision ? "true" : "false");
        }

        if (Models is { Count: > 0 })
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append("\"models\":[");
            for (var index = 0; index < Models.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                Models[index].WriteJson(builder);
            }

            builder.Append(']');
        }

        if (Metadata is { Count: > 0 })
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append("\"metadata\":{");
            var firstMember = true;
            foreach (var key in Metadata.Keys.OrderBy(static key => key, StringComparer.Ordinal))
            {
                if (!firstMember)
                {
                    builder.Append(',');
                }

                firstMember = false;
                builder.Append(GoQuote.Quote(key)).Append(':');
                Metadata[key].WriteJson(builder);
            }

            builder.Append('}');
        }

        builder.Append('}');
    }
}

/// <summary>
/// A Go <c>any</c> decoded from JSON: objects are maps (keys sorted when
/// marshalled, duplicate keys keep the last value), arrays keep their order,
/// numbers are float64 and strings use encoding/json's HTML-safe escaping.
/// </summary>
internal abstract class CatalogValue
{
    public abstract void WriteJson(StringBuilder builder);

    public abstract CatalogValue Clone();

    public string ToJson()
    {
        var builder = new StringBuilder();
        WriteJson(builder);
        return builder.ToString();
    }

    public static CatalogValue FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => CatalogObject.FromElement(element),
        JsonValueKind.Array => CatalogArray.FromElement(element),
        JsonValueKind.String => new CatalogString(element.GetString() ?? string.Empty),
        JsonValueKind.Number => new CatalogNumber(ReadNumber(element)),
        JsonValueKind.True => new CatalogBoolean(true),
        JsonValueKind.False => new CatalogBoolean(false),
        JsonValueKind.Null => CatalogNull.Instance,
        _ => throw new JsonException(
            "unexpected JSON token " + element.ValueKind.ToString()),
    };

    private static double ReadNumber(JsonElement element)
    {
        try
        {
            return element.GetDouble();
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            throw new JsonException(
                "json: cannot unmarshal number " + element.GetRawText()
                + " into Go value of type float64",
                ex);
        }
    }
}

internal sealed class CatalogNull : CatalogValue
{
    public static CatalogNull Instance { get; } = new();

    public override void WriteJson(StringBuilder builder) => builder.Append("null");

    public override CatalogValue Clone() => this;
}

internal sealed class CatalogBoolean(bool value) : CatalogValue
{
    public bool Value { get; } = value;

    public override void WriteJson(StringBuilder builder)
        => builder.Append(Value ? "true" : "false");

    public override CatalogValue Clone() => this;
}

internal sealed class CatalogString(string value) : CatalogValue
{
    public string Value { get; } = value;

    public override void WriteJson(StringBuilder builder) => builder.Append(GoQuote.Quote(Value));

    public override CatalogValue Clone() => this;
}

internal sealed class CatalogNumber(double value) : CatalogValue
{
    public double Value { get; } = value;

    public override void WriteJson(StringBuilder builder)
        => builder.Append(GoJsonFloat.Format(Value));

    public override CatalogValue Clone() => this;
}

internal sealed class CatalogArray(List<CatalogValue> items) : CatalogValue
{
    public List<CatalogValue> Items { get; } = items;

    public new static CatalogArray FromElement(JsonElement element)
    {
        var items = new List<CatalogValue>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            items.Add(CatalogValue.FromElement(item));
        }

        return new CatalogArray(items);
    }

    public override void WriteJson(StringBuilder builder)
    {
        builder.Append('[');
        for (var index = 0; index < Items.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            Items[index].WriteJson(builder);
        }

        builder.Append(']');
    }

    public override CatalogValue Clone()
        => new CatalogArray([.. Items.Select(static item => item.Clone())]);
}

internal sealed class CatalogObject : CatalogValue
{
    private readonly Dictionary<string, CatalogValue> _members = new(StringComparer.Ordinal);

    public int Count => _members.Count;

    public new static CatalogObject FromElement(JsonElement element)
    {
        var value = new CatalogObject();
        // encoding/json keeps the last occurrence of a duplicate key.
        foreach (var property in element.EnumerateObject())
        {
            value._members[property.Name] = CatalogValue.FromElement(property.Value);
        }

        return value;
    }

    public bool TryGet(string key, out CatalogValue value) => _members.TryGetValue(key, out value!);

    public CatalogValue? Get(string key)
        => _members.TryGetValue(key, out var value) ? value : null;

    public void Set(string key, CatalogValue value) => _members[key] = value;

    public IEnumerable<KeyValuePair<string, CatalogValue>> Members => _members;

    public override void WriteJson(StringBuilder builder)
    {
        builder.Append('{');
        var first = true;
        foreach (var key in _members.Keys.OrderBy(static key => key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append(GoQuote.Quote(key)).Append(':');
            _members[key].WriteJson(builder);
        }

        builder.Append('}');
    }

    public override CatalogValue Clone()
    {
        var clone = new CatalogObject();
        foreach (var (key, value) in _members)
        {
            clone._members[key] = value.Clone();
        }

        return clone;
    }
}

/// <summary>
/// Decodes the catalog's typed JSON the way Go's encoding/json decodes it into
/// <c>catalogFile</c>: unknown provider fields are ignored, null values fall
/// back to the zero value, and type mismatches fail the whole decode.
/// </summary>
internal static class CatalogParser
{
    public static CatalogFile ParseFile(ReadOnlySpan<byte> utf8)
    {
        using var document = ParseDocument(utf8);
        return FromRoot(document.RootElement);
    }

    public static CatalogFile FromRoot(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Null)
        {
            // json.Unmarshal("null", &catalogFile) leaves the zero value.
            return new CatalogFile();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal " + root.ValueKind.ToString().ToLowerInvariant()
                + " into Go value of type biz.catalogFile");
        }

        var file = new CatalogFile();
        if (TryGetProperty(root, "providers", out var providers))
        {
            if (providers.ValueKind == JsonValueKind.Null)
            {
                file.Providers = null;
            }
            else if (providers.ValueKind == JsonValueKind.Object)
            {
                file.Providers = new Dictionary<string, CatalogProvider>(StringComparer.Ordinal);
                foreach (var property in providers.EnumerateObject())
                {
                    file.Providers[property.Name] = ParseProvider(property.Value);
                }
            }
            else
            {
                throw new JsonException(
                    "json: cannot unmarshal " + providers.ValueKind.ToString().ToLowerInvariant()
                    + " into Go struct field catalogFile.providers of type map[string]biz.catalogProvider");
            }
        }

        return file;
    }

    public static CatalogProvider ParseProvider(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            // A null map value becomes the zero struct.
            return new CatalogProvider();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal " + element.ValueKind.ToString().ToLowerInvariant()
                + " into Go value of type biz.catalogProvider");
        }

        var provider = new CatalogProvider();

        if (TryGetProperty(element, "id", out var id) && id.ValueKind != JsonValueKind.Null)
        {
            provider.Id = ReadString(id, "catalogProvider.id");
        }

        if (TryGetProperty(element, "api", out var api) && api.ValueKind != JsonValueKind.Null)
        {
            provider.Api = ReadString(api, "catalogProvider.api");
        }

        if (TryGetProperty(element, "name", out var name) && name.ValueKind != JsonValueKind.Null)
        {
            provider.Name = ReadString(name, "catalogProvider.name");
        }

        if (TryGetProperty(element, "doc", out var doc) && doc.ValueKind != JsonValueKind.Null)
        {
            provider.Doc = ReadString(doc, "catalogProvider.doc");
        }

        if (TryGetProperty(element, "display_name", out var displayName)
            && displayName.ValueKind != JsonValueKind.Null)
        {
            provider.DisplayName = ReadString(displayName, "catalogProvider.display_name");
        }

        if (TryGetProperty(element, "vision", out var vision)
            && vision.ValueKind != JsonValueKind.Null)
        {
            if (vision.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new JsonException(
                    "json: cannot unmarshal " + vision.ValueKind.ToString().ToLowerInvariant()
                    + " into Go struct field catalogProvider.vision of type bool");
            }

            provider.Vision = vision.GetBoolean();
        }

        if (TryGetProperty(element, "models", out var models)
            && models.ValueKind != JsonValueKind.Null)
        {
            provider.Models = ParseModels(models);
        }

        if (TryGetProperty(element, "metadata", out var metadata)
            && metadata.ValueKind != JsonValueKind.Null)
        {
            if (metadata.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException(
                    "json: cannot unmarshal " + metadata.ValueKind.ToString().ToLowerInvariant()
                    + " into Go struct field catalogProvider.metadata of type map[string]interface {}");
            }

            provider.Metadata = new Dictionary<string, CatalogValue>(StringComparer.Ordinal);
            foreach (var property in metadata.EnumerateObject())
            {
                provider.Metadata[property.Name] = CatalogValue.FromElement(property.Value);
            }
        }

        return provider;
    }

    /// <summary>Decodes <c>[]map[string]any</c>: null elements become nil maps.</summary>
    public static List<CatalogValue> ParseModels(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(
                "json: cannot unmarshal " + element.ValueKind.ToString().ToLowerInvariant()
                + " into Go value of type []map[string]interface {}");
        }

        var models = new List<CatalogValue>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind is JsonValueKind.Null)
            {
                models.Add(CatalogNull.Instance);
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException(
                    "json: cannot unmarshal " + item.ValueKind.ToString().ToLowerInvariant()
                    + " into Go value of type map[string]interface {}");
            }

            models.Add(CatalogObject.FromElement(item));
        }

        return models;
    }

    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        // encoding/json matches struct fields case-insensitively and keeps the
        // last duplicate key.
        var found = false;
        value = default;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        return found;
    }

    public static JsonDocument ParseDocument(ReadOnlySpan<byte> utf8)
        => JsonDocument.Parse(utf8.ToArray());

    private static string ReadString(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(
                "json: cannot unmarshal " + element.ValueKind.ToString().ToLowerInvariant()
                + " into Go struct field " + field + " of type string");
        }

        return element.GetString() ?? string.Empty;
    }
}

