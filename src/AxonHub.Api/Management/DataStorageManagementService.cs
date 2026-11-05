using AxonHub.Api.GraphQL;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of ent.CreateDataStorageInput (ent.graphql:1671-1692).</summary>
public sealed record CreateDataStorageCommand
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>DataStorageType wire value (database, fs, s3, gcs, webdav).</summary>
    public required string Type { get; init; }

    public required DataStorageSettings Settings { get; init; }

    /// <summary>
    /// Accepted by the SDL but ignored: the reference always creates the row
    /// as active (data_storage.go:213-219).
    /// </summary>
    public string? Status { get; init; }
}

/// <summary>Port of ent.UpdateDataStorageInput (ent.graphql:6260-6277).</summary>
public sealed record UpdateDataStorageCommand
{
    public required long Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public DataStorageSettings? Settings { get; init; }

    /// <summary>DataStorageStatus wire value (active, archived).</summary>
    public string? Status { get; init; }
}

/// <summary>
/// Port of biz.DataStorageService's management surface
/// (internal/server/biz/data_storage.go:200-286 at f272549f). Every ent call in
/// the reference goes through a privacy-filtered client; the port evaluates the
/// same decisions explicitly before touching the store (C196).
/// </summary>
/// <remarks>
/// The reference also invalidates its in-memory ID/primary caches and rebuilds
/// the filesystem cache after a write; the port keeps no such caches (the
/// storage backends themselves are out of scope, C192), so the writes return
/// the stored row directly.
/// </remarks>
public sealed class DataStorageManagementService(
    IDataStorageStore storages,
    DataStoragePrivacy privacy)
{
    /// <summary>Ent's NotFoundError text for the DataStorage label.</summary>
    public const string DataStorageNotFound = "ent: data storage not found";

    /// <summary>The only values the ent enum accepts.</summary>
    public static readonly IReadOnlyList<string> DataStorageTypes =
        ["database", "fs", "s3", "gcs", "webdav"];

    /// <summary>The only values the ent enum accepts.</summary>
    public static readonly IReadOnlyList<string> DataStorageStatuses =
        ["active", "archived"];

    /// <summary>Port of DataStorageService.CreateDataStorage (data_storage.go:200-229).</summary>
    public async Task<DataStorageRecord> CreateAsync(
        CreateDataStorageCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The duplicate check runs through the privacy-filtered client, so a
        // query deny is wrapped by the reference's error text.
        await EvaluateQueryAsync("failed to check data storage name uniqueness: ", cancellationToken)
            .ConfigureAwait(false);

        bool exists;
        try
        {
            exists = await storages.ExistsByNameAsync(command.Name, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to check data storage name uniqueness: ", ex);
        }

        if (exists)
        {
            throw DuplicateName(command.Name);
        }

        // Ent evaluates the mutation policy inside Save.
        await EvaluateMutationAsync("failed to create data storage: ", cancellationToken)
            .ConfigureAwait(false);

        var record = new DataStorageRecord
        {
            Name = command.Name,
            Description = command.Description,
            Type = command.Type,
            Settings = DataStorageSettingsCodec.Serialize(command.Settings),
            // Create forces these regardless of the input status.
            Primary = false,
            Status = "active",
        };

        try
        {
            await storages.CreateAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to create data storage: ", ex);
        }

        return record;
    }

    /// <summary>Port of DataStorageService.UpdateDataStorage (data_storage.go:231-286).</summary>
    public async Task<DataStorageRecord> UpdateAsync(
        UpdateDataStorageCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The existing row is loaded through the privacy-filtered client.
        await EvaluateQueryAsync("failed to get data storage: ", cancellationToken)
            .ConfigureAwait(false);

        DataStorageRecord? existing;
        try
        {
            existing = await storages.GetByIdAsync(command.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to get data storage: ", ex);
        }

        if (existing is null)
        {
            throw Wrap("failed to get data storage: ", NotFound());
        }

        if (command.Name is not null && !string.Equals(command.Name, existing.Name, StringComparison.Ordinal))
        {
            await EvaluateQueryAsync(
                    "failed to check data storage name uniqueness: ",
                    cancellationToken)
                .ConfigureAwait(false);

            bool exists;
            try
            {
                exists = await storages.ExistsByNameAsync(
                        command.Name,
                        command.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Wrap("failed to check data storage name uniqueness: ", ex);
            }

            if (exists)
            {
                throw DuplicateName(command.Name);
            }
        }

        // Settings are merged with the stored ones so an empty credential keeps
        // the current value (mergeSettings, data_storage.go:763-859).
        var settings = command.Settings is null
            ? null
            : DataStorageSettingsCodec.Serialize(
                DataStorageSettingsCodec.Merge(
                    DataStorageSettingsCodec.Deserialize(existing.Settings),
                    command.Settings));

        // Ent evaluates the mutation policy inside Save.
        await EvaluateMutationAsync("failed to update data storage: ", cancellationToken)
            .ConfigureAwait(false);

        DataStorageRecord? updated;
        try
        {
            updated = await storages.UpdateAsync(
                    new DataStorageUpdate
                    {
                        Id = command.Id,
                        Name = command.Name,
                        Description = command.Description,
                        Settings = settings,
                        Status = command.Status,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to update data storage: ", ex);
        }

        return updated ?? throw Wrap("failed to update data storage: ", NotFound());
    }

    /// <summary>Port of the entgql DataStorage connection (ent.resolvers.go:400-412).</summary>
    public async Task<ListPage<DataStorageRecord>> ListAsync(
        DataStorageListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);
        return await storages.ListAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of the entgql Node lookup for DataStorage (privacy applies).</summary>
    public async Task<DataStorageRecord> GetNodeAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);
        var record = await storages.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return record ?? throw new InvalidOperationException(DataStorageNotFound);
    }

    private async Task EvaluateQueryAsync(
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap(errorPrefix, ex);
        }
    }

    private async Task EvaluateMutationAsync(
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            await privacy.EvaluateMutationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap(errorPrefix, ex);
        }
    }

    private static InvalidOperationException Wrap(string prefix, Exception exception)
        => new(prefix + exception.Message, exception);

    private static InvalidOperationException NotFound()
        => new(DataStorageNotFound);

    private static CodedException DuplicateName(string name)
        => new(
            CodedException.DuplicateName,
            "data storage name '" + name + "' already exists",
            new Dictionary<string, object?>
            {
                ["resource"] = "data storage",
                ["field"] = "name",
                ["value"] = name,
            });
}
