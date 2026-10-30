using AxonHub.Abstractions;
using AxonHub.Api.Auth;
using AxonHub.Api.Billing;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Api.Security;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Persistence;
using Orleans.Hosting;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Default is required (PostgreSQL, same schema as the reference).");
}

// Persistence: one connection factory feeds the write side (EF Core) and the
// read side (Dapper), matching the reference's single PostgreSQL database.
builder.Services.AddSingleton(new PostgresConnectionFactory(connectionString));
builder.Services.AddSingleton<IAxonHubDbContextFactory>(
    static services => services.GetRequiredService<PostgresConnectionFactory>());
builder.Services.AddSingleton<IEnabledChannelProvider, PostgresEnabledChannelProvider>();

builder.Services.AddSingleton(TimeProvider.System);

// The reference's HTTP client has no request timeout (llm/httpclient/client.go);
// timeouts come from the retry policy scopes inside the pipeline.
builder.Services.AddSingleton(static _ => new HttpExecutor(new HttpClient(new SocketsHttpHandler())
{
    Timeout = Timeout.InfiniteTimeSpan,
}));
builder.Services.AddSingleton<IExecutor>(static services => services.GetRequiredService<HttpExecutor>());

builder.Services.AddSingleton<IInboundTransformer, OpenAiInboundTransformer>();
builder.Services.AddSingleton<IChannelMetricsRecorder, GrainChannelMetricsRecorder>();
builder.Services.AddSingleton<IRequestChainRecorder, PostgresRequestChainRecorder>();

// Usage-cost pricing: the reference preloads channel_model_prices into a
// per-process cache; the port reads the same rows through the recorder-style
// synchronous seam (IChannelPricingSource). The cost service is stateless, so
// one instance serves every scoped orchestrator.
builder.Services.AddSingleton<IChannelPricingSource, PostgresChannelPricingSource>();
builder.Services.AddSingleton<IUsageCostService, UsageCostService>();

// One rate-limit state per silo, shared by the API middlewares (writes) and the
// channel pool grain's RateLimitAware scoring (reads) — the reference's
// per-process ChannelRequestTracker and ChannelLimiterManager
// (orchestrator.go: loadBalancerConfig).
builder.Services.AddSingleton(static services => new ChannelRequestTracker(
    services.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ChannelLimiterManager>();

// API key authentication (server.api.auth): the reference reads allow_no_auth
// from config.yml and the AXONHUB_* environment; both spellings are accepted.
var allowNoAuth = bool.TryParse(
        builder.Configuration["Server:Api:Auth:AllowNoAuth"],
        out var configuredAllowNoAuth)
    && configuredAllowNoAuth;
if (!allowNoAuth)
{
    _ = bool.TryParse(
        builder.Configuration["AXONHUB_SERVER_API_AUTH_ALLOW_NO_AUTH"],
        out allowNoAuth);
}

// server.trusted_proxies defaults to an empty list: forwarded client-IP headers
// are only honoured when the TCP peer is one of the configured networks.
var trustedProxies = builder.Configuration.GetSection("Server:TrustedProxies").Get<string[]>()
    ?? [];
ClientAddress.ValidateTrustedProxies(trustedProxies);

builder.Services.AddSingleton(new ApiKeyAuthOptions(allowNoAuth, trustedProxies));
builder.Services.AddScoped<AxonHubDbContext>(static services =>
    services.GetRequiredService<IAxonHubDbContextFactory>().CreateDbContext());
builder.Services.AddScoped<IProjectStore, ProjectStore>();
builder.Services.AddScoped<IUserStore, UserStore>();
builder.Services.AddScoped<IApiKeyStore, ApiKeyStore>();
builder.Services.AddScoped<IApiKeyAuthenticator, ApiKeyAuthenticator>();
builder.Services.AddScoped<ISystemStore, SystemStore>();
builder.Services.AddScoped<ISecuritySettingsProvider, SystemSettingsService>();
builder.Services.AddScoped<IRetryPolicyProvider, SystemRetryPolicyService>();

// The orchestrator reads the retry policy through the scoped system store.
builder.Services.AddScoped<ChatCompletionOrchestrator>();

builder.Host.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddMemoryGrainStorage("Default");
});

var app = builder.Build();

// Ent runs auto-migration on every startup; the port keeps the idempotent DDL.
await using (var connection = await app.Services.GetRequiredService<PostgresConnectionFactory>().OpenAsync())
{
    await DatabaseMigrator.EnsureCreatedAsync(connection);
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// The reference mounts WithIPBlocklist + WithAPIKeyConfig on its API group, in
// that order. The slice applies both to the ported /v1 surface.
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/v1"),
    static branch =>
    {
        branch.UseMiddleware<IpBlocklistMiddleware>();
        branch.UseMiddleware<ApiKeyAuthMiddleware>();
    });

app.MapPost("/v1/chat/completions", ChatCompletionEndpoint.HandleAsync);

app.Run();
