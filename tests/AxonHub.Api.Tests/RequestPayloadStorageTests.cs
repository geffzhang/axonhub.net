using System.Text;
using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Behavioural tests for RequestPayloadStorage: the storage-policy gates, the
/// default/primary/by-id lookups, SaveData/LoadData/DeleteData, the external
/// response-body write with rollback, and the loaders that back the GraphQL
/// request/execution payload fields (request.go:1369-1683).
/// </summary>
public sealed class RequestPayloadStorageTests
{
    private static readonly DateTime UpdatedAt = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeDataStorageStore _dataStorages = new();
    private readonly FakeSystemStore _systems = new();
    private readonly FakeDataStorageBackends _backends = new();
    private readonly FakeRequestStore _requests = new();

    private RequestPayloadStorage NewStorage()
        => new(_dataStorages, new SystemSettingsAdminService(_systems), _backends);

    private DataStorageRecord SeedStorage(long id, string type = "database", bool primary = false)
        => _dataStorages.Add(new DataStorageRecord { Id = id, Type = type, Primary = primary });

    private void SetPolicy(StoragePolicy policy)
        => _systems.Add(StoragePolicyCodec.Key, StoragePolicyCodec.Serialize(policy));

    [Fact]
    public async Task StoragePolicyGatesUseTheReferenceDefaults()
    {
        var payloads = NewStorage();

        Assert.True(await payloads.GetStoreRequestBodyAsync());
        Assert.True(await payloads.GetStoreResponseBodyAsync());
        Assert.False(await payloads.GetStoreChunksAsync());

        // A missing row is not a failure: the service returns the default.
        Assert.Same(StoragePolicy.Default, await payloads.TryGetStoragePolicyAsync());
    }

    [Fact]
    public async Task StoragePolicyRowOverridesEveryGate()
    {
        SetPolicy(new StoragePolicy
        {
            StoreChunks = true,
            StoreRequestBody = false,
            StoreResponseBody = false,
        });
        var payloads = NewStorage();

        Assert.False(await payloads.GetStoreRequestBodyAsync());
        Assert.False(await payloads.GetStoreResponseBodyAsync());
        Assert.True(await payloads.GetStoreChunksAsync());
    }

    [Fact]
    public async Task StoragePolicyReadFailureKeepsTheReferenceDefaults()
    {
        _systems.GetFailuresByKey[StoragePolicyCodec.Key] = new InvalidOperationException("boom");
        var payloads = NewStorage();

        Assert.Null(await payloads.TryGetStoragePolicyAsync());
        Assert.True(await payloads.GetStoreRequestBodyAsync());
        Assert.True(await payloads.GetStoreResponseBodyAsync());
        Assert.False(await payloads.GetStoreChunksAsync());
    }

