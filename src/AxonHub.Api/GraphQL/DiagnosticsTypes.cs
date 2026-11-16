using AxonHub.Api.Diagnostics;
using HotChocolate;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of <c>GetCacheDiagnosticsInput</c> (system.graphql:463-465).</summary>
public sealed class GetCacheDiagnosticsInput
{
    /// <summary><c>targets: [DiagnosticsTarget!]</c>: a null or empty list means the channel cache.</summary>
    public List<DiagnosticsTargetValue>? Targets { get; set; }
}

/// <summary>Port of <c>ClearCacheInput</c> (system.graphql:467-469).</summary>
public sealed class ClearCacheInput
{
    /// <summary><c>targets: [DiagnosticsTarget!]</c>: a null or empty list means the channel cache.</summary>
    public List<DiagnosticsTargetValue>? Targets { get; set; }
}

/// <summary>Port of <c>GetCacheDiagnosticsPayload</c> (system.graphql:471-475).</summary>
[GraphQLName("GetCacheDiagnosticsPayload")]
public sealed class GetCacheDiagnosticsPayloadType
{
    public string FileName { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public IReadOnlyList<DiagnosticsTargetValue> Targets { get; init; } = [];
}

/// <summary>Port of <c>ClearCachePayload</c> (system.graphql:477-481).</summary>
[GraphQLName("ClearCachePayload")]
public sealed class ClearCachePayloadType
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<DiagnosticsTargetValue> Targets { get; init; } = [];
}
