using System.Text;
using AxonHub.Api.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the middleware's abort bodies and identity publication
/// (internal/server/middleware/auth.go + AbortWithError).
/// </summary>
public sealed class ApiKeyAuthMiddlewareTests
{
    [Fact]
    public async Task InvalidKeyWritesUnauthorizedBodyAndDoesNotCallNext()
    {
        var (context, nextCalled) = await InvokeAsync(
            _ => throw new InvalidApiKeyException());

        Assert.False(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal(
            """{"error":{"type":"Unauthorized","message":"Invalid API key"}}""",
            Body(context));
    }

    [Fact]
    public async Task ForbiddenIpWritesForbiddenBodyAndDoesNotCallNext()
    {
        var (context, nextCalled) = await InvokeAsync(
            _ => throw new ClientIpForbiddenException());

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
            _ => throw new InvalidOperationException("database is down"));

        Assert.False(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"type":"Internal Server Error","message":"Failed to validate API key"}}""",
            Body(context));
    }

    [Fact]
    public async Task SuccessfulAuthenticationPublishesIdentityAndCallsNext()
    {
        var identity = new RequestIdentity(7, 42);
        var (context, nextCalled) = await InvokeAsync(_ => identity);

        Assert.True(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(identity, context.Items[RequestIdentity.HttpContextItemKey]);
    }

    private static async Task<(HttpContext Context, NextCall NextCalled)> InvokeAsync(
        Func<HttpRequest, RequestIdentity> authenticate)
    {
        var context = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };
        var next = new NextCall();

        var middleware = new ApiKeyAuthMiddleware(
            _ =>
            {
                next.Invoked = true;
                return Task.CompletedTask;
            },
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        var authenticator = new StubAuthenticator(authenticate);
        await middleware.InvokeAsync(context, authenticator);
        return (context, next);
    }

    private static string Body(HttpContext context)
    {
        var stream = Assert.IsType<MemoryStream>(context.Response.Body);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class StubAuthenticator(Func<HttpRequest, RequestIdentity> authenticate)
        : IApiKeyAuthenticator
    {
        public Task<RequestIdentity> AuthenticateAsync(
            HttpRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(authenticate(request));
        }
    }

    private sealed class NextCall
    {
        public bool Invoked { get; set; }
    }
}
