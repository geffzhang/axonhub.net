using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.Xai.Subscription;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging;
using GoJson = AxonHub.Llm.Json.GoJson;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Database and HTTP half of <see cref="IChannelTokenGrain"/>: builds the
/// reference's type-specific <c>TokenProvider</c> recipe for one refresh over
/// the grain's snapshot, and persists the refreshed credentials through the
/// reference's optimistic retry loop (channel_llm.go:1642-1702).
/// </summary>
/// <remarks>
/// The grains module references neither the LLM nor the persistence module, so
/// the provider recipes and the <c>OnRefreshed</c> callback live here. Each call
/// constructs a throwaway provider: the provider's singleflight and cache are
/// the grain's, while the recipe (endpoints, exchange strategy, user agent) is
/// stateless.
/// <para>
/// Scoped to codex, claudecode and xai_subscription — the three channel types
/// whose outbound is a bare <see cref="ITokenGetter"/> (§146). Copilot,
/// Antigravity and the model-fetcher/endpoint-detect refresh paths keep their
/// own providers (C094, C158).
/// </para>
/// </remarks>
public sealed class ChannelTokenSource(
    IChannelTokenStore store,
    IExecutor executor,
    TimeProvider timeProvider,
    ILogger<ChannelTokenSource> logger) : IChannelTokenSource
{
    /// <summary>
    /// token_provider.go:323/409: <c>EnsureFresh(ctx, 0)</c> falls back to the
    /// five-minute window; the grain passes the same default.
    /// </summary>
    private static readonly TimeSpan DefaultRefreshBefore = TimeSpan.Zero;

    private static readonly Action<ILogger, long, Exception?> LogPersistFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "ChannelTokenPersistFailed"),
            "failed to persist refreshed credentials channel={ChannelId}");

    public Task<ChannelTokenRefreshResult> RefreshAsync(
        ChannelTokenRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var credentials = ChannelOAuthTokenMapping.ToCredentials(request.Credentials);
        var state = new RefreshState(request.CredentialsJson, request.UpdatedAt);
        var onRefreshed = CreateOnRefreshed(request, state);
        var provider = CreateProvider(request.ChannelType, credentials, onRefreshed);

        try
        {
            var fresh = request.EnsureFresh
                ? provider.EnsureFresh(cancellationToken, DefaultRefreshBefore)
                : provider.Get(cancellationToken);

            return Task.FromResult(new ChannelTokenRefreshResult
            {
                Credentials = ChannelOAuthTokenMapping.ToToken(fresh),
                CredentialsJson = state.CredentialsJson,
                UpdatedAt = state.UpdatedAt,
            });
        }
        catch (InvalidOperationException ex) when (ex.Message == "credentials is nil")
        {
            // token_provider.go:327-330: the conflict winner is no longer OAuth,
            // so the provider stopped its auto-refresh and retired. The state
            // carries the winning row the callback reloaded.
            return Task.FromResult(new ChannelTokenRefreshResult
            {
                Credentials = null,
                CredentialsJson = state.CredentialsJson,
                UpdatedAt = state.UpdatedAt,
                Stopped = true,
            });
        }
    }

    /// <summary>
    /// Builds the reference's per-type provider around the grain's snapshot
    /// (channel_llm.go:331-425 codex, :924-989 claudecode, :867-884
    /// xai_subscription).
    /// </summary>
    private TokenProvider CreateProvider(
        string channelType,
        OAuthCredentials credentials,
        Func<CancellationToken, OAuthCredentials, Exception?> onRefreshed)
        => channelType switch
        {
            ChannelOutboundFactory.CodexChannelType => CodexToken.CreateTokenProvider(
                new CodexTokenProviderParams
                {
                    Credentials = credentials,
                    HttpClient = executor,
                    OnRefreshed = onRefreshed,
                }),
            ChannelOutboundFactory.ClaudeCodeChannelType => ClaudeCodeTokenProvider.Create(
                new TokenProviderParams
                {
                    Credentials = credentials,
                    HttpClient = executor,
                    OnRefreshed = onRefreshed,
                }),
            ChannelOutboundFactory.XaiSubscriptionChannelType => SubscriptionToken.NewTokenProvider(
                new SubscriptionTokenProviderParams
                {
                    Credentials = credentials,
                    HttpClient = executor,
                    OnRefreshed = onRefreshed,
                }),
            _ => throw new InvalidOperationException(
                "token refresh is not supported for channel type " + GoCast.GoQuote(channelType)),
        };

    /// <summary>
    /// Port of <c>refreshOAuthTokenWithClient</c> as the provider's persistence
    /// callback (channel_llm.go:1647-1701). Returning
    /// <see cref="CredentialConflictException"/> makes the provider adopt the
    /// winning row; any other error is logged by the caller and the refreshed
    /// credentials stay in memory.
    /// </summary>
    private Func<CancellationToken, OAuthCredentials, Exception?> CreateOnRefreshed(
        ChannelTokenRefreshRequest request,
        RefreshState state)
        => (cancellationToken, refreshed) =>
        {
            Exception? error;
            try
            {
                error = PersistRefreshedCredentials(request, state, refreshed, cancellationToken);
            }
            catch (Exception ex)
            {
                // A cancelled store call is a persist failure too: the
                // reference returns the context error from OnRefreshed, logs
                // it, and keeps the refreshed credentials in memory.
                error = ex;
            }

            if (error is not null and not CredentialConflictException)
            {
                // token_provider.go:225/319: the provider logs and ignores
                // non-conflict failures.
                LogPersistFailed(logger, request.ChannelId, error);
            }

            return error;
        };

    private Exception? PersistRefreshedCredentials(
        ChannelTokenRefreshRequest request,
        RefreshState state,
        OAuthCredentials refreshed,
        CancellationToken cancellationToken)
    {
        // channel_llm.go:1647-1667: the write base is the snapshot credentials
        // document; only apiKey and oauth are replaced (antigravity's
        // refreshToken|projectID form is not supported here, C094).
        var updated = ChannelWriteSerialization.ParseCredentials(state.CredentialsJson);
        updated.ApiKey = refreshed.ToJson();
        updated.OAuth = ToOAuthDraft(refreshed);
        var credentialsJson = AxonHub.Llm.Json.GoJson.MarshalToString(updated);

        while (true)
        {
            var expectedUpdatedAt = state.UpdatedAt;
            var updatedAt = timeProvider.GetUtcNow().UtcDateTime;

            ChannelTokenUpdate result;
            try
            {
                result = store.UpdateCredentialsIfUnchangedAsync(
                        request.ChannelId,
                        credentialsJson,
                        expectedUpdatedAt,
                        updatedAt,
                        cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                return ex;
            }

            if (result.Updated)
            {
                state.CredentialsJson = credentialsJson;
                state.UpdatedAt = updatedAt;
                return null;
            }

            var current = result.Current
                ?? throw new InvalidOperationException("missing current channel row");

            // channel_llm.go:1676-1682: a metadata-only update must not
            // discard a rotated refresh token, so retry against the freshly
            // observed timestamp with the same write payload.
            var unchanged = string.Equals(current.Type, request.ChannelType, StringComparison.Ordinal)
                && ChannelCredentialsComparison.Equal(current.Credentials, state.CredentialsJson);

            state.CredentialsJson = current.Credentials;
            state.UpdatedAt = current.UpdatedAt;

            if (unchanged)
            {
                continue;
            }

            // channel_llm.go:1684-1691: a real concurrent credential change
            // wins. A row that is no longer OAuth retires the provider.
            OAuthCredentials? winner = null;
            if (string.Equals(current.Type, request.ChannelType, StringComparison.Ordinal)
                && ChannelCredentials.IsOAuth(current.Credentials))
            {
                try
                {
                    winner = OAuthCredentialsJson.ResolveChannelCredentials(current.Credentials);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    // The reference reports an empty conflict error here.
                    return new CredentialConflictException(null);
                }
            }

            return new CredentialConflictException(winner);
        }
    }

    private static OAuthCredentialsDraft ToOAuthDraft(OAuthCredentials credentials) => new()
    {
        ClientId = credentials.ClientId.Length > 0 ? credentials.ClientId : null,
        AccessToken = credentials.AccessToken,
        RefreshToken = credentials.RefreshToken,
        IdToken = credentials.IdToken.Length > 0 ? credentials.IdToken : null,
        ExpiresAt = GoTime.Rfc3339Nano(credentials.ExpiresAt.UtcDateTime),
        TokenType = credentials.TokenType.Length > 0 ? credentials.TokenType : null,
        Scopes = credentials.Scopes is { Count: > 0 } ? [.. credentials.Scopes] : null,
    };

    /// <summary>
    /// The credentials document and row timestamp shared by the provider's
    /// snapshot and its callback. <c>OnRefreshed</c> may run more than once per
    /// <c>Get</c> (a still-expired conflict winner is refreshed again), so the
    /// callback advances this box the way the reference mutates
    /// <c>ch.Credentials</c>/<c>ch.UpdatedAt</c>.
    /// </summary>
    private sealed class RefreshState(string credentialsJson, DateTime updatedAt)
    {
        public string CredentialsJson { get; set; } = credentialsJson;

        public DateTime UpdatedAt { get; set; } = updatedAt;
    }
}

