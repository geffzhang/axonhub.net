using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Api.GraphQL;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AxonHub.Api.Tests;

public sealed class AdminGraphqlAuthScopeTests
{
    [Fact]
    public async Task ConcurrentJwtRequestsUseRequestScopedAuthDependencies()
    {
        var gate = new AdminGraphqlGateServices();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        gate.Register(builder.Services);
        builder.Services.RemoveAll<IUserStore>();
        builder.Services.AddScoped<IUserStore>(_ => new ConcurrentUseDetectingUserStore(gate.Users));

        await using var app = builder.Build();
        app.MapAdminGraphql();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var requests = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/graphql");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gate.Jwt);
            request.Content = new StringContent(
                """{"query":"{ __typename }"}""",
                Encoding.UTF8,
                "application/json");

            using var response = await client.SendAsync(request);
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync());
        });

        var responses = await Task.WhenAll(requests);

        Assert.All(
            responses,
            response => Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"{response.StatusCode}: {response.Body}"));
    }

    private sealed class ConcurrentUseDetectingUserStore(IUserStore inner) : IUserStore
    {
        private int _activeReads;

        public async Task<UserRecord?> GetByIdAsync(
            long id,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _activeReads) != 1)
            {
                Interlocked.Decrement(ref _activeReads);
                throw new InvalidOperationException("A scoped user store was used concurrently.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                return await inner.GetByIdAsync(id, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        public Task<UserRecord?> GetActivatedByEmailAsync(
            string email,
            CancellationToken cancellationToken = default)
            => inner.GetActivatedByEmailAsync(email, cancellationToken);

        public Task<UserRecord?> FindOwnerAsync(CancellationToken cancellationToken = default)
            => inner.FindOwnerAsync(cancellationToken);

        public Task<long> CreateAsync(UserRecord user, CancellationToken cancellationToken = default)
            => inner.CreateAsync(user, cancellationToken);
    }
}
