using System.Text.Json.Serialization;

namespace AxonHub.Llm;

/// <summary>
/// Port of llm.AlphaSearchRequest (llm/alpha_search.go): carries the original
/// Codex/CPA alpha search JSON body. The protocol is kept opaque so AxonHub can
/// proxy new search fields without changing the unified model.
/// </summary>
public sealed class AlphaSearchRequest
{
    /// <summary>Go: <c>Body []byte `json:"-"`</c> — carried but never serialized.</summary>
    [JsonIgnore]
    public byte[] Body { get; set; } = [];
}

/// <summary>Port of llm.AlphaSearchResponse: the upstream alpha search JSON verbatim.</summary>
public sealed class AlphaSearchResponse
{
    /// <summary>Go: <c>Body []byte `json:"-"`</c> — carried but never serialized.</summary>
    [JsonIgnore]
    public byte[] Body { get; set; } = [];
}
