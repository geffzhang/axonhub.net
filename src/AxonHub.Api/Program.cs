using AxonHub.Abstractions;
using AxonHub.Api.Auth;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
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
builder.Services.AddSingleton(static services =>
    services.GetRequiredService<IConfiguration>().GetSection("RetryPolicy").Get<RetryPolicy>() ?? new RetryPolicy());
builder.Services.AddSingleton<IChannelMetricsRecorder, GrainChannelMetricsRecorder>();
builder.Services.AddSingleton<IRequestChainRecorder, PostgresRequestChainRecorder>();
builder.Services.AddSingleton<ChatCompletionOrchestrator>();

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

// The reference mounts WithIPBlocklist + WithAPIKeyConfig on its /v1 group;
// the slice ports the API key middleware (C023).
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/v1"),
    static branch => branch.UseMiddleware<ApiKeyAuthMiddleware>());

app.MapPost("/v1/chat/completions", ChatCompletionEndpoint.HandleAsync);

app.Run();
