namespace AxonHub.Api.Models;

/// <summary>
/// Port of <c>objects.ModelSettings</c> (internal/objects/model.go:43-49): the
/// per-model <c>settings</c> JSON column. The reference's custom
/// <c>UnmarshalJSON</c> seeds both routing policies to <c>"default"</c> and
/// normalizes <c>""</c> / <c>"system_default"</c>, which
/// <see cref="ModelSettingsCodec.DeserializeEntitySettings"/> reproduces.
/// </summary>
/// <remarks>
/// <see cref="Associations"/> is <c>[]*objects.ModelAssociation</c> in Go, so
/// null elements survive and <c>nil</c> marshals as JSON <c>null</c> (the
/// field has no omitempty).
/// </remarks>
public sealed record ModelEntitySettings
{
    public bool DisableDeveloperSettingsInheritance { get; init; }

    public IReadOnlyList<ModelAssociation?>? Associations { get; init; }

    public string LoadBalancerStrategy { get; init; } = RoutingPolicy.Default;

    public string TraceStickyMode { get; init; } = RoutingPolicy.Default;

    /// <summary>Port of objects.ModelSettings.NormalizeRoutingPolicy.</summary>
    public ModelEntitySettings NormalizeRoutingPolicy() => this with
    {
        LoadBalancerStrategy = RoutingPolicy.NormalizeValue(LoadBalancerStrategy),
        TraceStickyMode = RoutingPolicy.NormalizeValue(TraceStickyMode),
    };
}