/// <summary>
/// Maps between the LLM's <see cref="OAuthCredentials"/> and the
/// grains module's serializable twin (§146).
/// </summary>
public static class ChannelOAuthTokenMapping
{
    public static ChannelOAuthToken ToToken(OAuthCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        return new ChannelOAuthToken
        {
            ClientId = credentials.ClientId,
            AccessToken = credentials.AccessToken,
            RefreshToken = credentials.RefreshToken,
            IdToken = credentials.IdToken,
            ExpiresAt = credentials.ExpiresAt,
            TokenType = credentials.TokenType,
            Scopes = credentials.Scopes is null ? null : [.. credentials.Scopes],
        };
    }

    public static OAuthCredentials ToCredentials(ChannelOAuthToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return new OAuthCredentials
        {
            ClientId = token.ClientId,
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            IdToken = token.IdToken,
            ExpiresAt = token.ExpiresAt,
            TokenType = token.TokenType,
            Scopes = token.Scopes is null ? null : [.. token.Scopes],
        };
    }
}

/// <summary>
/// Port of <c>channelCredentialsEqual</c> and its helpers
/// (channel_llm.go:1704-1741). <c>vertexApiKeys</c> is deliberately not
/// compared, matching the reference.
/// </summary>
public static class ChannelCredentialsComparison
{
    public static bool Equal(string? a, string? b)
    {
        var left = ChannelWriteSerialization.ParseCredentials(a);
        var right = ChannelWriteSerialization.ParseCredentials(b);

        return string.Equals(left.ApiKey, right.ApiKey, StringComparison.Ordinal)
            && OAuthEqual(left.OAuth, right.OAuth)
            && SequenceEqual(left.ApiKeys, right.ApiKeys)
            && string.Equals(left.ManagementApiKey, right.ManagementApiKey, StringComparison.Ordinal)
            && AzureEqual(left.Azure, right.Azure)
            && GcpEqual(left.Gcp, right.Gcp);
    }

