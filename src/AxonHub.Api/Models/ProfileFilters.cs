using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;

namespace AxonHub.Api.Models;

/// <summary>
/// Port of the profile half of the selectCandidates inbound middleware
/// (internal/server/orchestrator/select_candidates.go:27-51): the API key's
/// project profile and the key's own active profile narrow the candidate list
/// before load balancing.
/// </summary>
/// <remarks>
/// The reference applies the filters as decorators around the base selector,
/// so they also apply to the tester's SpecifiedChannelSelector. The port applies
/// the same predicates to the selected candidate list; order is preserved and a
/// profile with no channel IDs and no channel tags is a no-op. The project
/// filters are only consulted when an API key is in context, exactly like
/// <c>inbound.state.APIKey.Edges.Project</c>.
/// </remarks>
public static class ProfileFilters
{
    /// <summary>Port of Project.GetActiveProfile (ent/extra.go:16-28).</summary>
    public static ProjectProfileModel? ActiveProjectProfile(ProjectRecord? project)
    {
        if (project is null)
        {
            return null;
        }

        var profiles = ProjectProfilesCodec.Deserialize(project.Profiles);
        if (profiles.ActiveProfile.Length == 0)
        {
            return null;
        }

        foreach (var profile in profiles.Profiles ?? [])
        {
            if (string.Equals(profile.Name, profiles.ActiveProfile, StringComparison.Ordinal))
            {
                return profile;
            }
        }

        return null;
    }

    /// <summary>Port of APIKey.GetActiveProfile (ent/extra.go:30-42).</summary>
    public static ApiKeyProfileModel? ActiveApiKeyProfile(ApiKeyRecord? apiKey)
        => apiKey is null ? null : ApiKeyProfilesCodec.ActiveProfile(apiKey.Profiles);

    /// <summary>
    /// Applies the project profile (upper boundary) and then the key profile
    /// (which narrows further within the project scope). Both are intersections;
    /// an empty or nil filter field leaves the list untouched.
    /// </summary>
    public static IReadOnlyList<ChannelCandidate> ApplyToCandidates(
        IReadOnlyList<ChannelCandidate> candidates,
        RequestIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(identity);

        IReadOnlyList<ChannelCandidate> scoped = candidates;

        // select_candidates.go:27-40: the project edge belongs to the API key,
        // so without a key there is no project profile in play.
        if (identity.ApiKey is not null
            && ActiveProjectProfile(identity.Project) is { } projectProfile)
        {
            scoped = ApplyProfile(
                scoped,
                Widen(projectProfile.ChannelIDs),
                projectProfile.ChannelTags,
                projectProfile.ChannelTagsMatchMode);
        }

        if (ActiveApiKeyProfile(identity.ApiKey) is { } profile)
        {
            scoped = ApplyProfile(
                scoped,
                profile.ChannelIDs,
                profile.ChannelTags,
                profile.ChannelTagsMatchMode);
        }

        return scoped;
    }

    private static IReadOnlyList<ChannelCandidate> ApplyProfile(
        IReadOnlyList<ChannelCandidate> candidates,
        IReadOnlyList<long>? channelIds,
        IReadOnlyList<string>? tags,
        string matchMode)
    {
        IReadOnlyList<ChannelCandidate> scoped = candidates;

        if (channelIds is { Count: > 0 })
        {
            var allowed = new HashSet<long>(channelIds);
            scoped = [.. scoped.Where(candidate => allowed.Contains(candidate.ChannelId))];
        }

        if (tags is { Count: > 0 })
        {
            scoped = [.. scoped.Where(candidate => ChannelTagsCodec.Match(
                tags,
                matchMode,
                ChannelTagsCodec.Parse(candidate.Tags)))];
        }

        return scoped;
    }

    /// <summary>
    /// ProjectProfileModel.ChannelIDs is the reference's <c>[]int</c>, while
    /// channel ids are 64-bit in the port; widen the list once for filtering.
    /// </summary>
    private static long[]? Widen(IReadOnlyList<int>? channelIds)
        => channelIds?.Select(static id => (long)id).ToArray();
}