    [Fact]
    public async Task StoragePolicyCancellationIsNotSwallowed()
    {
        _systems.GetFailuresByKey[StoragePolicyCodec.Key] = new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewStorage().GetStoreChunksAsync());
    }

    [Fact]
    public async Task DefaultStoragePrefersTheConfiguredId()
    {
        SeedStorage(1, primary: true);
        var configured = SeedStorage(2, type: "fs");
        _systems.Add("default_data_storage_id", "2");

        Assert.Same(configured, await NewStorage().GetDefaultDataStorageAsync());
    }

    [Fact]
    public async Task DefaultStorageFallsBackToPrimaryWhenUnsetOrMissing()
    {
        var primary = SeedStorage(1, primary: true);
        var payloads = NewStorage();

        Assert.Same(primary, await payloads.GetDefaultDataStorageAsync());

        _systems.Add("default_data_storage_id", "99");
        Assert.Same(primary, await payloads.GetDefaultDataStorageAsync());
    }

    [Fact]
    public async Task DefaultStorageWithoutAnyStorageFails()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewStorage().GetDefaultDataStorageAsync());

        Assert.Equal(
            "failed to get primary data storage: ent: data storage not found",
            error.Message);
    }

    [Fact]
    public async Task DefaultStorageWrapsAParseFailure()
    {
        _systems.Add("default_data_storage_id", "abc");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewStorage().GetDefaultDataStorageAsync());

        Assert.Equal(
            "failed to get default data storage ID: "
            + "failed to parse default data storage ID: expected integer",
            error.Message);
    }

    [Fact]
    public async Task DefaultStorageWrapsTheSettingReadFailure()
    {
        _systems.GetFailuresByKey["default_data_storage_id"] = new InvalidOperationException("db down");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewStorage().GetDefaultDataStorageAsync());

        Assert.Equal(
            "failed to get default data storage ID: "
            + "failed to get default data storage ID: failed to get system value: db down",
            error.Message);
    }

    [Fact]
    public async Task DataStorageLookupByIdUsesTheReferenceTexts()
    {
        var storage = SeedStorage(5, type: "fs");
        var payloads = NewStorage();

        Assert.Same(storage, await payloads.GetDataStorageByIdAsync(5));

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => payloads.GetDataStorageByIdAsync(6));
        Assert.Equal(
            "failed to get data storage by ID 6: ent: data storage not found",
            missing.Message);
    }

    [Fact]
    public async Task NullOrZeroStorageIdMeansPrimary()
    {
        var primary = SeedStorage(1, primary: true);
        var payloads = NewStorage();

        Assert.Same(primary, await payloads.GetDataStorageAsync(null));
        Assert.Same(primary, await payloads.GetDataStorageAsync(0));

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => payloads.GetDataStorageAsync(7));
        Assert.Equal(
            "failed to get data storage by ID 7: ent: data storage not found",
            missing.Message);
    }

    [Fact]
    public void ExternalStorageMeansAnyNonPrimaryRow()
    {
        Assert.False(RequestPayloadStorage.ShouldUseExternalStorage(null));
        Assert.False(RequestPayloadStorage.ShouldUseExternalStorage(
            new DataStorageRecord { Primary = true, Type = "fs" }));
        Assert.True(RequestPayloadStorage.ShouldUseExternalStorage(
            new DataStorageRecord { Primary = false, Type = "database" }));
    }

    [Fact]
    public async Task DatabaseStorageKeepsTheDataInTheKey()
    {
        var storage = new DataStorageRecord { Id = 1, Type = "database", Primary = true };
        var payloads = NewStorage();

        await payloads.SaveDataAsync(storage, "{\"a\":1}", "{\"a\":1}"u8.ToArray());
        await payloads.DeleteDataAsync(storage, "{\"a\":1}");

        // The row itself is the storage: no backend is ever built.
        Assert.Empty(_backends.Created);
        Assert.Empty(_backends.Backend.Writes);
        Assert.Empty(_backends.Backend.Deletes);

        Assert.Equal(
            "{\"a\":1}",
            Encoding.UTF8.GetString(await payloads.LoadDataAsync(storage, "{\"a\":1}")));
    }

    [Fact]
    public async Task ExternalStorageDelegatesToTheBackend()
    {
        var storage = new DataStorageRecord { Id = 2, Type = "fs" };
        var payloads = NewStorage();

        await payloads.SaveDataAsync(storage, "/k", "v"u8.ToArray());
        Assert.Equal(new[] { "/k" }, _backends.Backend.Writes);
        Assert.Same(storage, _backends.LastStorage);

        Assert.Equal(
            "v",
            Encoding.UTF8.GetString(await payloads.LoadDataAsync(storage, "/k")));

        await payloads.DeleteDataAsync(storage, "/k");
        Assert.Equal(new[] { "/k" }, _backends.Backend.Deletes);
    }

    [Fact]
    public async Task ResponseBodyStaysInTheColumnWithoutExternalStorage()
    {
        var payloads = NewStorage();

        var none = await payloads.WriteResponseBodyAsync(null, "/k", "{\"a\":1}");
        Assert.Equal(new ResponseBodyWrite(true, "{\"a\":1}", string.Empty), none);

        var primary = new DataStorageRecord { Id = 1, Type = "database", Primary = true };
        var database = await payloads.WriteResponseBodyAsync(primary, "/k", "{\"a\":1}");
        Assert.Equal(new ResponseBodyWrite(true, "{\"a\":1}", string.Empty), database);
        Assert.Empty(_backends.Created);
    }

    [Fact]
    public async Task ResponseBodyGoesToExternalStorageBehindTheMarker()
    {
        var storage = new DataStorageRecord { Id = 2, Type = "fs" };
        var payloads = NewStorage();

        var write = await payloads.WriteResponseBodyAsync(storage, "/k/response_body.json", "{\"a\":1}");

        Assert.True(write.SetBody);
        Assert.Equal("{\"_ext\":1}", write.DbValue);
        Assert.Equal("/k/response_body.json", write.ExternalKey);
        Assert.Equal(
            "{\"a\":1}",
            Encoding.UTF8.GetString(_backends.Backend.Objects["/k/response_body.json"]));
    }

    [Fact]
    public async Task FailedExternalWriteLeavesTheColumnUntouched()
    {
        _backends.Backend.WriteFailure = new IOException("disk full");
        var storage = new DataStorageRecord { Id = 2, Type = "fs" };

        var write = await NewStorage().WriteResponseBodyAsync(storage, "/k", "{}");

        Assert.False(write.SetBody);
        Assert.Null(write.DbValue);
        Assert.Equal(string.Empty, write.ExternalKey);
    }

    [Fact]
    public async Task NonPrimaryDatabaseStorageWritesTheMarkerWithoutSaving()
    {
        // shouldUseExternalStorage only checks Primary; SaveData then no-ops for
        // type=database. The row still gets the marker, like the reference.
        var storage = new DataStorageRecord { Id = 3, Type = "database", Primary = false };

        var write = await NewStorage().WriteResponseBodyAsync(storage, "/k", "{\"a\":1}");

        Assert.True(write.SetBody);
        Assert.Equal("{\"_ext\":1}", write.DbValue);
        Assert.Equal("/k", write.ExternalKey);
        Assert.Empty(_backends.Created);
    }

    [Fact]
    public async Task SnapshotWriteHonoursTheResponseBodyPolicy()
    {
        SetPolicy(StoragePolicy.Default with { StoreResponseBody = false });
        var row = _requests.Add(new RequestRecord
        {
            Id = 42,
            ProjectId = 7,
            ResponseBody = "{\"old\":1}",
        });

        await NewStorage().SetRequestStatusExternalIdAndResponseBodyAsync(
            _requests,
            row,
            "completed",
            "ext-1",
            "{\"new\":1}",
            UpdatedAt);

        var snapshot = Assert.Single(_requests.Snapshots);
        Assert.Equal(42, snapshot.Id);
        Assert.Equal("completed", snapshot.Status);
        Assert.Equal("ext-1", snapshot.ExternalId);
        Assert.Equal("{\"old\":1}", snapshot.ResponseBody);
        Assert.Equal("{\"old\":1}", row.ResponseBody);
        Assert.Empty(_backends.Created);
    }

    [Fact]
    public async Task SnapshotWriteStoresTheBodyInTheColumnForDatabaseStorage()
    {
        var row = _requests.Add(new RequestRecord { Id = 42, ProjectId = 7 });

        await NewStorage().SetRequestStatusExternalIdAndResponseBodyAsync(
            _requests,
            row,
            "completed",
            "ext-1",
            "{\"new\":1}",
            UpdatedAt);

        var snapshot = Assert.Single(_requests.Snapshots);
        Assert.Equal("{\"new\":1}", snapshot.ResponseBody);
        Assert.Equal("{\"new\":1}", row.ResponseBody);
        Assert.Equal(UpdatedAt, row.UpdatedAt);
        Assert.Empty(_backends.Created);
    }

    [Fact]
    public async Task SnapshotWriteStoresTheBodyExternallyAndMarksTheRow()
    {
        SeedStorage(9, type: "fs");
        var row = _requests.Add(new RequestRecord { Id = 42, ProjectId = 7, DataStorageId = 9 });

        await NewStorage().SetRequestStatusExternalIdAndResponseBodyAsync(
            _requests,
            row,
            "completed",
            "ext-1",
            "{\"new\":1}",
            UpdatedAt);

        Assert.Equal("{\"_ext\":1}", row.ResponseBody);
        Assert.Equal(
            "{\"new\":1}",
            Encoding.UTF8.GetString(
                _backends.Backend.Objects["/7/requests/42/response_body.json"]));
    }

    [Fact]
    public async Task FailedSnapshotWriteRollsBackTheExternalObject()
    {
        SeedStorage(9, type: "fs");
        var row = _requests.Add(new RequestRecord { Id = 42, ProjectId = 7, DataStorageId = 9 });
        _requests.ThrowOnSnapshot = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewStorage().SetRequestStatusExternalIdAndResponseBodyAsync(
                _requests,
                row,
                "completed",
                "ext-1",
                "{\"new\":1}",
                UpdatedAt));

        Assert.Equal(new[] { "/7/requests/42/response_body.json" }, _backends.Backend.Deletes);
        Assert.False(_backends.Backend.Objects.ContainsKey("/7/requests/42/response_body.json"));
    }

    [Fact]
    public async Task RollbackFailureDoesNotMaskTheUpdateError()
    {
        SeedStorage(9, type: "fs");
        var row = _requests.Add(new RequestRecord { Id = 42, ProjectId = 7, DataStorageId = 9 });
        _requests.ThrowOnSnapshot = true;
        _backends.Backend.DeleteFailure = new IOException("delete failed");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewStorage().SetRequestStatusExternalIdAndResponseBodyAsync(
                _requests,
                row,
                "completed",
                "ext-1",
                "{\"new\":1}",
                UpdatedAt));

        Assert.Equal("snapshot write failed", error.Message);
    }

    [Fact]
    public async Task TryStorageLookupSwallowsFailuresAndIgnoresMissingIds()
    {
        var payloads = NewStorage();
        Assert.Null(await payloads.TryGetDataStorageAsync(null));
        Assert.Null(await payloads.TryGetDataStorageAsync(0));

        var storage = SeedStorage(3, type: "fs");
        Assert.Same(storage, await payloads.TryGetDataStorageAsync(3));

        _dataStorages.GetByIdFailure = new InvalidOperationException("db down");
        Assert.Null(await payloads.TryGetDataStorageAsync(3));
    }

    [Fact]
    public async Task RequestBodyFallsBackToTheColumnForDatabaseStorage()
    {
        SeedStorage(1, primary: true);
        var payloads = NewStorage();
        var row = new RequestRecord
        {
            Id = 42,
            ProjectId = 7,
            RequestBody = "{\"model\":\"m\"}",
        };

        Assert.Equal("{\"model\":\"m\"}", await payloads.LoadRequestBodyAsync(row));

        row.RequestBody = string.Empty;
        Assert.Equal("{}", await payloads.LoadRequestBodyAsync(row));
    }

    [Fact]
    public async Task RequestBodyLoadsTheExternalObject()
    {
        SeedStorage(9, type: "fs");
        var payloads = NewStorage();
        var row = new RequestRecord
        {
            Id = 42,
            ProjectId = 7,
            DataStorageId = 9,
            RequestBody = "{\"column\":true}",
        };
        const string key = "/7/requests/42/request_body.json";

        _backends.Backend.Add(key, "{\"x\":1}"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.Equal("{\"x\":1}", await payloads.LoadRequestBodyAsync(row));

        _backends.Backend.Add(key, "not-json"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.Equal("{}", await payloads.LoadRequestBodyAsync(row));

        _backends.Backend.ReadFailure = new IOException("gone");
        Assert.Equal("{}", await payloads.LoadRequestBodyAsync(row));
    }

    [Fact]
    public async Task RequestBodyIsEmptyWhenTheStorageLookupFails()
    {
        var row = new RequestRecord
        {
            Id = 42,
            ProjectId = 7,
            DataStorageId = 9,
            RequestBody = "{\"column\":true}",
        };

        Assert.Equal("{}", await NewStorage().LoadRequestBodyAsync(row));
    }

    [Fact]
    public async Task ResponseBodyIsEmptyForUnfinishedRequests()
    {
        var row = new RequestRecord
        {
            Id = 42,
            Status = "processing",
            ResponseBody = "{\"a\":1}",
        };

        Assert.Equal("{}", await NewStorage().LoadResponseBodyAsync(row));
    }

    [Fact]
    public async Task ResponseBodyCollapsesTheExternalMarker()
    {
        SeedStorage(1, primary: true);
        var payloads = NewStorage();

        var marker = new RequestRecord
        {
            Id = 42,
            Status = "completed",
            ResponseBody = "{ \"_ext\" : 1 }",
        };
        Assert.Equal("{}", await payloads.LoadResponseBodyAsync(marker));

        var stored = new RequestRecord
        {
            Id = 42,
            Status = "completed",
            ResponseBody = "{\"a\":1}",
        };
        Assert.Equal("{\"a\":1}", await payloads.LoadResponseBodyAsync(stored));

        var empty = new RequestRecord { Id = 42, Status = "completed" };
        Assert.Equal("{}", await payloads.LoadResponseBodyAsync(empty));
    }

    [Fact]
    public async Task ResponseBodyLoadsTheExternalObject()
    {
        SeedStorage(9, type: "fs");
        var payloads = NewStorage();
        var row = new RequestRecord
        {
            Id = 42,
            ProjectId = 7,
            Status = "completed",
            DataStorageId = 9,
        };
        const string key = "/7/requests/42/response_body.json";

        _backends.Backend.Add(key, "{\"ok\":true}"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.Equal("{\"ok\":true}", await payloads.LoadResponseBodyAsync(row));

        _backends.Backend.Add(key, "bad"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.Equal("{}", await payloads.LoadResponseBodyAsync(row));
    }

    [Fact]
    public async Task RequestChunksRequireAFinishedStream()
    {
        SeedStorage(1, primary: true);
        var payloads = NewStorage();

        Assert.Empty(await payloads.LoadResponseChunksAsync(new RequestRecord { Id = 42 }));

        var processing = new RequestRecord { Id = 42, Stream = true, Status = "processing" };
        Assert.Empty(await payloads.LoadResponseChunksAsync(processing));

        var completed = new RequestRecord { Id = 42, Stream = true, Status = "completed" };
        Assert.Empty(await payloads.LoadResponseChunksAsync(completed));
    }

    [Fact]
    public async Task RequestChunksComeFromTheColumnAndCollapseTheMarker()
    {
        SeedStorage(1, primary: true);
        var payloads = NewStorage();
        var row = new RequestRecord
        {
            Id = 42,
            Stream = true,
            Status = "failed",
            ResponseChunks = "[{\"_ext\":1}]",
        };

        Assert.Empty(await payloads.LoadResponseChunksAsync(row));

        row.ResponseChunks = "[{\"a\":1},{\"b\":2}]";
        Assert.Equal(
            new[] { "{\"a\":1}", "{\"b\":2}" },
            await payloads.LoadResponseChunksAsync(row));

        row.ResponseChunks = "{}";
        Assert.Empty(await payloads.LoadResponseChunksAsync(row));
    }

    [Fact]
    public async Task RequestChunksFallBackToTheColumnWhenStorageLookupFails()
    {
        var row = new RequestRecord
        {
            Id = 42,
            ProjectId = 7,
            Stream = true,
            Status = "completed",
            DataStorageId = 9,
            ResponseChunks = "[{\"a\":1}]",
        };

        Assert.Equal(
            new[] { "{\"a\":1}" },
            await NewStorage().LoadResponseChunksAsync(row));
    }

    [Fact]
    public async Task RequestChunksLoadFromExternalStorage()
    {
        SeedStorage(9, type: "fs");
        var payloads = NewStorage();
        var row = new RequestRecord
        {
            Id = 42,
            ProjectId = 7,
            Stream = true,
            Status = "completed",
            DataStorageId = 9,
            ResponseChunks = "[{\"column\":true}]",
        };
        const string key = "/7/requests/42/response_chunks.json";

        _backends.Backend.Add(key, "[{\"a\":1},{\"b\":2}]"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.Equal(
            new[] { "{\"a\":1}", "{\"b\":2}" },
            await payloads.LoadResponseChunksAsync(row));

        _backends.Backend.Add(key, "{}"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.Empty(await payloads.LoadResponseChunksAsync(row));

        _backends.Backend.Add(key, "bad"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.Empty(await payloads.LoadResponseChunksAsync(row));
    }

    [Fact]
    public async Task ExecutionRequestBodyFallsBackToTheColumnForDatabaseStorage()
    {
        SeedStorage(1, primary: true);
        var payloads = NewStorage();
        var row = new RequestExecutionRecord
        {
            Id = 5,
            RequestId = 42,
            ProjectId = 7,
            RequestBody = "{\"m\":1}",
        };

        Assert.Equal("{\"m\":1}", await payloads.LoadExecutionRequestBodyAsync(row));

        row.RequestBody = string.Empty;
        Assert.Equal("{}", await payloads.LoadExecutionRequestBodyAsync(row));
    }

    [Fact]
    public async Task ExecutionRequestBodyLoadsTheExternalObject()
    {
        SeedStorage(9, type: "fs");
        var payloads = NewStorage();
        var row = new RequestExecutionRecord
        {
            Id = 5,
            RequestId = 42,
            ProjectId = 7,
            DataStorageId = 9,
        };
        const string key = "/7/requests/42/executions/5/request_body.json";

        _backends.Backend.Add(key, "{\"x\":1}"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.Equal("{\"x\":1}", await payloads.LoadExecutionRequestBodyAsync(row));

        _backends.Backend.Add(key, "not-json"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.Equal("{}", await payloads.LoadExecutionRequestBodyAsync(row));
    }

    [Fact]
    public async Task ExecutionResponseBodyRequiresCompletedStatus()
    {
        SeedStorage(1, primary: true);
        var payloads = NewStorage();

        var failed = new RequestExecutionRecord
        {
            Id = 5,
            Status = "failed",
            ResponseBody = "{\"a\":1}",
        };
        Assert.Equal("{}", await payloads.LoadExecutionResponseBodyAsync(failed));

        var marker = new RequestExecutionRecord
        {
            Id = 5,
            Status = "completed",
            ResponseBody = "{\"_ext\":1}",
        };
        Assert.Equal("{}", await payloads.LoadExecutionResponseBodyAsync(marker));

        var stored = new RequestExecutionRecord
        {
            Id = 5,
            Status = "completed",
            ResponseBody = "{\"a\":1}",
        };
        Assert.Equal("{\"a\":1}", await payloads.LoadExecutionResponseBodyAsync(stored));
    }

    [Fact]
    public async Task ExecutionChunksRequireAFinishedStream()
    {
        SeedStorage(1, primary: true);
        var payloads = NewStorage();

        Assert.Empty(await payloads.LoadExecutionResponseChunksAsync(
            new RequestExecutionRecord { Id = 5 }));

        var processing = new RequestExecutionRecord
        {
            Id = 5,
            Stream = true,
            Status = "processing",
        };
        Assert.Empty(await payloads.LoadExecutionResponseChunksAsync(processing));
    }

    [Fact]
    public async Task ExecutionChunksFallBackToTheColumnForDatabaseStorage()
    {
        SeedStorage(1, primary: true);
        var row = new RequestExecutionRecord
        {
            Id = 5,
            Stream = true,
            Status = "failed",
            ResponseChunks = "[{\"_ext\":1}]",
        };
        var payloads = NewStorage();

        Assert.Empty(await payloads.LoadExecutionResponseChunksAsync(row));

        row.ResponseChunks = "[{\"a\":1}]";
        Assert.Equal(
            new[] { "{\"a\":1}" },
            await payloads.LoadExecutionResponseChunksAsync(row));
    }

    [Fact]
    public async Task ExecutionChunksLoadFromExternalStorageWithJsonValidation()
    {
        SeedStorage(9, type: "fs");
        var payloads = NewStorage();
        var row = new RequestExecutionRecord
        {
            Id = 5,
            RequestId = 42,
            ProjectId = 7,
            Stream = true,
            Status = "canceled",
            DataStorageId = 9,
        };
        const string key = "/7/requests/42/executions/5/response_chunks.json";

        _backends.Backend.Add(key, "[{\"a\":1}]"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.Equal(
            new[] { "{\"a\":1}" },
            await payloads.LoadExecutionResponseChunksAsync(row));

        _backends.Backend.Add(key, "bad"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.Empty(await payloads.LoadExecutionResponseChunksAsync(row));

        _backends.Backend.Add(key, "{}"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.Empty(await payloads.LoadExecutionResponseChunksAsync(row));

        _backends.Backend.Add(key, "[]"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.Empty(await payloads.LoadExecutionResponseChunksAsync(row));
    }

    [Fact]
    public void ExternalMarkerDetectionIgnoresWhitespace()
    {
        Assert.True(RequestPayloadStorage.IsExternalResponseBodyMarker("{\"_ext\":1}"));
        Assert.True(RequestPayloadStorage.IsExternalResponseBodyMarker("{ \"_ext\" : 1 }"));
        Assert.False(RequestPayloadStorage.IsExternalResponseBodyMarker("{\"_ext\":2}"));
        Assert.False(RequestPayloadStorage.IsExternalResponseBodyMarker("[]"));
    }

    [Fact]
    public void SanitizeBodyCollapsesTheMarkerAndEmptyValues()
    {
        Assert.Equal("{}", RequestPayloadStorage.SanitizeLoadedResponseBody(null));
        Assert.Equal("{}", RequestPayloadStorage.SanitizeLoadedResponseBody(string.Empty));
        Assert.Equal("{}", RequestPayloadStorage.SanitizeLoadedResponseBody("{\"_ext\":1}"));
        Assert.Equal("{\"a\":1}", RequestPayloadStorage.SanitizeLoadedResponseBody("{\"a\":1}"));
    }

    [Fact]
    public void SanitizeChunksCollapsesASingleMarker()
    {
        Assert.Empty(RequestPayloadStorage.SanitizeLoadedResponseChunks(null));
        Assert.Empty(RequestPayloadStorage.SanitizeLoadedResponseChunks(string.Empty));
        Assert.Empty(RequestPayloadStorage.SanitizeLoadedResponseChunks("[{\"_ext\":1}]"));
        Assert.Empty(RequestPayloadStorage.SanitizeLoadedResponseChunks("{}"));
        Assert.Equal(
            new[] { "{\"a\":1}", "{\"b\":2}" },
            RequestPayloadStorage.SanitizeLoadedResponseChunks("[{\"a\":1},{\"b\":2}]"));
    }

    [Fact]
    public void MarkersMatchTheReference()
    {
        Assert.Equal("{}", RequestPayloadStorage.EmptyJson);
        Assert.Equal("{\"_ext\":1}", RequestPayloadStorage.ExternalResponseBodyMarker);
        Assert.Equal("{\"message\":\"invalid text\"}", RequestPayloadStorage.InvalidRequestBodyJson);
    }
}
