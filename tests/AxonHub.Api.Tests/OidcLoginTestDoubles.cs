using AxonHub.Api.Auth.Oidc;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Scripted byte cache for the login-flow tests. Values and expirations stay
/// observable, and each operation can be made to fail, so the tests can assert
/// both the cached payloads and the reference's error wrapping.
/// </summary>
internal sealed class FakeOidcCache : IOidcCache
{
    public Dictionary<string, byte[]> Entries { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, TimeSpan> Expirations { get; } = new(StringComparer.Ordinal);

    public List<string> DeletedKeys { get; } = [];

    public Exception? GetFailure { get; set; }

    public Exception? SetFailure { get; set; }

    /// <summary>Only a set whose key starts with this prefix fails.</summary>
    public string? SetFailureKeyPrefix { get; set; }

    public Exception? DeleteFailure { get; set; }

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (GetFailure is not null)
        {
            return Task.FromException<byte[]?>(GetFailure);
        }

        return Task.FromResult(
            Entries.TryGetValue(key, out var value) ? value : (byte[]?)null);
    }

    public Task SetAsync(
        string key,
        byte[] value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var failure = SetFailureKeyPrefix is null
            || key.StartsWith(SetFailureKeyPrefix, StringComparison.Ordinal)
            ? SetFailure
            : null;
        if (failure is not null)
        {
            return Task.FromException(failure);
        }

        Entries[key] = value;
        Expirations[key] = expiration;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        DeletedKeys.Add(key);
        if (DeleteFailure is not null)
        {
            return Task.FromException(DeleteFailure);
        }

        Entries.Remove(key);
        Expirations.Remove(key);
        return Task.CompletedTask;
    }

    public string? Text(string key)
        => Entries.TryGetValue(key, out var value)
            ? System.Text.Encoding.UTF8.GetString(value)
            : null;
}

internal sealed class FakeOidcTokenClient : IOidcTokenClient
{
    public List<OidcTokenRequest> Requests { get; } = [];

    public OidcTokenResult Result { get; set; } = new("access-token", "Bearer", null);

    public Exception? Failure { get; set; }

    public Task<OidcTokenResult> ExchangeAsync(
        OidcTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Requests.Add(request);
        return Failure is not null
            ? Task.FromException<OidcTokenResult>(Failure)
            : Task.FromResult(Result);
    }
}

internal sealed class FakeOidcUserInfoClient : IOidcUserInfoClient
{
    public List<OidcUserInfoRequest> Requests { get; } = [];

    public OidcUserInfo Result { get; set; } = new(new OidcClaims(), new Dictionary<string, object?>());

    public Exception? Failure { get; set; }

    public Task<OidcUserInfo> GetAsync(
        OidcUserInfoRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Requests.Add(request);
        return Failure is not null
            ? Task.FromException<OidcUserInfo>(Failure)
            : Task.FromResult(Result);
    }
}

internal sealed class FakeOidcIdTokenVerifier : IOidcIdTokenVerifier
{
    public List<OidcIdTokenRequest> Requests { get; } = [];

    public OidcVerifiedIdToken Result { get; set; } =
        new("sub-1", new Dictionary<string, object?>());

    public Exception? Failure { get; set; }

    public Task<OidcVerifiedIdToken> VerifyAsync(
        OidcIdTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Requests.Add(request);
        return Failure is not null
            ? Task.FromException<OidcVerifiedIdToken>(Failure)
            : Task.FromResult(Result);
    }
}

internal sealed class FakeOidcJwksClient : IOidcJwksClient
{
    public IReadOnlyList<OidcJwksKey> Keys { get; set; } = [];

    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<OidcJwksKey>> GetKeysAsync(
        string jwksUri,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Failure is not null
            ? Task.FromException<IReadOnlyList<OidcJwksKey>>(Failure)
            : Task.FromResult(Keys);
    }
}

/// <summary>
/// In-memory <see cref="IOidcLoginStore"/> that mirrors the SQL store's visible
/// predicates (soft-deleted rows are invisible) and records every write so the
/// login service's branching can be asserted without a database.
/// </summary>
internal sealed class FakeOidcLoginStore : IOidcLoginStore
{
    private long _nextUserId = 1;
    private long _nextIdentityId = 1;

    public List<UserRecord> Users { get; } = [];

