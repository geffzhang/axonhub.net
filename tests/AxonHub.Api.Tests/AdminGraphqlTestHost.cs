using System.Net.Http.Headers;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Api.Static;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Shared authenticated host for the admin GraphQL endpoint tests. Since the
/// gate (AdminGraphqlAuthMiddleware) runs ahead of every /admin/graphql POST,
/// each fixture seeds a user and the JWT secret, issues a token and authorizes
/// its client. The seeded user replaces the FakeManagementIdentity the
/// fixtures registered while the endpoint was open (C161); service-account API
/// key clients cover the "no user in context" branches.
/// </summary>
internal sealed class AdminGraphqlGateServices
{
    public const string Secret = "test-secret";

    /// <summary>The JWT user id the fixtures assert on (gid://axonhub/User/9).</summary>
    public const long UserId = 9;

    public FakeUserStore Users { get; } = new();

    /// <summary>
    /// Scoped-permission rows for the privacy resolvers (ChannelPrivacy /
    /// AuthorizationScope). Seeded with the gate user so owner checks work in
    /// fixtures that do not register their own IUserAdminStore; a fixture's
    /// later registration replaces it entirely.
    /// </summary>
    public FakeUserAdminStore UserAdmins { get; } = new();

    public FakeSystemStore Systems { get; } = new();

    public FakeApiKeyStore ApiKeys { get; } = new();

    public FakeProjectStore Projects { get; } = new();

    public FakeUserProfileStore Profiles { get; } = new();

    public FakePasswordHasher Passwords { get; } = new();

    public FakeOidcLoginOnlyProviders Oidc { get; } = new();

    /// <summary>The gate user row (id 9) the JWT resolves to.</summary>
    public UserRecord Owner { get; }

    /// <summary>The bearer token every fixture client sends.</summary>
    public string Jwt { get; }

    public AdminGraphqlGateServices(bool isOwner = true)
    {
        Owner = Users.Add(user =>
        {
            user.Id = UserId;
            user.Email = "gate-user@example.com";
            user.Status = "activated";
            user.Password = "stored-hash";
            user.IsOwner = isOwner;
        });
        Systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        Jwt = IssueJwt(Owner);

        // The same row backs the permission lookups; FakeUserAdminStore returns
        // null for unknown ids, so an unseeded gate user would look scope-less.
        UserAdmins.CreateAsync(
                new UserRecord
                {
                    Id = UserId,
                    Email = Owner.Email,
                    Status = "activated",
                    IsOwner = isOwner,
                },
                roleIds: null)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>Issues a token with this fixture's stores.</summary>
    public string IssueJwt(UserRecord user) => IssueJwtWith(Users, Systems, user);

    /// <summary>Issues a token against an explicitly chosen store pair.</summary>
    public static string IssueJwtWith(
        IUserStore users,
        ISystemStore systems,
        UserRecord user)
        => new ManagementAuthService(
                users,
                systems,
                new FakeUserProfileStore(),
                new FakePasswordHasher(),
                new FakeOidcLoginOnlyProviders())
            .GenerateJwtTokenAsync(user)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Registers the gate's dependencies plus the HttpContext-backed identity.
    /// Register this before any fixture-specific store registration so the
    /// fixture's own fakes stay in effect (DI resolves the last registration).
    /// </summary>
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IUserStore>(Users);
        services.AddSingleton<IUserAdminStore>(UserAdmins);
        services.AddSingleton<ISystemStore>(Systems);
        services.AddSingleton<IApiKeyStore>(ApiKeys);
        services.AddSingleton<IProjectStore>(Projects);
        services.AddSingleton<IUserProfileStore>(Profiles);
        services.AddSingleton<IPasswordHasher>(Passwords);
        services.AddSingleton<IOidcLoginOnlyProviders>(Oidc);
        services.AddSingleton(ApiKeyAuthOptions.Default);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IApiKeyAuthenticator, ApiKeyAuthenticator>();
        services.AddScoped<ManagementAuthService>();
        services.AddHttpContextAccessor();
        services.AddScoped<IManagementIdentity, HttpContextManagementIdentity>();

        // Shared privacy seams: channel field gates and the summary/tag roots
        // resolve them in every fixture; the prompt-protection rule service
        // resolves its schema policy the same way (C014).
        services.AddScoped<ChannelPrivacy>();
        services.AddScoped<PromptProtectionRulePrivacy>();
        services.AddScoped<AuthorizationScope>();
    }