    private static bool OAuthEqual(OAuthCredentialsDraft? a, OAuthCredentialsDraft? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return string.Equals(a.ClientId ?? string.Empty, b.ClientId ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(a.AccessToken, b.AccessToken, StringComparison.Ordinal)
            && string.Equals(a.RefreshToken, b.RefreshToken, StringComparison.Ordinal)
            && string.Equals(a.IdToken ?? string.Empty, b.IdToken ?? string.Empty, StringComparison.Ordinal)
            && ExpiresAtEqual(a.ExpiresAt, b.ExpiresAt)
            && string.Equals(a.TokenType ?? string.Empty, b.TokenType ?? string.Empty, StringComparison.Ordinal)
            && SequenceEqual(a.Scopes, b.Scopes);
    }

    private static bool ExpiresAtEqual(string a, string b)
    {
        var left = GoTimeParsing.TryParseRfc3339(a);
        var right = GoTimeParsing.TryParseRfc3339(b);

        // Both rows were loaded by the reference's JSON scanner, so an
        // unparseable timestamp is unreachable there; compare the raw text so
        // a malformed row cannot masquerade as unchanged.
        if (left is null || right is null)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        // Go's time.Time.Equal compares instants, not representations.
        return left.Value == right.Value;
    }

    private static bool SequenceEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        // Go's slices.Equal treats nil and empty as equal.
        var leftCount = a?.Count ?? 0;
        var rightCount = b?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        for (var i = 0; i < leftCount; i++)
        {
            if (!string.Equals(a![i], b![i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AzureEqual(AzureCredentialDraft? a, AzureCredentialDraft? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return string.Equals(a.ApiVersion, b.ApiVersion, StringComparison.Ordinal);
    }

    private static bool GcpEqual(GcpCredentialDraft? a, GcpCredentialDraft? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return string.Equals(a.Region, b.Region, StringComparison.Ordinal)
            && string.Equals(a.ProjectId, b.ProjectId, StringComparison.Ordinal)
            && string.Equals(a.JsonData, b.JsonData, StringComparison.Ordinal);
    }
}
