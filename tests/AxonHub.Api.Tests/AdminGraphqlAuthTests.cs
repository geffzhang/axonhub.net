using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the admin GraphQL gate against internal/server/middleware/auth.go
/// (WithAdminGraphqlAuth, auth_admin_test.go:108-159), middleware/project.go
/// (WithProjectID) and internal/server/gql/graphql.go:187-204
/// (apiKeyReadOnly): the bearer extraction texts, JWT-first fallback, the
/// service_account type rule, the allowed_ips rule, the 500/401 split, the
/// read-only mutation refusal and the X-Project-ID rules.
/// </summary>
public sealed class AdminGraphqlAuthTests : IAsyncLifetime
{
    private const string Query = "{ allScopes { scope } }";
    private const string Mutation = "mutation { updateRetryPolicy(input: { enabled: true }) }";

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly RecordingLogger<AdminGraphqlAuthMiddleware> _logger = new();

    private AdminGraphqlAuthMiddleware _middleware = null!;
    private ManagementAuthService _auth = null!;
    private IApiKeyAuthenticator _apiKeys = null!;
    private bool _nextCalled;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _apiKeys = new ApiKeyAuthenticator(
            _gate.ApiKeys,
            _gate.Projects,
            _gate.Users,
            ApiKeyAuthOptions.Default,
            TimeProvider.System);
        _auth = new ManagementAuthService(
            _gate.Users,
            _gate.Systems,
            _gate.Profiles,
            _gate.Passwords,
            _gate.Oidc);
        _middleware = new AdminGraphqlAuthMiddleware(
            _ =>
            {
                _nextCalled = true;
                return Task.CompletedTask;
            },
            ApiKeyAuthOptions.Default,
            _logger);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddScoped<ISystemSettingsAdminService>(
            _ => new SystemSettingsAdminService(_gate.Systems));
        builder.Services.AddSingleton<UserProfileService>();
        builder.Services.AddSingleton<ILogger<AdminGraphqlAuthMiddleware>>(_logger);

        _app = builder.Build();
        _app.MapAdminGraphql();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task AUserJwtIsAcceptedAndFeedsTheResolvers()
    {
        _gate.Profiles.User = new UserRecord
        {
            Id = AdminGraphqlGateServices.UserId,
            Email = "gate-user@example.com",
            Status = "activated",
            Password = "stored-hash",
            IsOwner = true,
        };

        var (status, body, _) = await PostAsync(
            "Bearer " + _gate.Jwt,
            "{ me { id email isOwner } }");

        Assert.True(status == HttpStatusCode.OK, body.ToString());
        Assert.True(
            body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object,
            body.ToString());
        var me = data.GetProperty("me");
        Assert.Equal("gid://axonhub/User/9", me.GetProperty("id").GetString());
        Assert.Equal("gate-user@example.com", me.GetProperty("email").GetString());
        Assert.True(me.GetProperty("isOwner").GetBoolean());
    }

    [Fact]
    public async Task AServiceAccountApiKeyIsAccepted()
    {
        _gate.AddServiceAccountKey();

        var (status, body, _) = await PostAsync("Bearer sk-service-account", Query);

        Assert.True(status == HttpStatusCode.OK, body.ToString());
        Assert.NotEmpty(body.GetProperty("data").GetProperty("allScopes").EnumerateArray());
    }