    /// <summary>Adds an enabled service-account key on an active project.</summary>
    public ApiKeyRecord AddServiceAccountKey(
        long projectId = 1,
        Action<ApiKeyRecord>? configure = null)
    {
        EnsureActiveProject(projectId);
        return ApiKeys.Add(key =>
        {
            key.Key = "sk-service-account";
            key.Name = "service account";
            key.Type = "service_account";
            key.Status = "enabled";
            key.ProjectId = projectId;
            configure?.Invoke(key);
        });
    }

    /// <summary>Adds an enabled key of the given type on an active project.</summary>
    public ApiKeyRecord AddKey(string type, long projectId = 1, Action<ApiKeyRecord>? configure = null)
    {
        EnsureActiveProject(projectId);
        return ApiKeys.Add(key =>
        {
            key.Key = "sk-" + type;
            key.Name = type + " key";
            key.Type = type;
            key.Status = "enabled";
            key.ProjectId = projectId;
            configure?.Invoke(key);
        });
    }

    private void EnsureActiveProject(long projectId)
    {
        if (Projects.GetByIdAsync(projectId).GetAwaiter().GetResult() is null)
        {
            Projects.Add(project =>
            {
                project.Id = projectId;
                project.Status = "active";
            });
        }
    }

    /// <summary>
    /// Fixtures that replace the gate's ISystemStore after <see cref="Register"/>
    /// must still make the JWT secret readable to the auth middleware, which
    /// resolves the last-registered ISystemStore. Call after <c>Build()</c>.
    /// </summary>
    public void SeedJwtSecret(IServiceProvider services)
    {
        var systems = services.GetRequiredService<ISystemStore>();
        if (systems.GetByKeyAsync(ManagementAuthService.SecretKeyKey)
            .GetAwaiter().GetResult() is not null)
        {
            return;
        }

        systems.CreateAsync(new SystemRecord
        {
            Key = ManagementAuthService.SecretKeyKey,
            Value = Secret,
        }).GetAwaiter().GetResult();
    }

    /// <summary>Sends the fixture JWT on every request.</summary>
    public void Authorize(HttpClient client)
        => client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Jwt);

    /// <summary>Sends a raw bearer value on every request.</summary>
    public static void Authorize(HttpClient client, string token)
        => client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
}

internal static class AdminGraphqlHostBuilder
{
    /// <summary>
    /// Mirrors Program.cs: the POST-only HotChocolate endpoint plus the
    /// WithAdminGraphqlAuth / WithProjectID / apiKeyReadOnly chain
    /// (routes.go:161-168, gql/graphql.go:187-204).
    /// </summary>
    public static WebApplication MapAdminGraphql(this WebApplication app)
    {
        app.MapGraphQL("/admin/graphql")
            .WithOptions(static options => options.EnableGetRequests = false)
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));
        app.UseWhen(
            static context => context.Request.Path.StartsWithSegments("/admin/graphql")
                && !HttpMethods.IsPost(context.Request.Method),
            static branch => branch.Run(context =>
                StaticHandler.WriteApiNotFoundAsync(
                    context,
                    context.Request.Path.Value ?? "/")));
        app.UseWhen(
            static context => context.Request.Path.StartsWithSegments("/admin/graphql")
                && HttpMethods.IsPost(context.Request.Method),
            static branch =>
            {
                branch.UseMiddleware<AdminGraphqlAuthMiddleware>();
                branch.UseMiddleware<ProjectIdMiddleware>();
                branch.UseMiddleware<ApiKeyReadOnlyMiddleware>();
            });
        return app;
    }
}