    public List<OidcIdentityRecord> Identities { get; } = [];

    public List<RoleRecord> Roles { get; } = [];

    public Dictionary<long, List<long>> UserRoles { get; } = [];

    public List<(long IdentityId, DateTime LastLoginAt)> LastLoginTouches { get; } = [];

    public List<OidcSyncUpdate> SyncUpdates { get; } = [];

    public List<(string Issuer, string Subject)> IdentityLookups { get; } = [];

    public List<IReadOnlyList<string>> RoleQueries { get; } = [];

    public List<(UserRecord User, IReadOnlyList<string> RoleNames, OidcIdentityRecord Identity)>
        CreationRequests { get; } = [];

    public Exception? IdentityLookupFailure { get; set; }

    public Exception? TouchFailure { get; set; }

    public Exception? EmailLookupFailure { get; set; }

    public Exception? UserByIdFailure { get; set; }

    public Exception? CreateIdentityFailure { get; set; }

    public OidcLoginStoreException? CreateUserWithIdentityFailure { get; set; }

    public Exception? UpdateFailure { get; set; }

    public Exception? ListRolesFailure { get; set; }

    public Exception? CountRolesFailure { get; set; }

    public Task<OidcIdentityWithUser?> GetIdentityWithUserAsync(
        string issuer,
        string subject,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        IdentityLookups.Add((issuer, subject));

        if (IdentityLookupFailure is not null)
        {
            return Task.FromException<OidcIdentityWithUser?>(IdentityLookupFailure);
        }

        var identity = Identities.FirstOrDefault(
            row => row.DeletedAt == 0 && row.Issuer == issuer && row.Subject == subject);
        if (identity is null)
        {
            return Task.FromResult<OidcIdentityWithUser?>(null);
        }

        var user = Users.FirstOrDefault(row => row.Id == identity.UserId && row.DeletedAt == 0);
        return Task.FromResult<OidcIdentityWithUser?>(
            new OidcIdentityWithUser(identity, user));
    }

    public Task TouchIdentityLastLoginAsync(
        long identityId,
        DateTime lastLoginAt,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        LastLoginTouches.Add((identityId, lastLoginAt));
        if (TouchFailure is not null)
        {
            return Task.FromException(TouchFailure);
        }

        var identity = Identities.FirstOrDefault(row => row.Id == identityId);
        if (identity is not null)
        {
            identity.LastLoginAt = lastLoginAt;
        }

        return Task.CompletedTask;
    }

    public Task<UserRecord?> GetUserByEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (EmailLookupFailure is not null)
        {
            return Task.FromException<UserRecord?>(EmailLookupFailure);
        }

