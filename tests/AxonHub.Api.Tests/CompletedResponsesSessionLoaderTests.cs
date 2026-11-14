using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of biz/request_responses_session_test.go: the continuation loader is
/// scoped to the authenticated API key and project, accepts both Responses
/// formats and rejects empty or invalid stored bodies.
/// </summary>
public sealed class CompletedResponsesSessionLoaderTests
{
    private const long OwnerKeyId = 21;
    private const long OtherKeyId = 22;
    private const long ProjectId = 31;

    private readonly FakeRequestStore _requests = new();
    private readonly FakeDataStorageStore _storages = new();
    private readonly CompletedResponsesSessionLoader _loader;

    public CompletedResponsesSessionLoaderTests()
    {
        _storages.Seed(storage =>
        {
            storage.Name = "primary";
            storage.Primary = true;
            storage.Type = "database";
            storage.Status = "active";
        });

        _loader = new CompletedResponsesSessionLoader(
            _requests,
            new RequestPayloadStorage(
                _storages,
                new FakeGcSettingsService(),
                new FakeDataStorageBackends()));
    }

    private static RequestIdentity IdentityFor(long apiKeyId, long projectId = ProjectId)
        => new(projectId, apiKeyId)
        {
            Project = new ProjectRecord { Id = projectId },
        };

    private RequestRecord AddSession(
        long apiKeyId,
        string format,
        string responseId,
        string marker,
        string? requestBody = null,
        string? responseBody = null,
        string status = "completed",
        DateTime? createdAt = null)
    {
        var row = new RequestRecord
        {
            ApiKeyId = apiKeyId,
            ProjectId = ProjectId,
            ModelId = "gpt-5",
            Format = format,
            RequestBody = requestBody ?? """{"model":"gpt-5","input":"MARKER"}""".Replace("MARKER", marker, StringComparison.Ordinal),
            ResponseBody = responseBody ?? """{"id":"ID","status":"completed","output":[{"type":"message","role":"assistant","content":"MARKER"}]}"""
                .Replace("ID", responseId, StringComparison.Ordinal)
                .Replace("MARKER", marker, StringComparison.Ordinal),
            ExternalId = responseId,
            Status = status,
            Stream = true,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };

        return _requests.Add(row);
    }

    private static string Text(byte[]? value) => value is null ? string.Empty : Encoding.UTF8.GetString(value);

    [Fact]
    public async Task ScopesByApiKeyAndProject()
    {
        AddSession(OwnerKeyId, ApiFormats.OpenAIResponse, "resp_shared", "owner");
        AddSession(OtherKeyId, ApiFormats.OpenAIResponse, "resp_shared", "other");
        AddSession(OwnerKeyId, ApiFormats.OpenAIResponseWebSocket, "resp_websocket", "websocket");

        var owner = await _loader.LoadAsync(
            "resp_shared",
            IdentityFor(OwnerKeyId),
            CancellationToken.None);
        Assert.True(owner.Found);
        Assert.Equal("""{"model":"gpt-5","input":"owner"}""", Text(owner.RequestBody));
        Assert.Equal(
            """{"id":"resp_shared","status":"completed","output":[{"type":"message","role":"assistant","content":"owner"}]}""",
            Text(owner.ResponseBody));

        var other = await _loader.LoadAsync(
            "resp_shared",
            IdentityFor(OtherKeyId),
            CancellationToken.None);
        Assert.True(other.Found);
        Assert.Equal("""{"model":"gpt-5","input":"other"}""", Text(other.RequestBody));

        var differentProject = await _loader.LoadAsync(
            "resp_shared",
            IdentityFor(OwnerKeyId, ProjectId + 1),
            CancellationToken.None);
        Assert.False(differentProject.Found);

        var websocket = await _loader.LoadAsync(
            "resp_websocket",
            IdentityFor(OwnerKeyId),
            CancellationToken.None);
        Assert.True(websocket.Found);
        Assert.Equal("""{"model":"gpt-5","input":"websocket"}""", Text(websocket.RequestBody));

        Assert.False((await _loader.LoadAsync(
            "resp_websocket",
            IdentityFor(OtherKeyId),
            CancellationToken.None)).Found);
        Assert.False((await _loader.LoadAsync(
            "resp_websocket",
            IdentityFor(OwnerKeyId, ProjectId + 1),
            CancellationToken.None)).Found);
    }

    [Fact]
    public async Task RequiresAnApiKey()
    {
        AddSession(OwnerKeyId, ApiFormats.OpenAIResponse, "resp_shared", "owner");

        var loaded = await _loader.LoadAsync(
            "resp_shared",
            new RequestIdentity(ProjectId, ApiKeyId: null)
            {
                Project = new ProjectRecord { Id = ProjectId },
            },
            CancellationToken.None);

        Assert.False(loaded.Found);
    }

