using AxonHub.Abstractions;
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
app.MapPost("/v1/chat/completions", ChatCompletionEndpoint.HandleAsync);

app.Run();