        return Task.FromResult(
            Users.FirstOrDefault(row => row.DeletedAt == 0 && row.Email == email));
    }

    public Task<UserRecord?> GetUserByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (UserByIdFailure is not null)
        {
            return Task.FromException<UserRecord?>(UserByIdFailure);
        }

        return Task.FromResult(
            Users.FirstOrDefault(row => row.DeletedAt == 0 && row.Id == id));
    }

    public Task<long> CreateIdentityAsync(
        OidcIdentityRecord identity,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (CreateIdentityFailure is not null)
        {
            return Task.FromException<long>(CreateIdentityFailure);
        }

        AssignIdentityId(identity);
        Identities.Add(identity);
        return Task.FromResult(identity.Id);
    }

    public Task<UserRecord> CreateUserWithIdentityAsync(
        UserRecord user,
        IReadOnlyList<string> roleNames,
        OidcIdentityRecord identity,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        CreationRequests.Add((user, roleNames, identity));
        if (CreateUserWithIdentityFailure is not null)
        {
            return Task.FromException<UserRecord>(CreateUserWithIdentityFailure);
        }

        AssignUserId(user);
        Users.Add(user);

        var roleIds = new List<long>();
        foreach (var name in roleNames)
        {
            foreach (var role in Roles)
            {
                if (role.DeletedAt == 0
                    && string.Equals(role.Name, name, StringComparison.Ordinal)
                    && !roleIds.Contains(role.Id))
                {
                    roleIds.Add(role.Id);
                }
            }
        }

        if (roleIds.Count > 0)
        {
            UserRoles[user.Id] = roleIds;
        }

        identity.UserId = user.Id;
        AssignIdentityId(identity);
        Identities.Add(identity);
        return Task.FromResult(user);
    }

    public Task<UserRecord?> UpdateUserForSyncAsync(
        OidcSyncUpdate update,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        SyncUpdates.Add(update);
        if (UpdateFailure is not null)
        {
            return Task.FromException<UserRecord?>(UpdateFailure);
        }

        var user = Users.FirstOrDefault(
            row => row.DeletedAt == 0 && row.Id == update.UserId);
        if (user is null)
        {
            return Task.FromResult<UserRecord?>(null);
        }

        if (update.FirstName is not null || update.LastName is not null)
        {
            user.FirstName = update.FirstName ?? string.Empty;
            user.LastName = update.LastName ?? string.Empty;
        }

        if (update.Avatar is not null)
        {
            user.Avatar = update.Avatar;
        }

        if (update.IsOwner is bool isOwner)
        {
            user.IsOwner = isOwner;
        }

        if (update.Scopes is not null)
        {
            user.Scopes = System.Text.Json.JsonSerializer.Serialize(update.Scopes);
        }
        else if (update.ClearScopes)
        {
            user.Scopes = null;
        }

        var roleIds = UserRoles.TryGetValue(user.Id, out var existing)
            ? existing
            : [];
        if (update.ClearRoles)
        {
            roleIds = [];
        }

        foreach (var roleId in update.AddRoleIds)
        {
            if (!roleIds.Contains(roleId))
            {
                roleIds.Add(roleId);
            }
        }

        if (roleIds.Count > 0)
        {
            UserRoles[user.Id] = roleIds;
        }
        else
        {
            UserRoles.Remove(user.Id);
        }

        return Task.FromResult<UserRecord?>(user);
    }

    public Task<IReadOnlyList<RoleRecord>> ListRolesByNameAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        RoleQueries.Add(names);
        if (ListRolesFailure is not null)
        {
            return Task.FromException<IReadOnlyList<RoleRecord>>(ListRolesFailure);
        }

        var roles = Roles
            .Where(role => role.DeletedAt == 0 && names.Contains(role.Name, StringComparer.Ordinal))
            .OrderBy(role => role.Id)
            .ToList();
        return Task.FromResult<IReadOnlyList<RoleRecord>>(roles);
    }

    public Task<int> CountUserRolesAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (CountRolesFailure is not null)
        {
            return Task.FromException<int>(CountRolesFailure);
        }

        var liveRoleIds = Roles
            .Where(role => role.DeletedAt == 0)
            .Select(role => role.Id)
            .ToHashSet();
        var count = UserRoles.TryGetValue(userId, out var roleIds)
            ? roleIds.Distinct().Count(liveRoleIds.Contains)
            : 0;
        return Task.FromResult(count);
    }

    public UserRecord AddUser(Action<UserRecord>? configure = null)
    {
        var row = new UserRecord
        {
            Email = "user@example.com",
            Status = "activated",
            PreferLanguage = "en",
            Password = "hashed",
        };
        configure?.Invoke(row);
        AssignUserId(row);
        Users.Add(row);
        return row;
    }

    public OidcIdentityRecord AddIdentity(Action<OidcIdentityRecord>? configure = null)
    {
        var row = new OidcIdentityRecord
        {
            Issuer = "https://issuer.example.com",
            Subject = "sub-1",
            Email = "user@example.com",
        };
        configure?.Invoke(row);
        AssignIdentityId(row);
        Identities.Add(row);
        return row;
    }

    public RoleRecord AddRole(string name, long? id = null, Action<RoleRecord>? configure = null)
    {
        var row = new RoleRecord { Name = name, Level = "system" };
        configure?.Invoke(row);
        row.Id = id ?? (Roles.Count == 0 ? 1 : Roles.Max(static role => role.Id) + 1);
        Roles.Add(row);
        return row;
    }

    private void AssignUserId(UserRecord user)
    {
        user.Id = user.Id > 0 ? user.Id : _nextUserId++;
        if (user.Id >= _nextUserId)
        {
            _nextUserId = user.Id + 1;
        }
    }

    private void AssignIdentityId(OidcIdentityRecord identity)
    {
        identity.Id = identity.Id > 0 ? identity.Id : _nextIdentityId++;
        if (identity.Id >= _nextIdentityId)
        {
            _nextIdentityId = identity.Id + 1;
        }
    }
}