    [Fact]
    public async Task RejectsEmptyStoredBodies()
    {
        AddSession(
            OwnerKeyId,
            ApiFormats.OpenAIResponse,
            "resp_empty_request",
            "owner",
            requestBody: "{}",
            responseBody: """{"id":"resp_empty_request","status":"completed","output":[]}""");
        AddSession(
            OwnerKeyId,
            ApiFormats.OpenAIResponse,
            "resp_empty_response",
            "owner",
            responseBody: "{}");

        Assert.False((await _loader.LoadAsync(
            "resp_empty_request",
            IdentityFor(OwnerKeyId),
            CancellationToken.None)).Found);
        Assert.False((await _loader.LoadAsync(
            "resp_empty_response",
            IdentityFor(OwnerKeyId),
            CancellationToken.None)).Found);
    }

    [Fact]
    public async Task RejectsInvalidStoredJson()
    {
        AddSession(
            OwnerKeyId,
            ApiFormats.OpenAIResponse,
            "resp_invalid",
            "owner",
            requestBody: "not json");

        Assert.False((await _loader.LoadAsync(
            "resp_invalid",
            IdentityFor(OwnerKeyId),
            CancellationToken.None)).Found);
    }

    [Fact]
    public async Task IgnoresOtherFormatsAndStatuses()
    {
        AddSession(
            OwnerKeyId,
            ApiFormats.OpenAIResponseCompact,
            "resp_compact",
            "compact");
        AddSession(
            OwnerKeyId,
            ApiFormats.OpenAIResponse,
            "resp_failed",
            "failed",
            status: "failed");

        Assert.False((await _loader.LoadAsync(
            "resp_compact",
            IdentityFor(OwnerKeyId),
            CancellationToken.None)).Found);
        Assert.False((await _loader.LoadAsync(
            "resp_failed",
            IdentityFor(OwnerKeyId),
            CancellationToken.None)).Found);
    }

    [Theory]
    [InlineData(
        "root",
        """{"input":"canary"}""",
        """{"id":"response","status":"completed","output":[]}""",
        true)]
    [InlineData(
        "unresolved ancestor",
        """{"previous_response_id":"missing","input":"delta"}""",
        """{"id":"response","status":"completed","output":[]}""",
        false)]
    [InlineData(
        "incomplete",
        """{"input":"canary"}""",
        """{"id":"response","status":"incomplete","output":[]}""",
        false)]
    [InlineData(
        "wrong provider ID",
        """{"input":"canary"}""",
        """{"id":"wrong","status":"completed","output":[]}""",
        false)]
    public async Task RequiresACertifiedSnapshot(
        string _,
        string requestBody,
        string responseBody,
        bool found)
    {
        // request_responses_certification.go:20-46: only a self-contained turn
        // may be replayed from persistence. The ancestor check is deliberately
        // strict about a previous_response_id that the persisted request could
        // not resolve, and the response must carry the provider id and a
        // completed status.
        AddSession(
            OwnerKeyId,
            ApiFormats.OpenAIResponse,
            "response",
            "canary",
            requestBody: requestBody,
            responseBody: responseBody);

        var loaded = await _loader.LoadAsync(
            "response",
            IdentityFor(OwnerKeyId),
            CancellationToken.None);

        Assert.Equal(found, loaded.Found);
        if (found)
        {
            Assert.Equal(requestBody, Text(loaded.RequestBody));
            Assert.Equal(responseBody, Text(loaded.ResponseBody));
        }
    }

    [Fact]
    public async Task TrimsTheClientResponseId()
    {
        AddSession(OwnerKeyId, ApiFormats.OpenAIResponse, "resp_trim", "trim");

        var loaded = await _loader.LoadAsync(
            "  resp_trim  ",
            IdentityFor(OwnerKeyId),
            CancellationToken.None);

        Assert.True(loaded.Found);
    }

    [Fact]
    public async Task ReturnsTheNewestCompletedRow()
    {
        AddSession(
            OwnerKeyId,
            ApiFormats.OpenAIResponse,
            "resp_same",
            "older",
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        AddSession(
            OwnerKeyId,
            ApiFormats.OpenAIResponse,
            "resp_same",
            "newer",
            createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var loaded = await _loader.LoadAsync(
            "resp_same",
            IdentityFor(OwnerKeyId),
            CancellationToken.None);

        Assert.True(loaded.Found);
        Assert.Equal("""{"model":"gpt-5","input":"newer"}""", Text(loaded.RequestBody));
    }
}
