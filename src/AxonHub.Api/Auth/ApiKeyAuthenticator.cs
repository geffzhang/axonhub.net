using System.Text.Json;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Auth;

/// <summary>Authentication rejected the caller with 401 Invalid API key.</summary>
public sealed class InvalidApiKeyException : Exception
{
    public InvalidApiKeyException()
        : base("invalid api key")
    {
    }

    public InvalidApiKeyException(string message)
        : base(message)
    {
    }

    public InvalidApiKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The API key's allowed_ips list does not contain the client.</summary>
public sealed class ClientIpForbiddenException : Exception
{
    public ClientIpForbiddenException()
        : base("IP address is not allowed for this API key")
    {
    }
}

/// <summary>HTTP-level seam so tests can supply an authenticated caller directly.</summary>
public interface IApiKeyAuthenticator
{
    Task<RequestIdentity> AuthenticateAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Strict authentication for surfaces that never fall back to no-auth
    /// (middleware.WithGeminiKeyAuth): a supplied key is mandatory and any
    /// lookup or validation failure rejects the request.
    /// </summary>
    Task<RequestIdentity> AuthenticateKeyAsync(
        HttpRequest request,
        string key,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of biz.AuthService.AuthenticateAPIKey + AuthenticateNoAuth and the
/// surrounding middleware.WithAPIKeyConfig flow
/// (internal/server/middleware/auth.go:27-79).
/// </summary>
/// <remarks>
/// The reference caches API keys and projects; the port reads PostgreSQL on
/// every request, which is observably identical but slower (C026). No-auth
/// keys are created lazily exactly like APIKeyService.EnsureNoAuthAPIKey.
/// </remarks>
public sealed class ApiKeyAuthenticator(
    IApiKeyStore apiKeys,
    IProjectStore projects,
    IUserStore users,
    ApiKeyAuthOptions options,
    TimeProvider timeProvider) : IApiKeyAuthenticator
{
    /// <summary>biz.NoAuthAPIKeyValue: never accepted as a client-supplied key.</summary>
    public const string NoAuthApiKeyValue = "AXONHUB_API_KEY_NO_AUTH";

    /// <summary>biz.NoAuthAPIKeyName.</summary>
    public const string NoAuthApiKeyName = "No Auth System Key";

    public async Task<RequestIdentity> AuthenticateAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = ApiKeyHeaderExtractor.Extract(request.Headers);

        // DO NOT ALLOW USE NO AUTH API KEY DIRECTLY.
        if (key == NoAuthApiKeyValue)
        {
            throw new InvalidApiKeyException();
        }

        ApiKeyRecord? apiKey = null;
        if (key is not null)
        {
            try
            {
                apiKey = await AuthenticateApiKeyAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // middleware.WithAPIKeyConfig falls back to no-auth on any error.
                apiKey = null;
            }
        }

        apiKey ??= await AuthenticateNoAuthAsync(cancellationToken).ConfigureAwait(false);

        CheckAllowedIps(request, apiKey);

        return new RequestIdentity(apiKey.ProjectId, apiKey.Id);
    }

    /// <summary>
    /// Port of middleware.WithGeminiKeyAuth's direct AuthService.
    /// AuthenticateAPIKey call (auth.go:262-310): no no-auth fallback, the
    /// allowed_ips check follows a successful lookup exactly like the
    /// reference.
    /// </summary>
    public async Task<RequestIdentity> AuthenticateKeyAsync(
        HttpRequest request,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var apiKey = await AuthenticateApiKeyAsync(key, cancellationToken).ConfigureAwait(false);

        CheckAllowedIps(request, apiKey);

        return new RequestIdentity(apiKey.ProjectId, apiKey.Id);
    }

    /// <summary>Port of AuthService.AuthenticateAPIKey.</summary>
    private async Task<ApiKeyRecord> AuthenticateApiKeyAsync(string key, CancellationToken cancellationToken)
    {
        var apiKey = await apiKeys.GetByKeyAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidApiKeyException();

        if (!string.Equals(apiKey.Status, "enabled", StringComparison.Ordinal))
        {
            throw new InvalidApiKeyException();
        }

        var project = await projects.GetByIdAsync(apiKey.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidApiKeyException();

        if (!string.Equals(project.Status, "active", StringComparison.Ordinal))
        {
            throw new InvalidApiKeyException();
        }

        if (string.Equals(apiKey.Type, "noauth", StringComparison.Ordinal))
        {
            throw new InvalidApiKeyException();
        }

        return apiKey;
    }

    /// <summary>Port of AuthService.AuthenticateNoAuth + EnsureNoAuthAPIKey.</summary>
    private async Task<ApiKeyRecord> AuthenticateNoAuthAsync(CancellationToken cancellationToken)
    {
        if (!options.AllowNoAuth)
        {
            throw new InvalidApiKeyException("API key is required");
        }

        var apiKey = await apiKeys.GetByKeyAsync(NoAuthApiKeyValue, cancellationToken).ConfigureAwait(false)
            ?? await CreateNoAuthApiKeyAsync(cancellationToken).ConfigureAwait(false);

        if (!string.Equals(apiKey.Status, "enabled", StringComparison.Ordinal))
        {
            throw new InvalidApiKeyException();
        }

        var project = await projects.GetByIdAsync(apiKey.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidApiKeyException();

        if (!string.Equals(project.Status, "active", StringComparison.Ordinal))
        {
            throw new InvalidApiKeyException();
        }

        return apiKey;
    }

    /// <summary>
    /// Port of APIKeyService.EnsureNoAuthAPIKey's create branch: the first
    /// project (id ascending) and the owner user. A missing either one is
    /// ent.NotFound in the reference, which the middleware maps to 401.
    /// </summary>
    private async Task<ApiKeyRecord> CreateNoAuthApiKeyAsync(CancellationToken cancellationToken)
    {
        var project = await projects.GetFirstAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidApiKeyException("failed to get default project");

        var owner = await users.FindOwnerAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidApiKeyException("failed to get owner user for noauth api key");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var apiKey = new ApiKeyRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            Key = NoAuthApiKeyValue,
            Name = NoAuthApiKeyName,
            Type = "noauth",
            Status = "enabled",
            // Ent create defaults: the field's string list, an empty profiles
            // object and an empty IP list.
            Scopes = """["write_requests","read_channels"]""",
            Profiles = """{"activeProfile":"","profiles":null}""",
            AllowedIps = "[]",
            ProjectId = project.Id,
            UserId = owner.Id,
        };

        await apiKeys.CreateAsync(apiKey, cancellationToken).ConfigureAwait(false);
        return apiKey;
    }

    /// <summary>Port of middleware.WithAPIKeyConfig's allowed_ips check.</summary>
    private void CheckAllowedIps(HttpRequest request, ApiKeyRecord apiKey)
    {
        var allowed = ParseAllowedIps(apiKey.AllowedIps);
        if (allowed.Length == 0)
        {
            return;
        }

        var clientIp = ClientAddress.Resolve(request, options.TrustedProxies);
        var candidates = clientIp.Length == 0 ? Array.Empty<string>() : [clientIp];
        if (!ClientAddress.IsAnyAllowedIp(candidates, allowed))
        {
            throw new ClientIpForbiddenException();
        }
    }

    private static string[] ParseAllowedIps(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<string[]>(json) ?? [];
    }
}
