using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Emulation of the Channel / ChannelModelPrice / ChannelModelPriceVersion
/// schema privacy policies (internal/ent/schema/channel.go:233-252,
/// channel_model_price.go:62-73, channel_model_price_versions.go:66-77). The
/// reference evaluates them inside every ent query; the port applies the same
/// decisions explicitly before touching the store (C177/C014).
/// </summary>
/// <remarks>
/// <para>
/// All three schemas use the same read chain:
/// <c>APIKeyScopeQueryRule(read_channels)</c> → <c>OwnerRule()</c> →
/// <c>UserReadScopeRule(read_channels)</c>, then <c>default deny</c> when
/// every rule skipped (internal/scopes/policy.go:51).
/// </para>
/// <para>
/// The query half covers node(Channel), the channels / queryChannels /
/// allChannelSummarys / allChannelTags / countChannelsByType roots and the
/// channelModelPrices / versions / channel / channelModelPrice read edges
/// (§136). The mutation half (<c>OwnerRule()</c> →
/// <c>UserWriteScopeRule(write_channels)</c> → default deny) is evaluated at
/// the entry of every channel write resolver (C014).
/// </para>
/// <para>
/// The deny texts mirror the reference exactly, including ent's
/// <c>fmt.Errorf("%s: %w", msg, privacy.Deny)</c> suffix (C179), and a deny
/// throws <see cref="PrivacyDenyException"/>, which the GraphQL error filter
/// reports as <c>permission denied</c> / FORBIDDEN.
/// </para>
/// </remarks>
public sealed class ChannelPrivacy(
    IManagementIdentity identity,
    IUserAdminStore users)
{
    public const string ReadChannels = AuthorizationScope.ReadChannels;

    public const string WriteChannels = AuthorizationScope.WriteChannels;

    /// <summary>The Project schema's read scope (<c>scopes.ScopeReadProjects</c>).</summary>
    public const string ReadProjects = "read_projects";

    /// <summary>The reference's <c>scopes.ErrNoUser</c> (internal/scopes/rule.go:18).</summary>
    private const string NoUserInContext = "no user in context";

    /// <summary>The reference's <c>scopes.QueryPolicy</c> default (policy.go:51).</summary>
    private const string DefaultDeny = "default deny";

    /// <summary>Port of the Channel query policy.</summary>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task EvaluateQueryAsync(CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        // The test principal's decision context bypasses every rule
        // (authz.WithTestBypass → privacy.WithBypassPrivacy), including the
        // API-key rule, so it is checked before the policy sequence.
        if (current?.IsTest == true)
        {
            return;
        }

        // Rule 1: APIKeyScopeQueryRule — only applies to an API-key principal;
        // a context without an API key skips on to the next rule.
        var apiKey = identity.GetCurrentApiKey();
        if (apiKey is not null)
        {
            if (HasScope(apiKey.Scopes, ReadChannels))
            {
                return;
            }

            throw Deny($"API key does not have required scope: {ReadChannels}");
        }

        // Rule 2: OwnerRule — no user skips, non-owner skips.
        if (current?.IsOwner == true)
        {
            return;
        }

        // Rule 3: UserReadScopeRule — no user denies; a scope-less user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);
        if (PermissionValidator.HasSystemScope(loaded, ReadChannels))
        {
            return;
        }

        // QueryPolicy denies by default once every rule skipped.
        throw Deny(DefaultDeny);
    }

    /// <summary>
    /// Port of the Channel / ChannelModelPrice mutation policy: <c>OwnerRule()</c>
    /// → <c>UserWriteScopeRule(write_channels)</c> → default deny
    /// (internal/ent/schema/channel.go:239-245). An API-key principal has no
    /// user, so the write rule denies it with "no user in context" — API keys
    /// can never mutate channels.
    /// </summary>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task EvaluateMutationAsync(CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current?.IsTest == true)
        {
            return;
        }

        // Rule 1: OwnerRule — no user skips, non-owner skips.
        if (current?.IsOwner == true)
        {
            return;
        }

        // Rule 2: UserWriteScopeRule — no user denies; a scope-less user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);
        if (PermissionValidator.HasSystemScope(loaded, WriteChannels))
        {
            return;
        }

        // MutationPolicy denies by default once every rule skipped.
        throw Deny(DefaultDeny);
    }

    /// <summary>
    /// Port of the Project query policy (internal/ent/schema/project.go:121-134)
    /// for the project lookup behind allChannelTags:
    /// <c>UserProjectScopeReadRule(read_projects)</c> → <c>OwnerRule()</c> →
    /// <c>UserReadScopeRule(read_projects)</c> → default deny.
    /// </summary>
    /// <remarks>
    /// The first rule only allows when the user holds read_projects at system
    /// level or on the context project; it then filters the query to the
    /// context project. The only caller fetches the context project, so the
    /// filter always matches when the rule allows. <paramref name="systemBypass"/>
    /// mirrors <c>authz.RunWithSystemBypass</c> (allChannelSummarys resolves the
    /// profile under a bypass, allChannelTags does not).
    /// </remarks>
    /// <exception cref="PrivacyDenyException">The project query policy denied.</exception>
    public async Task EvaluateProjectQueryAsync(
        bool systemBypass,
        CancellationToken cancellationToken = default)
    {
        if (systemBypass)
        {
            return;
        }

        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current?.IsTest == true)
        {
            return;
        }

        // Rule 1: UserProjectScopeReadRule — a context without a project id or
        // without a user skips; the branch below always has a project id, but
        // the context id is what the rule checks and filters on.
        var contextProjectId = identity.GetCurrentProjectId();
        if (contextProjectId is not null && current is not null)
        {
            var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
            if (PermissionValidator.HasSystemScope(loaded, ReadProjects)
                || PermissionValidator.UserHasProjectScope(
                    loaded,
                    contextProjectId.Value,
                    ReadProjects))
            {
                return;
            }
        }

        // Rule 2: OwnerRule.
        if (current?.IsOwner == true)
        {
            return;
        }

        // Rule 3: UserReadScopeRule — no user denies; a scope-less user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        // QueryPolicy denies by default once every rule skipped.
        throw Deny(DefaultDeny);
    }

    /// <summary>
    /// Wraps a rule message the way ent reports a deny:
    /// <c>fmt.Errorf("%s: %w", msg, privacy.Deny)</c>.
    /// </summary>
    private static PrivacyDenyException Deny(string message)
        => new(message + ": " + PrivacyDenyException.DenyMessage);

    private static bool HasScope(string? scopesJson, string requiredScope)
        => PermissionValidator.ParseScopes(scopesJson)
            .Contains(requiredScope, StringComparer.Ordinal);
}