    [Fact]
    public async Task AnApiKeyOfAnotherTypeIsForbidden()
    {
        _gate.AddKey("user");

        var (status, body, _) = await PostAsync("Bearer sk-user", Query);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(
            "Only service account API keys may access the admin GraphQL API",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task AnUnknownTokenIsUnauthorized()
    {
        var (status, body, _) = await PostAsync("Bearer not-a-jwt-nor-an-api-key", Query);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(
            "Invalid token",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ADisabledServiceAccountKeyIsUnauthorized()
    {
        _gate.AddServiceAccountKey(configure: key => key.Status = "disabled");

        var (status, body, _) = await PostAsync("Bearer sk-service-account", Query);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(
            "Invalid token",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task AKeyOnAnInactiveProjectIsUnauthorized()
    {
        _gate.Projects.Add(project =>
        {
            project.Id = 5;
            project.Status = "archived";
        });
        _gate.AddServiceAccountKey(projectId: 5);

        var (status, body, _) = await PostAsync("Bearer sk-service-account", Query);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(
            "Invalid token",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheNoAuthKeyValueIsUnauthorized()
    {
        _gate.AddKey(
            "noauth",
            configure: key => key.Key = ApiKeyAuthenticator.NoAuthApiKeyValue);

        var (status, body, _) = await PostAsync(
            "Bearer " + ApiKeyAuthenticator.NoAuthApiKeyValue,
            Query);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(
            "Invalid token",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheKeyTypeIsCheckedBeforeTheAllowedIpList()
    {
        _gate.AddKey(
            "user",
            configure: key => key.AllowedIps = """["10.0.0.0/8"]""");

        var (status, body, _) = await PostAsync("Bearer sk-user", Query);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(
            "Only service account API keys may access the admin GraphQL API",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task AServiceAccountKeyOutsideItsAllowedIpsIsForbidden()
    {
        _gate.AddServiceAccountKey(configure: key => key.AllowedIps = """["10.0.0.0/8"]""");

        var (status, body, _) = await PostAsync("Bearer sk-service-account", Query);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(
            "IP address is not allowed for this API key",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ASecretKeyFailureIsInternalServerErrorNotAFallback()
    {
        _gate.Systems.GetFailure = new InvalidOperationException("db down");

        var (status, body, _) = await PostAsync("Bearer " + _gate.Jwt, Query);

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal(
            "Failed to validate token",
            body.GetProperty("error").GetProperty("message").GetString());
        Assert.Contains(
            _logger.Entries,
            entry => entry.Level == LogLevel.Error
                && entry.Message.Contains("JWT validation failed", StringComparison.Ordinal)
                && entry.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(
            _logger.Entries,
            entry => entry.Message.Contains("db down", StringComparison.Ordinal));
        Assert.All(_logger.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task AnApiKeyStoreFailureIsInternalServerError()
    {
        _gate.ApiKeys.GetByKeyFailure = new InvalidOperationException("db down");

        var (status, body, _) = await PostAsync("Bearer unknown-key", Query);

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal(
            "Failed to validate token",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(null, "API key is required")]
    [InlineData("Basic abc", "invalid token: Authorization header must start with 'Bearer '")]
    [InlineData("Bearer    ", "invalid token: API key is required")]
    public async Task TheExtractionTextsMatchTheReference(string? authorization, string message)
    {
        // The reference's unit test drives gin directly with raw header values
        // (httptest does not re-parse them), so the port drives the middleware
        // with a raw DefaultHttpContext for the same edge inputs.
        _nextCalled = false;
        var context = NewHttpContext();
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        await _middleware.InvokeAsync(context, _auth, _apiKeys);

        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(message, ReadErrorMessage(context));
    }

    [Fact]
    public async Task TheExtractedTokenIsTrimmed()
    {
        _nextCalled = false;
        var context = NewHttpContext();
        context.Request.Headers.Authorization = "Bearer   " + _gate.Jwt + "  ";

        await _middleware.InvokeAsync(context, _auth, _apiKeys);

        Assert.True(_nextCalled);
        var identity = Assert.IsType<AdminGraphqlIdentity>(
            context.Items[AdminGraphqlIdentity.ContextKey]);
        Assert.Equal(new ManagementUser(AdminGraphqlGateServices.UserId, IsOwner: true), identity.User);
        Assert.False(identity.IsApiKey);
    }

    [Fact]
    public async Task AnApiKeyPrincipalSeesNoUserInContext()
    {
        _gate.AddServiceAccountKey();

        var (status, body, _) = await PostAsync("Bearer sk-service-account", "{ me { id } }");

        Assert.Equal(294, (int)status);
        var error = Assert.Single(body.GetProperty("errors").EnumerateArray());
        Assert.Equal("user not found in context", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AServiceAccountMutationIsRefusedAsReadOnly()
    {
        _gate.AddServiceAccountKey();

        var (status, body, contentType) = await PostAsync(
            "Bearer sk-service-account",
            Mutation,
            accept: "application/json");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);

        // A request error uses the GraphQL response media type even for a
        // legacy client that only accepts application/json.
        Assert.Equal("application/graphql-response+json", contentType);
        var error = Assert.Single(body.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "service account API keys are read-only",
            error.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("data", out _));

        // The mutation never executed: the retry-policy row was not written.
        Assert.DoesNotContain(
            _gate.Systems.Rows,
            row => string.Equals(row.Key, RetryPolicyCodec.RetryPolicyKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AServiceAccountQueryIsAllowed()
    {
        _gate.AddServiceAccountKey();

        var (status, body, _) = await PostAsync("Bearer sk-service-account", Query);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEmpty(body.GetProperty("data").GetProperty("allScopes").EnumerateArray());
    }

    [Theory]
    [InlineData("Q", true)]
    [InlineData("M", false)]
    public async Task TheReadOnlyRuleFollowsTheSelectedOperation(string operationName, bool allowed)
    {
        _gate.AddServiceAccountKey();
        var document = "query Q " + Query
            + "\nmutation M " + Mutation["mutation ".Length..];

        var (status, body, _) = await PostAsync(
            "Bearer sk-service-account",
            document,
            operationName: operationName);

        Assert.Equal(
            allowed ? HttpStatusCode.OK : HttpStatusCode.UnprocessableEntity,
            status);
        if (allowed)
        {
            Assert.True(body.TryGetProperty("data", out _), body.ToString());
        }
        else
        {
            var error = Assert.Single(body.GetProperty("errors").EnumerateArray());
            Assert.Equal(
                "service account API keys are read-only",
                error.GetProperty("message").GetString());
        }
    }

    [Fact]
    public async Task AUserJwtCanRunMutations()
    {
        var (status, body, _) = await PostAsync("Bearer " + _gate.Jwt, Mutation);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("data").GetProperty("updateRetryPolicy").GetBoolean());
    }

    [Fact]
    public async Task AProjectHeaderPassesForAUserJwt()
    {
        var (status, body, _) = await PostAsync(
            "Bearer " + _gate.Jwt,
            Query,
            projectId: "gid://axonhub/Project/7");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.TryGetProperty("data", out _));
    }

    [Theory]
    [InlineData("not-a-gid")]
    [InlineData("gid://axonhub/User/7")]
    public async Task AMalformedProjectHeaderIsBadRequest(string projectId)
    {
        var (status, body, _) = await PostAsync(
            "Bearer " + _gate.Jwt,
            Query,
            projectId: projectId);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(
            "Invalid project ID",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task AnotherProjectsHeaderIsForbiddenForAnApiKey()
    {
        _gate.AddServiceAccountKey(projectId: 1);

        var (status, body, _) = await PostAsync(
            "Bearer sk-service-account",
            Query,
            projectId: "gid://axonhub/Project/7");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(
            "Project ID is not allowed for this API key",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheApiKeysOwnProjectHeaderPasses()
    {
        _gate.AddServiceAccountKey(projectId: 1);

        var (status, body, _) = await PostAsync(
            "Bearer sk-service-account",
            Query,
            projectId: "gid://axonhub/Project/1");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task TheRouteIsPostOnly()
    {
        using var response = await _client.GetAsync("/admin/graphql");

        // routes.go mounts POST /graphql only; a GET falls through to the
        // NoRoute handler, which answers API prefixes with its JSON 404.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static DefaultHttpContext NewHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/admin/graphql";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadErrorMessage(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        var raw = reader.ReadToEnd();
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.GetProperty("error").GetProperty("message").GetString()!;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body, string? ContentType)> PostAsync(
        string? authorization,
        string query,
        string? projectId = null,
        string? operationName = null,
        string? accept = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/graphql");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (projectId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Project-ID", projectId);
        }

        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        var payload = JsonSerializer.Serialize(new { query, operationName });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);
        return (
            response.StatusCode,
            document.RootElement.Clone(),
            response.Content.Headers.ContentType?.MediaType);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = eventId;
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
