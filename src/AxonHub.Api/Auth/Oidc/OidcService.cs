using System.Text.Json.Serialization;
using AxonHub.Persistence;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>Port of biz.ProviderInfo (internal/server/biz/oidc.go:38-51).</summary>
public sealed record OidcProviderInfo
{
    /// <summary><c>id</c>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary><c>name</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary><c>display_name</c>.</summary>
    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary><c>jit_enabled</c>.</summary>
    [JsonPropertyName("jit_enabled")]
    public bool JitEnabled { get; init; }

    /// <summary><c>icon_url</c>.</summary>
    [JsonPropertyName("icon_url")]
    public string IconUrl { get; init; } = string.Empty;

    /// <summary><c>button_color</c>.</summary>
    [JsonPropertyName("button_color")]
    public string ButtonColor { get; init; } = string.Empty;

    /// <summary><c>active</c>.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    /// <summary><c>oidc_login_only</c>.</summary>
    [JsonPropertyName("oidc_login_only")]
    public bool OidcLoginOnly { get; init; }

    /// <summary><c>last_check,omitempty</c>: only set for inactive providers.</summary>
    [JsonPropertyName("last_check")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long LastCheck { get; init; }

    /// <summary><c>is_linked</c>.</summary>
    [JsonPropertyName("is_linked")]
    public bool IsLinked { get; init; }

    /// <summary><c>linked_identity_id,omitempty</c>.</summary>
    [JsonPropertyName("linked_identity_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LinkedIdentityId { get; init; }

    /// <summary><c>linked_email,omitempty</c>.</summary>
    [JsonPropertyName("linked_email")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LinkedEmail { get; init; }
}

/// <summary>
/// Port of the database-side half of <c>OIDCService.GetProviders</c>
/// (internal/server/biz/oidc.go:407-460): the provider list decorated with the
/// caller's linked identities. The provider registry itself is a singleton
/// (<see cref="OidcProviderRegistry"/>); the store is scoped with the request.
/// </summary>
public sealed class OidcService(OidcProviderRegistry registry, IUserProfileStore profiles)
{
    /// <summary>Port of <c>OIDCService.CountProviders</c> (oidc.go:338-340).</summary>
    public int CountProviders() => registry.CountProviders();

    /// <summary>Port of <c>OIDCService.GetProviders</c>.</summary>
    public async Task<IReadOnlyList<OidcProviderInfo>> GetProvidersAsync(
        long? userId,
        CancellationToken cancellationToken = default)
    {
        await registry.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var linkedIdentities = new Dictionary<string, OidcIdentityRecord>(StringComparer.Ordinal);
        if (userId is long id)
        {
            try
            {
                var identities = await profiles.ListOidcIdentitiesAsync(id, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var identity in identities)
                {
                    linkedIdentities[identity.Issuer] = identity;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // The reference ignores a failed identity lookup and lists the
                // providers without the is_linked markers.
            }
        }

        var configured = registry.ConfiguredProviders;
        var result = new List<OidcProviderInfo>(configured.Count);
        foreach (var rawProvider in configured)
        {
            var provider = rawProvider.Normalize();
            var providerId = provider.ProviderId;

            var displayName = provider.ProviderDisplayName;
            if (displayName.Length == 0)
            {
                displayName = provider.Name;
            }

            var status = registry.GetProviderStatus(providerId);

            string? linkedIdentityId = null;
            string? linkedEmail = null;
            if (linkedIdentities.TryGetValue(provider.GetIssuer(), out var identity))
            {
                linkedIdentityId = $"gid://axonhub/OIDCIdentity/{identity.Id}";
                linkedEmail = identity.Email is { Length: > 0 } email ? email : null;
            }

            result.Add(new OidcProviderInfo
            {
                Id = providerId,
                Name = provider.Name,
                DisplayName = displayName,
                JitEnabled = provider.JitEnabled,
                IconUrl = provider.IconUrl,
                ButtonColor = provider.ButtonColor,
                OidcLoginOnly = provider.OidcLoginOnly,
                Active = status.Active,
                LastCheck = status.Active ? 0 : status.LastCheck,
                IsLinked = linkedIdentityId is not null,
                LinkedIdentityId = linkedIdentityId,
                LinkedEmail = linkedEmail,
            });
        }

        return result;
    }
}
