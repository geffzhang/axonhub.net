using AxonHub.Api.Settings;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the storage-policy half of SystemService (internal/server/biz/system.go:
/// 985-1044, system_default.go:11-77): the merged default cleanup list, the
/// backward-compatibility body flags, the Go-shaped JSON document and the
/// positive-days write validation.
/// </summary>
public sealed class StoragePolicyTests
{
    [Fact]
    public void MissingRowReturnsTheReferenceDefaultPolicy()
    {
        var policy = StoragePolicy.Default;

        Assert.False(policy.StoreChunks);
        Assert.False(policy.LivePreview);
        Assert.True(policy.StoreRequestBody);
        Assert.True(policy.StoreResponseBody);

        Assert.Equal(
            [
                new CleanupOption(CleanupResourceTypes.Requests, false, 3),
                new CleanupOption(CleanupResourceTypes.UsageLogs, false, 30),
                new CleanupOption(CleanupResourceTypes.RequestBodies, false, 7),
                new CleanupOption(CleanupResourceTypes.ResponseBodies, false, 7),
                new CleanupOption(CleanupResourceTypes.ResponseChunks, false, 3),
            ],
            policy.CleanupOptions);
    }

    [Fact]
    public void MergeKeepsFirstSeenOrderAndAppendsMissingDefaults()
    {
        var merged = StoragePolicy.MergeCleanupOptions(
        [
            new CleanupOption(CleanupResourceTypes.ResponseChunks, true, 9),
            new CleanupOption("custom", true, 2),
            new CleanupOption(CleanupResourceTypes.ResponseChunks, false, 4),
        ]);

        // The duplicate keeps its first-seen position but the later value wins;
        // unknown resource types survive and the missing defaults follow.
        Assert.Equal(
            [
                new CleanupOption(CleanupResourceTypes.ResponseChunks, false, 4),
                new CleanupOption("custom", true, 2),
                new CleanupOption(CleanupResourceTypes.Requests, false, 3),
                new CleanupOption(CleanupResourceTypes.UsageLogs, false, 30),
                new CleanupOption(CleanupResourceTypes.RequestBodies, false, 7),
                new CleanupOption(CleanupResourceTypes.ResponseBodies, false, 7),
            ],
            merged);
    }

    [Fact]
    public void DeserializeDefaultsAbsentBodyFlagsToTrue()
    {
        var policy = StoragePolicyCodec.Deserialize("""{"store_chunks":true}""");

        Assert.True(policy.StoreChunks);
        Assert.False(policy.LivePreview);
        Assert.True(policy.StoreRequestBody);
        Assert.True(policy.StoreResponseBody);
        Assert.Equal(StoragePolicy.DefaultCleanupOptions(), policy.CleanupOptions);
    }

    [Fact]
    public void DeserializeHonoursExplicitFalseBodyFlags()
    {
        var policy = StoragePolicyCodec.Deserialize(
            """{"store_request_body":false,"store_response_body":false}""");

        Assert.False(policy.StoreRequestBody);
        Assert.False(policy.StoreResponseBody);
    }

    [Fact]
    public void DeserializeMergesStoredCleanupOptionsWithTheDefaults()
    {
        var policy = StoragePolicyCodec.Deserialize(
            """
            {"cleanup_options":[{"resource_type":"requests","enabled":true,"cleanup_days":11}]}
            """);

        var option = policy.CleanupOptions!
            .Single(static candidate => candidate.ResourceType == CleanupResourceTypes.Requests);
        Assert.True(option.Enabled);
        Assert.Equal(11, option.CleanupDays);

        // All five defaults still exist, in stored-first order.
        Assert.Equal(
            [
                CleanupResourceTypes.Requests,
                CleanupResourceTypes.UsageLogs,
                CleanupResourceTypes.RequestBodies,
                CleanupResourceTypes.ResponseBodies,
                CleanupResourceTypes.ResponseChunks,
            ],
            policy.CleanupOptions!.Select(static candidate => candidate.ResourceType).ToArray());
    }

    [Fact]
    public void SerializeWritesTheGoFieldOrderAndSnakeCaseNames()
    {
        var json = StoragePolicyCodec.Serialize(StoragePolicy.Default);

        Assert.Equal(
            """
            {"store_chunks":false,"live_preview":false,"store_request_body":true,"store_response_body":true,"cleanup_options":[{"resource_type":"requests","enabled":false,"cleanup_days":3},{"resource_type":"usage_logs","enabled":false,"cleanup_days":30},{"resource_type":"request_bodies","enabled":false,"cleanup_days":7},{"resource_type":"response_bodies","enabled":false,"cleanup_days":7},{"resource_type":"response_chunks","enabled":false,"cleanup_days":3}]}
            """,
            json);
    }

    [Fact]
    public void SerializeThenDeserializeRoundTrips()
    {
        var policy = new StoragePolicy
        {
            StoreChunks = true,
            LivePreview = true,
            StoreRequestBody = false,
            StoreResponseBody = true,
            CleanupOptions =
            [
                new CleanupOption(CleanupResourceTypes.UsageLogs, true, 12),
            ],
        };

        var roundTripped = StoragePolicyCodec.Deserialize(StoragePolicyCodec.Serialize(policy));

        Assert.True(roundTripped.StoreChunks);
        Assert.True(roundTripped.LivePreview);
        Assert.False(roundTripped.StoreRequestBody);
        Assert.True(roundTripped.StoreResponseBody);
        Assert.Equal(StoragePolicy.MergeCleanupOptions(policy.CleanupOptions), roundTripped.CleanupOptions);
    }

    [Fact]
    public async Task GetStoragePolicyReturnsTheStoredDocument()
    {
        var store = new FakeSystemStore();
        store.Add(
            StoragePolicyCodec.Key,
            """{"store_chunks":true,"live_preview":true,"store_request_body":false,"store_response_body":true,"cleanup_options":[]}""");

        var policy = await new SystemSettingsAdminService(store).GetStoragePolicyAsync();

        Assert.True(policy.StoreChunks);
        Assert.True(policy.LivePreview);
        Assert.False(policy.StoreRequestBody);
        Assert.Equal(5, policy.CleanupOptions!.Count);
    }

    [Fact]
    public async Task GetStoragePolicyLookupFailureKeepsTheWrapperChain()
    {
        var store = new FakeSystemStore
        {
            GetFailure = new InvalidOperationException("database is down"),
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SystemSettingsAdminService(store).GetStoragePolicyAsync());

        Assert.Equal(
            "failed to get storage policy: failed to get system value: database is down",
            error.Message);
    }

    [Fact]
    public async Task MalformedStoragePolicyIsReportedWithoutFallingBack()
    {
        var store = new FakeSystemStore();
        store.Add(StoragePolicyCodec.Key, """{"cleanup_options":"nope"}""");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SystemSettingsAdminService(store).GetStoragePolicyAsync());

        Assert.StartsWith(
            "failed to unmarshal storage policy: ",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetStoragePolicyRejectsNonPositiveCleanupDays()
    {
        var store = new FakeSystemStore();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SystemSettingsAdminService(store).SetStoragePolicyAsync(new StoragePolicy
            {
                CleanupOptions =
                [
                    new CleanupOption(CleanupResourceTypes.Requests, false, 0),
                ],
            }));

        Assert.Equal(
            "cleanup_days for \"requests\" must be positive; set enabled=false to keep data forever",
            error.Message);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task SetStoragePolicyPersistsTheSerializedDocument()
    {
        var store = new FakeSystemStore();
        var policy = new StoragePolicy
        {
            StoreChunks = true,
            StoreRequestBody = true,
            StoreResponseBody = true,
            CleanupOptions =
            [
                new CleanupOption(CleanupResourceTypes.Requests, true, 4),
            ],
        };

        await new SystemSettingsAdminService(store).SetStoragePolicyAsync(policy);

        Assert.Equal(
            StoragePolicyCodec.Serialize(policy),
            Assert.Single(store.Rows).Value);
    }

    [Fact]
    public async Task SetStoragePolicyWriteFailureKeepsTheWrapperChain()
    {
        var store = new FakeSystemStore
        {
            SetFailure = new InvalidOperationException("write rejected"),
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SystemSettingsAdminService(store).SetStoragePolicyAsync(new StoragePolicy
            {
                CleanupOptions =
                [
                    new CleanupOption(CleanupResourceTypes.Requests, true, 1),
                ],
            }));

        Assert.Equal(
            "failed to create system setting: write rejected",
            error.Message);
    }
}
