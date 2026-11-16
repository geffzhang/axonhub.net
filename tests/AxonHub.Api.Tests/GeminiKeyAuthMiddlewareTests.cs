using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks middleware.WithGeminiKeyAuth (auth.go:262-310): the query `key`
/// parameter wins over the header extractor, authentication is strict (no
/// no-auth fallback), and the abort bodies match AbortWithError's envelope.
/// </summary>
public sealed class GeminiKeyAuthMiddlewareTests
{
    [Fact]
    public async Task QueryKeyWinsOverHeaders()
    {
        var receivedKeys = new List<string>();
        var (context, nextCalled) = await InvokeAsync(
            static _ => new RequestIdentity(1, 2),
            receivedKeys,
            queryKey: "query-key",
            headerKey: "Bearer header-key");

        Assert.True(nextCalled.Invoked);
        Assert.Equal(["query-key"], receivedKeys);
        Assert.IsType<RequestIdentity>(context.Items[RequestIdentity.HttpContextItemKey]);
    }

    [Fact]
    public async Task HeaderFallbackWhenQueryMissing()
    {
        var receivedKeys = new List<string>();
        var (context, nextCalled) = await InvokeAsync(
            static _ => new RequestIdentity(1, 2),
            receivedKeys,
            headerKey: "Bearer header-key");

        Assert.True(nextCalled.Invoked);
        Assert.Equal(["header-key"], receivedKeys);
    }

    [Fact]
    public async Task MissingKeyWritesUnauthorizedBodyAndDoesNotCallNext()
    {
        var (context, nextCalled) = await InvokeAsync(static _ => throw new InvalidApiKeyException(), []);

        Assert.False(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"type":"Unauthorized","message":"API key is required"}}""",
            Body(context));
    }

    [Fact]
    public async Task InvalidKeyWritesInvalidApiKeyBodyAndDoesNotCallNext()
    {
        var (context, nextCalled) = await InvokeAsync(
            static _ => throw new InvalidApiKeyException(),
            [],
            queryKey: "bad-key");

        Assert.False(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"type":"Unauthorized","message":"invalid api key"}}""",
            Body(context));
    }

    [Fact]
    public async Task ForbiddenIpWritesForbiddenBodyAndDoesNotCallNext()
    {
        var (context, nextCalled) = await InvokeAsync(
            static _ => throw new ClientIpForbiddenException(),
            [],
            queryKey: "good-key");

        Assert.False(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"type":"Forbidden","message":"IP address is not allowed for this API key"}}""",
            Body(context));
    }

    [Fact]
    public async Task UnexpectedFailureWritesInternalErrorBody()
    {
        var (context, nextCalled) = await InvokeAsync(
            static _ => throw new InvalidOperationException("database is down"),
            [],
            queryKey: "good-key");

        Assert.False(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"type":"Internal Server Error","message":"Failed to validate API key"}}""",
            Body(context));
    }

    private static async Task<(HttpContext Context, NextCall NextCalled)> InvokeAsync(
        Func<HttpRequest, RequestIdentity> authenticate,
        List<string> receivedKeys,
        string? queryKey = null,
        string? headerKey = null)
    {
        var context = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };
        if (queryKey is not null)
        {
            context.Request.QueryString = new QueryString($"?key={queryKey}");
        }

        if (headerKey is not null)
        {
            context.Request.Headers.Authorization = headerKey;
        }

        var next = new NextCall();

        var middleware = new GeminiKeyAuthMiddleware(
            _ =>
            {
                next.Invoked = true;
                return Task.CompletedTask;
            },
            NullLogger<GeminiKeyAuthMiddleware>.Instance);

        var authenticator = new StubAuthenticator(
            authenticate,
            key =>
            {
                receivedKeys.Add(key);
                return authenticate(context.Request);
            });
        await middleware.InvokeAsync(context, authenticator);
        return (context, next);
    }

    private static string Body(HttpContext context)
    {
        var stream = Assert.IsType<MemoryStream>(context.Response.Body);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class StubAuthenticator(
        Func<HttpRequest, RequestIdentity> authenticate,
        Func<string, RequestIdentity> authenticateKey)
        : IApiKeyAuthenticator
    {
        public Task<RequestIdentity> AuthenticateAsync(
            HttpRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(authenticate(request));
        }

        public Task<RequestIdentity> AuthenticateKeyAsync(
            HttpRequest request,
            string key,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromResult(authenticateKey(key));
        }

        public Task<ApiKeyRecord> AuthenticateAdminKeyAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            _ = key;
            _ = cancellationToken;
            throw new NotSupportedException();
        }
    }

    private sealed class NextCall
    {
        public bool Invoked { get; set; }
    }
}
