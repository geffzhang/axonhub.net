using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.Settings;

/// <summary>Port of biz.InitializeSystemParams (internal/server/biz/system.go:712-719).</summary>
public sealed record InitializeSystemParameters
{
    public required string OwnerEmail { get; init; }

    public required string OwnerPassword { get; init; }

    public required string OwnerFirstName { get; init; }

    public required string OwnerLastName { get; init; }

    public required string BrandName { get; init; }

    /// <summary>Empty selects "en" (system.go:761-764).</summary>
    public string PreferLanguage { get; init; } = string.Empty;
}

/// <summary>
/// Port of the first-run half of biz.SystemService: IsInitialized and the
/// Initialize transaction (internal/server/biz/system.go:698-846).
/// </summary>
public interface ISystemInitializationService
{
    /// <summary>Port of SystemService.IsInitialized (system.go:698-710).</summary>
    Task<bool> IsInitializedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SystemService.Initialize (system.go:721-846): create the owner
    /// user, the default project with its role presets, the JWT secret, the
    /// brand name, the Primary data storage and its default setting, then flip
    /// the initialized flag and record the build version — all in one
    /// transaction.
    /// </summary>
    Task InitializeAsync(
        InitializeSystemParameters parameters,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISystemInitializationService"/>
public sealed class SystemInitializationService(
    ISystemStore systems,
    IUserStore users,
    IDataStorageStore dataStorages,
    ProjectManagementService projects,
    IPasswordHasher passwordHasher,
    ISystemSettingsAdminService systemSettings,
    TimeProvider timeProvider) : ISystemInitializationService
{
    /// <summary>biz.SystemKeyBrandName (system.go:76).</summary>
    public const string BrandNameKey = "system_brand_name";

    /// <summary>biz.SystemKeyVersion (system.go:78).</summary>
    public const string VersionKey = "system_version";

    /// <summary>The default project name the initialize transaction uses.</summary>
    public const string DefaultProjectName = "Default";

    /// <summary>The Primary storage seeded by the same transaction.</summary>
    public const string PrimaryStorageName = "Primary";

    /// <summary>The owner's implicit full scope (system.go:800).</summary>
    private static readonly string[] OwnerScopes = ["*"];

    public Task<bool> IsInitializedAsync(CancellationToken cancellationToken = default)
        => systemSettings.IsInitializedAsync(cancellationToken);

    public async Task InitializeAsync(
        InitializeSystemParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        bool isInitialized;
        try
        {
            isInitialized = await IsInitializedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to check initialization status: " + ex.Message,
                ex);
        }

        if (isInitialized)
        {
            // system.go:730-733: already initialized is not an error.
            return;
        }

        string secretKey;
        try
        {
            secretKey = GenerateSecretKey();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to generate secret key: " + ex.Message,
                ex);
        }

        // The reference distinguishes "failed to start transaction" (BeginTx)
        // from "failed to commit transaction" (tx.Commit). RunInTransactionAsync
        // exposes the same boundary through where the failure happened.
        var callbackCompleted = false;
        try
        {
            await systems.RunInTransactionAsync(
                    async token =>
                    {
                        await InitializeInsideTransactionAsync(parameters, secretKey, token)
                            .ConfigureAwait(false);
                        callbackCompleted = true;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsStepFailure(ex))
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var prefix = callbackCompleted
                ? "failed to commit transaction: "
                : "failed to start transaction: ";
            throw new InvalidOperationException(prefix + ex.Message, ex);
        }
    }

    private async Task InitializeInsideTransactionAsync(
        InitializeSystemParameters parameters,
        string secretKey,
        CancellationToken cancellationToken)
    {
        // system.go:755-758 runs HashPassword inside the transaction.
        string hashedPassword;
        try
        {
            hashedPassword = passwordHasher.Hash(parameters.OwnerPassword);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw StepFailure(
                "failed to hash password: " + ex.Message,
                ex);
        }

        // system.go:761-764.
        var preferLanguage = string.IsNullOrEmpty(parameters.PreferLanguage)
            ? "en"
            : parameters.PreferLanguage;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // system.go:765-776. Ent create defaults: status "activated",
        // avatar nil, scopes the explicit ["*"].
        var owner = new UserRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            Email = parameters.OwnerEmail,
            Status = "activated",
            PreferLanguage = preferLanguage,
            Password = hashedPassword,
            FirstName = parameters.OwnerFirstName,
            LastName = parameters.OwnerLastName,
            IsOwner = true,
            Scopes = JsonSerializer.Serialize(OwnerScopes),
        };

        try
        {
            await users.CreateAsync(owner, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw StepFailure(
                "failed to create owner user: " + ex.Message,
                ex);
        }

        // system.go:780-792: ProjectService.CreateProject as the owner. The
        // reference runs Initialize under authz.WithSystemBypass
        // (system.go:723), so every ent privacy policy is skipped; the port
        // passes bypassPrivacy to mirror it. The service joins this transaction
        // because every store shares the scoped DbContext; its own "no
        // transaction" discipline matches the reference.
        try
        {
            await projects.CreateForUserAsync(
                    new CreateProjectCommand
                    {
                        Name = DefaultProjectName,
                        Description = "Default project",
                    },
                    owner.Id,
                    cancellationToken,
                    bypassPrivacy: true)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw StepFailure(
                "failed to create default project: " + ex.Message,
                ex);
        }

        // system.go:796-806.
        await SetSystemValueAsync(
                ManagementAuthService.SecretKeyKey,
                secretKey,
                "failed to set secret key: ",
                cancellationToken)
            .ConfigureAwait(false);
        await SetSystemValueAsync(
                BrandNameKey,
                parameters.BrandName,
                "failed to set brand name: ",
                cancellationToken)
            .ConfigureAwait(false);

        // system.go:808-819. Ent's SetSettings(&objects.DataStorageSettings{})
        // stores the all-null document; the port's "{}" decodes to the same
        // settings object (C196).
        long storageId;
        try
        {
            storageId = await dataStorages.CreateAsync(
                    new DataStorageRecord
                    {
                        CreatedAt = now,
                        UpdatedAt = now,
                        Name = PrimaryStorageName,
                        Description = "Primary database storage",
                        Primary = true,
                        Type = "database",
                        Settings = "{}",
                        Status = "active",
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw StepFailure(
                "failed to create primary data storage: " + ex.Message,
                ex);
        }

        // system.go:821-839.
        await SetSystemValueAsync(
                SystemSettingsAdminService.DefaultDataStorageIdKey,
                storageId.ToString(CultureInfo.InvariantCulture),
                "failed to set default data storage ID: ",
                cancellationToken)
            .ConfigureAwait(false);
        await SetSystemValueAsync(
                SystemSettingsAdminService.InitializedKey,
                "true",
                "failed to set initialized flag: ",
                cancellationToken)
            .ConfigureAwait(false);
        await SetSystemValueAsync(
                VersionKey,
                SystemVersionService.Version,
                "failed to set system version: ",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of setSystemValue (system.go:962-978): the upsert, with the
    /// reference's inner <c>failed to create system setting</c> wrapper.
    /// </summary>
    private async Task SetSystemValueAsync(
        string key,
        string value,
        string stepPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            await systems.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw StepFailure(
                stepPrefix + "failed to create system setting: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of biz.GenerateSecretKey (biz/auth.go:92-102).</summary>
    private static string GenerateSecretKey()
    {
        try
        {
            return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to generate random bytes: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Marks an error raised by an initialization step. A step already carries
    /// the reference's full error chain; the outer transaction wrapper must not
    /// re-prefix it. The marker lives in <see cref="Exception.Data"/> rather
    /// than a dedicated exception subtype so callers can catch the plain
    /// <see cref="InvalidOperationException"/> the reference's error chain
    /// surfaces (Go returns errors, not a distinct step type).
    /// </summary>
    private const string StepFailureMarker = "AxonHub.InitializationStepFailure";

    private static InvalidOperationException StepFailure(string message, Exception inner)
    {
        var error = new InvalidOperationException(message, inner);
        error.Data[StepFailureMarker] = true;
        return error;
    }

    private static bool IsStepFailure(Exception error)
        => error is InvalidOperationException
           && error.Data.Contains(StepFailureMarker);
}
