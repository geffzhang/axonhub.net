using AxonHub.Abstractions;
using AxonHub.Api.Assets;
using AxonHub.Api.Auth;
using AxonHub.Api.Auth.Oidc;
using AxonHub.Api.Billing;
using AxonHub.Api.Catalog;
using AxonHub.Api.Cli;
using AxonHub.Api.Codex;
using AxonHub.Api.Diagnostics;
using AxonHub.Api.Endpoints;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Http;
using AxonHub.Api.Lifecycle;
using AxonHub.Api.Management;
using AxonHub.Api.Metrics;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Api.PromptProtection;
using AxonHub.Api.Prompts;
using AxonHub.Api.Safeguards;
using AxonHub.Api.Security;
using AxonHub.Api.Settings;
using AxonHub.Api.Static;
using AxonHub.Api.Storage;
using AxonHub.Api.Traces;
using AxonHub.Grains;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.Doubao;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.Jina;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Decisions;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.TypeSafe;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Metrics;
using Orleans.Hosting;

// cmd/axonhub/main.go:33-52: argv commands run before the host is built.
// Anything else falls through to the normal server start.
var cliExitCode = CliCommand.TryRun(args, Console.Out, Console.Error);
if (cliExitCode is { } exitCode)
{
    Environment.ExitCode = exitCode;
    return;
}

var builder = WebApplication.CreateBuilder(args);

// internal/server/server.go:50-54: gin's MaxMultipartMemory bounds the
// in-memory multipart buffer; HotChocolate's multipart reader consumes the
// same bound from FormOptions.MemoryBufferThreshold.
MultipartMemoryOptions.Configure(builder.Services, builder.Configuration);

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

// Enabled-channel cache: the reference keeps one in-memory snapshot per
// process (ChannelService.enabledChannelsCache, refreshed every minute and on
// mutation). The port hosts it in EnabledChannelsGrain; this registration is
// the grain's database half, and the selection path reads the grain snapshot.
// The reference builds each row's outbounds inside the refresh and skips the
// rows whose build fails (channel.go:265-285); the port's descriptors carry no
// outbounds, so the build check runs in the source decorator (§213).
builder.Services.AddSingleton<PostgresEnabledChannelSource>();
builder.Services.AddSingleton<IEnabledChannelSource>(static services =>
    new BuildFilteringEnabledChannelSource(
        services.GetRequiredService<PostgresEnabledChannelSource>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<ILogger<BuildFilteringEnabledChannelSource>>()));

// Multi-key selection state: the reference keeps apiKeyStates on the
// process-wide ChannelService, so the round_robin rotation, the sticky
// trace→key memory and the fixed / round_robin_success cursors survive every
// channel rebuild (channel_apikey_state.go). Registered before the provider
// that publishes the served generation.
builder.Services.AddSingleton<ChannelApiKeySelectionRegistry>();
builder.Services.AddSingleton<IEnabledChannelProvider, GrainEnabledChannelProvider>();
builder.Services.AddSingleton<IEnabledChannelsCacheRefresher, GrainEnabledChannelsCacheRefresher>();

// The cache diagnostics export reads the same snapshot plus the cache version
// in one activation (getCacheDiagnostics §156).
builder.Services.AddSingleton<
    IEnabledChannelCacheSnapshotProvider,
    GrainEnabledChannelCacheSnapshotProvider>();

// Per-channel OAuth token refresher: ChannelTokenGrain owns the provider state,
// ChannelTokenStore is its optimistic-write half, and ChannelTokenSource builds
// the type-specific token recipe and persists a refresh (§146).
builder.Services.AddSingleton<IChannelTokenStore, ChannelTokenStore>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new ModelCircuitBreaker(TimeProvider.System));

// The reference's HTTP client has no request timeout (llm/httpclient/client.go);
// timeouts come from the retry policy scopes inside the pipeline. Redirects are
// followed by HttpExecutor itself (Go's loop plus the redirect-policy hooks), so
// the handler must not follow them.
builder.Services.AddSingleton(static _ => new HttpExecutor(new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
})
{
    Timeout = Timeout.InfiniteTimeSpan,
}));
builder.Services.AddSingleton<IExecutor>(static services => services.GetRequiredService<HttpExecutor>());

// The refresh HTTP uses the channel's own client, resolved from the seed's
// settings snapshot through ChannelProxyExecutors — the reference hands the
// provider channel.HTTPClient (channel_llm.go:707, 872, 951; §178).
builder.Services.AddSingleton<IChannelTokenSource, ChannelTokenSource>();

builder.Services.AddSingleton<IInboundTransformer, OpenAiInboundTransformer>();

// The Anthropic Messages surface (POST /v1/messages, POST /anthropic/v1/messages)
// reuses the chat handler with a different inbound transformer, exactly like
// internal/server/api/anthropic.go delegates to ChatCompletionHandlers.
builder.Services.AddSingleton<AnthropicInboundTransformer>();

// The native Gemini surface (POST /v1beta/models/{model}:generateContent and
// /gemini/{apiVersion}/...): the Gemini inbound transformer is stateless, so
// one singleton serves every scoped orchestrator.
builder.Services.AddSingleton<GeminiInboundTransformer>();

// The Responses API surface (POST /v1/responses, POST /v1/responses/compact)
// uses the same chat handler with the Responses inbound transformers.
builder.Services.AddSingleton<ResponsesInboundTransformer>();
builder.Services.AddSingleton<ResponsesCompactInboundTransformer>();

// Responses continuation store (orchestrator.responsesSessionStore): process
// wide like the reference. The loader opens a fresh scope per persistence miss
// because the request store and payload storage are scoped services.
builder.Services.AddSingleton(static services => new ResponsesSessionStore(
    services.GetRequiredService<TimeProvider>(),
    async (responseId, identity, cancellationToken) =>
    {
        await using var scope = services
            .GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();
        var loader = scope.ServiceProvider
            .GetRequiredService<CompletedResponsesSessionLoader>();
        return await loader.LoadAsync(responseId, identity, cancellationToken)
            .ConfigureAwait(false);
    },
    services.GetRequiredService<ILogger<ResponsesSessionStore>>(),
    // orchestrator.go:41: requestService.HasCompletedResponsesOrigin, read
    // through a fresh scope for the same reason as the loader.
    async (responseId, cancellationToken) =>
    {
        await using var scope = services
            .GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestStore>();
        return await store.HasCompletedResponsesOriginAsync(responseId, cancellationToken)
            .ConfigureAwait(false);
    }));
builder.Services.AddSingleton<IChannelMetricsRecorder, GrainChannelMetricsRecorder>();
builder.Services.AddSingleton<IRequestChainRecorder, PostgresRequestChainRecorder>();

// Usage-cost pricing: the reference preloads channel_model_prices into a
// per-process cache; the port reads the same rows through the recorder-style
// synchronous seam (IChannelPricingSource). The cost service is scoped so its
// request-scoped model-library memo matches WithModelLibraryPriceMemo and is
// shared by cost injection and usage-log creation within one request.
builder.Services.AddSingleton<IChannelPricingSource, PostgresChannelPricingSource>();
builder.Services.AddScoped<IUsageCostService, UsageCostService>();

// RPM/TPM/cooldown live in the channel grain (one owner per channel), so the
// API middlewares write through the same authority ChannelPoolGrain scores
// from; the reference's per-process tracker remains the in-memory fallback for
// direct constructions. The blocking limiter stays a per-silo singleton
// (orchestrator.go: loadBalancerConfig; C042).
builder.Services.AddSingleton<IChannelRateLimitRecorder, GrainChannelRateLimitRecorder>();
builder.Services.AddSingleton<ChannelLimiterManager>();

// The reference registers the channel-limiter instruments on the process-wide
// OpenTelemetry meter while building the orchestrator (orchestrator.go:41-47).
// System.Diagnostics.Metrics is the .NET collection surface; OpenTelemetry .NET
// reads the same "axonhub" meter, and with no listener the instruments are a
// no-op (difference ledger C044).
builder.Services.AddSingleton(static services => ChannelLimiterMetrics.CreateDefault(
    services.GetRequiredService<ChannelLimiterManager>()));

// internal/metrics (conf/conf.go:293-300, cmd/axonhub/main.go:76,105): metrics
// are off by default. server.name (default "AxonHub") names the meter that
// carries the nine general instruments; the channel limiter keeps its own
// "axonhub" meter (C044), and the provider collects both. The provider is
// always built — disabled means "no reader", like sdk.NewMeterProvider() — and
// the singleton is resolved after Build so the host owns its shutdown flush.
var metricsOptions = MetricsOptions.FromConfiguration(builder.Configuration);
var serverName = builder.Configuration["AXONHUB_SERVER_NAME"];
if (string.IsNullOrEmpty(serverName))
{
    serverName = builder.Configuration["Server:Name"];
}
if (string.IsNullOrEmpty(serverName))
{
    serverName = "AxonHub";
}

builder.Services.AddSingleton(_ => new ServerMetrics(serverName));
builder.Services.AddSingleton(_ => MetricsProvider.Create(metricsOptions, serverName));

// The reference's RequestService owns the previous-channel cache consumed by
// trace sticky routing (request.go:1733-1801); one grain per trace/thread key
// owns each entry so the selector's reads and the persistence middleware's
// writes meet in one place across silos (§147; C181 narrowed).
builder.Services.AddSingleton(new PreviousChannelOptions());
builder.Services.AddSingleton<IPreviousChannelProvider>(static services
    => new GrainPreviousChannelProvider(
        services.GetRequiredService<IGrainFactory>(),
        services.GetRequiredService<ILogger<GrainPreviousChannelProvider>>()));

// Dashboard aggregates live in process-local caches in the reference
// (gql/dashboard_cache.go, gql/dashboard_helpers.go): a shared TTL cache with
// single-flight computation and the all-time token stats snapshot.
builder.Services.AddSingleton<DashboardCache>();
builder.Services.AddSingleton<DashboardTokenStatsCache>();

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

// server.api.auth.key_prefix (conf/conf.go:257) defaults to "ah"; Viper's
// AutomaticEnv accepts AXONHUB_SERVER_API_AUTH_KEY_PREFIX as the override.
var keyPrefix = builder.Configuration["Server:Api:Auth:KeyPrefix"];
if (string.IsNullOrEmpty(keyPrefix))
{
    keyPrefix = builder.Configuration["AXONHUB_SERVER_API_AUTH_KEY_PREFIX"];
}

if (string.IsNullOrEmpty(keyPrefix))
{
    keyPrefix = "ah";
}

builder.Services.AddSingleton(new ApiKeyAuthOptions(allowNoAuth, trustedProxies, keyPrefix));

// server.ip_access_control.* (config.go:155-159). NewIPAccessControlConfig
// validates the allowlist at startup and refuses to start on an invalid entry;
// the constructor call here fails the host build the same way. The snapshot
// object is mutable so the reload path can replace it atomically (C298).
var ipAccessControl = new IpAccessControlConfig(
    IpAccessControlSettings.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(ipAccessControl);

// server.cors.* (conf/conf.go:277-285). rs/cors only mounts when enabled, so
// the ASP.NET Core CORS policy is registered conditionally as well.
var corsSettings = CorsSettings.FromConfiguration(builder.Configuration);
if (corsSettings.Enabled)
{
    builder.Services.AddCors(options =>
        options.AddDefaultPolicy(corsSettings.ConfigurePolicy));
}

// server.sse_keep_alive.* (conf/conf.go:245-246).
builder.Services.AddSingleton(SseKeepAliveSettings.FromConfiguration(builder.Configuration));
builder.Services.AddScoped<AxonHubDbContext>(static services =>
    services.GetRequiredService<IAxonHubDbContextFactory>().CreateDbContext());
builder.Services.AddScoped<IProjectStore, ProjectStore>();
builder.Services.AddScoped<IUserStore, UserStore>();
builder.Services.AddSingleton<ApiKeyCache>();
builder.Services.AddScoped<ApiKeyStore>();
builder.Services.AddScoped<IApiKeyStore>(static services =>
    new CachingApiKeyStore(
        services.GetRequiredService<ApiKeyStore>(),
        services.GetRequiredService<ApiKeyCache>()));
builder.Services.AddScoped<IApiKeyAuthenticator, ApiKeyAuthenticator>();
builder.Services.AddSingleton<SystemValueCache>();
builder.Services.AddScoped<SystemStore>();
builder.Services.AddScoped<ISystemStore>(static services =>
    new CachingSystemStore(
        services.GetRequiredService<SystemStore>(),
        services.GetRequiredService<SystemValueCache>()));
builder.Services.AddScoped<IOidcIdentityAdminStore, OidcIdentityAdminStore>();
builder.Services.AddScoped<ISystemSettingsAdminService, SystemSettingsAdminService>();
// First-run initialization (biz/system.go:698-846): owner user, default
// project, JWT secret, Primary storage, version — one transaction (C241).
builder.Services.AddScoped<ISystemInitializationService, SystemInitializationService>();
// Default favicon asset (internal/server/assets/favicon.go): the embedded
// icon served by GET /favicon when no brand logo is configured (C242).
builder.Services.AddSingleton<IFaviconProvider, EmbeddedFaviconProvider>();
// Version surface (internal/build/info.go + biz/version.go): the static build
// metadata and the GitHub release lookup behind checkForUpdate (C235).
builder.Services.AddSingleton<ISystemVersionService, SystemVersionService>();
builder.Services.AddScoped<ISecuritySettingsProvider, SystemSettingsService>();
builder.Services.AddScoped<IRetryPolicyProvider, SystemRetryPolicyService>();
builder.Services.AddScoped<IPassThroughSettingsProvider, SystemPassThroughSettingsService>();
// Quota routing settings (biz/quota_routing_settings.go): the lenient runtime
// accessor the orchestrator reads, and the strict get/set pair the management
// mutation surfaces. They share one scoped instance.
builder.Services.AddScoped<SystemQuotaRoutingSettingsService>();
builder.Services.AddScoped<IQuotaRoutingSettingsProvider>(
    static services => services.GetRequiredService<SystemQuotaRoutingSettingsService>());

// Prompt protection (biz.PromptProtectionRuleService): the port reads enabled
// rules per request instead of the reference's 30-second live cache (C092).
builder.Services.AddScoped<IPromptProtectionRuleStore, PromptProtectionRuleStore>();
builder.Services.AddScoped<IPromptProtectionRuleProvider, PromptProtectionRuleService>();

// Prompt protection management (the rule service's create/update/delete/status/
// bulk/preview surface): promptProtectionRules / node(PromptProtectionRule)
// and the mutations the unchanged admin frontend sends. The schema's
// read/write channels policy is evaluated by PromptProtectionRulePrivacy
// (C014); its asyncReloadEnabledRules cache refresh is a no-op here (C092).
builder.Services.AddScoped<PromptProtectionRulePrivacy>();
builder.Services.AddScoped<PromptProtectionRuleManagementService>();

// Project prompts (biz.PromptService): the port reads enabled prompts per
// request instead of the reference's per-project one-minute cache (C187).
builder.Services.AddScoped<IPromptStore, PromptStore>();
builder.Services.AddScoped<IPromptProvider, PromptService>();

// The models surface (biz.ModelService.ListEnabledModels) reads the
// system_model_settings row through the same scoped system store.
builder.Services.AddScoped<IModelSettingsProvider, SystemModelSettingsService>();
builder.Services.AddScoped<ModelService>();

// server.trace (conf.go:227-234 area): one options instance feeds both
// middleware.WithLoggingTracing and middleware.WithTrace. Empty header names
// keep the reference's in-middleware fallbacks (AH-Thread-Id / AH-Trace-Id /
// AH-Request-Id), and the three client-marker extractors default to enabled.
builder.Services.AddSingleton(
    TracingOptions.FromConfiguration(builder.Configuration.GetSection("Server:Trace")));

// middleware.WithTrace's persistence half (biz.TraceService.GetOrCreateTrace).
builder.Services.AddScoped<ITraceStore, TraceStore>();

// middleware.WithThread's persistence half (biz.ThreadService.GetOrCreateThread).
builder.Services.AddScoped<IThreadStore, ThreadStore>();

// The video task flow (biz/video.go) reads the request/execution/channel rows
// and persists the polled snapshot through the same stores the write side uses.
builder.Services.AddScoped<IChannelStore, ChannelStore>();
builder.Services.AddScoped<IRequestStore, RequestStore>();
builder.Services.AddScoped<IRequestExecutionStore, RequestExecutionStore>();
builder.Services.AddScoped<IUsageLogStore, UsageLogStore>();
builder.Services.AddScoped<IAnalyticsStore, AnalyticsStore>();
builder.Services.AddScoped<IDashboardStore, DashboardStore>();
builder.Services.AddScoped<VideoService>();

// biz.RequestService.LoadCompletedResponsesSession's persistence half, called
// by the process-wide session store on a cache miss.
builder.Services.AddScoped<CompletedResponsesSessionLoader>();

// The orchestrator's enforceQuota middleware (biz.QuotaService): API key
// profile quotas aggregated from usage_logs in the general-settings timezone.
builder.Services.AddScoped<ApiKeyQuotaService>();
builder.Services.AddScoped<IApiKeyQuotaChecker>(
    static services => services.GetRequiredService<ApiKeyQuotaService>());
// The apiKeyQuotaUsages root reads the same service's ProfileQuotaUsages.
builder.Services.AddScoped<IApiKeyProfileQuotaReader>(
    static services => services.GetRequiredService<ApiKeyQuotaService>());

// The orchestrator reads the retry policy through the scoped system store and
// resolves candidates through the model-association selector
// (orchestrator.DefaultSelector, candidates.go:110-398). The association cache
// is process-wide like DefaultSelector.associationCache, so every scoped
// selector and the cache diagnostics export share the same entries.
builder.Services.AddSingleton<ModelAssociationCache>();
builder.Services.AddScoped<IModelCandidateSelector, ModelCandidateSelector>();
builder.Services.AddScoped<ChatCompletionOrchestrator>();

// lifecycle.New in server.New: stream executions stop when the server stops,
// independently from the client connection's lifetime.
builder.Services.AddSingleton<ServerLifetime>();

// Live stream preview (biz.LiveStreamRegistry + chunkbuffer): the
// orchestrator's live-preview middleware registers buffers for active streams,
// the chunk loaders and GET /admin/requests/:id/preview read them, and the
// hosted sweeper mirrors StartSweeper (5-minute tick, 10-minute idle
// threshold). The registry is process-wide by design: the preview contract is
// explicitly single-instance (request_live.go:66-87).
builder.Services.AddSingleton<LiveStreamRegistry>();
builder.Services.AddHostedService(
    static services => new LiveStreamRegistrySweeper(
        services.GetRequiredService<LiveStreamRegistry>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<ILogger<LiveStreamRegistrySweeper>>()));

// The owner-only cache diagnostics export runs the ordinary selector for its
// candidate preview; the reference's OwnerRule admits owners where the port's
// API-key privacy check would deny, so the preview uses its own bypass
// instance (reachable only behind the resolver's owner gate, §156).
builder.Services.AddScoped<
    ICacheDiagnosticsCandidateSelector,
    CacheDiagnosticsCandidateSelector>();
builder.Services.AddScoped<CacheDiagnosticsService>();

// Users and roles (biz.UserService / biz.RoleService). Passwords keep the
// reference's hex(bcrypt cost 10) format so rows are interchangeable with the
// Go implementation.
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddScoped<IUserAdminStore, UserAdminStore>();
builder.Services.AddScoped<IRoleStore, RoleStore>();
builder.Services.AddScoped<PermissionValidator>();

// User / Role / Project / UserProject ent privacy policies (§197). The
// reference evaluates them inside every ent query/mutation; the port evaluates
// the same decisions in the services, node/edge resolvers and stores.
builder.Services.AddScoped<UserPrivacy>();
builder.Services.AddScoped<RolePrivacy>();
builder.Services.AddScoped<ProjectPrivacy>();
builder.Services.AddScoped<UserProjectPrivacy>();
builder.Services.AddScoped<UserManagementService>();
builder.Services.AddScoped<RoleManagementService>();

// Invitations (biz.InvitationService): the REST-only surface of
// api/invitation.go — creation under the admin JWT gate plus the public
// lookup/registration pair. Role deletion revokes a role's invitations in the
// same transaction (role.go:258-265).
builder.Services.AddScoped<IInvitationStore, InvitationStore>();
builder.Services.AddScoped<InvitationService>();

// Antigravity OAuth management (api/antigravity.go): the PKCE session lives in
// the in-process cache (cache.mode defaults to "memory"), the token exchange
// and project discovery reuse the shared executor, and the optional URL proxy
// builds a per-request executor (C202).
builder.Services.AddSingleton<IProxyExecutorFactory, HttpProxyExecutorFactory>();

// Per-channel proxy-aware executors (channel_llm.go:113-130,700-774's
// Channel.HTTPClient): a channel with settings.proxy gets a cached client; the
// tester's per-request proxy builds a fresh override (§177).
builder.Services.AddSingleton<ChannelProxyExecutors>();

// Per-channel Copilot token exchanger (channel_llm.go:1218-1276): the reference
// keeps the provider — and its SHA-256 keyed Copilot-token cache — on the
// enabled-channel cache entry; the port reuses one exchanger per channel row
// (§194). Copilot deliberately stays off the token grain.
builder.Services.AddSingleton<ChannelCopilotTokenCache>();
builder.Services.AddSingleton<IAntigravityOAuthStateCache, AntigravityOAuthStateCache>();
builder.Services.AddScoped<AntigravityOAuthService>();

// Codex OAuth management (api/codex.go): the PKCE session cache is likewise
// in-process; start/exchange/decode sit on the admin group (routes.go:114-116).
builder.Services.AddSingleton<ICodexOAuthStateCache, CodexOAuthStateCache>();
builder.Services.AddScoped<CodexOAuthService>();

// xAI OAuth management (api/xai.go): a 30-minute PKCE session bound to the
// starting user, the token exchange, and the SSO-cookie device flow
// (routes.go:117-119).
builder.Services.AddSingleton<IXaiOAuthStateCache, XaiOAuthStateCache>();
builder.Services.AddScoped<XaiOAuthService>(static services => new XaiOAuthService(
    services.GetRequiredService<IXaiOAuthStateCache>(),
    services.GetRequiredService<IExecutor>(),
    services.GetRequiredService<IProxyExecutorFactory>(),
    services.GetRequiredService<IManagementIdentity>()));

// Claude Code OAuth management (api/claudecode.go): a 10-minute PKCE session.
builder.Services.AddSingleton<IClaudeCodeOAuthStateCache, ClaudeCodeOAuthStateCache>();
builder.Services.AddScoped<ClaudeCodeOAuthService>();

// GitHub Copilot device-flow management (api/copilot.go): the device state is
// cached for expires_in capped at 15 minutes and polled by session id.
builder.Services.AddSingleton<ICopilotDeviceFlowCache, CopilotDeviceFlowCache>();
builder.Services.AddScoped<CopilotOAuthService>();

// Projects (biz.ProjectService): CRUD over the projects table plus the
// project-level role presets and the creator's UserProject row. The project
// cache is not ported (C174).
builder.Services.AddScoped<ProjectManagementService>();

// Channel management reads (entgql channels / queryChannels / aggregates).
// ChannelPrivacy evaluates the Channel / ChannelModelPrice /
// ChannelModelPriceVersion query policies for node(Channel), the roots and the
// price read edges (§136); the channel mutation policy is evaluated by the
// same seam at the entry of every channel write resolver (C014).
builder.Services.AddScoped<IChannelAdminStore, ChannelAdminStore>();
builder.Services.AddScoped<ChannelPrivacy>();
builder.Services.AddScoped<ChannelQueryService>();

// Channel lifecycle writes (status / delete / bulk status / bulk delete /
// bulk tag management). The entgql Transactioner wraps each mutation; the
// store reproduces that atomicity with a per-mutation transaction.
builder.Services.AddScoped<IChannelWriteStore, ChannelWriteStore>();
builder.Services.AddScoped<ChannelLifecycleService>();

// The reference's "channel-disabled-api-key-cleanup" minute task
// (channel.go:204-223): the grain owns the cadence, the runner opens a fresh
// scope around the scoped lifecycle service. The reference runs the pass under
// authz.WithSystemBypass (channel_apikey.go:398); the cleanup path performs no
// Channel query policy evaluation.
builder.Services.AddSingleton<IChannelCredentialRecoveryRunner, GrainChannelCredentialRecoveryRunner>();

// Automatic credential/channel disabling (biz/channel_auto_disable.go, C236):
// the counter maps live for the process lifetime like the reference's
// ChannelService fields, the engine evaluates one failure at a time through the
// same singleton queue the metrics middleware feeds, and the lifecycle service
// is the credential mutation seam. The reference's performance goroutine runs
// under authz.WithSystemBypass (channel_internal.go:16-21); the adapter carries
// that bypass into the credential reads. The webhook notifier consumes the
// events published when a channel is auto-disabled.
builder.Services.AddSingleton<AutoDisableCounters>();
builder.Services.AddScoped<IChannelCredentialMutations, SystemChannelCredentialMutations>();
builder.Services.AddScoped<ChannelAutoDisableService>();
builder.Services.AddSingleton<ChannelAutoDisableEvaluationQueue>();
builder.Services.AddSingleton<IChannelAutoDisableEvaluationQueue>(
    static services => services.GetRequiredService<ChannelAutoDisableEvaluationQueue>());
builder.Services.AddHostedService(
    static services => services.GetRequiredService<ChannelAutoDisableEvaluationQueue>());
builder.Services.AddScoped<WebhookNotifier>();
builder.Services.AddSingleton<IWebhookNotificationSink, BackgroundWebhookNotificationSink>();

// Quota routing startup migration (biz/quota_routing_migration.go): one
// transaction claims quota_routing_migration_done, copies the legacy
// quota_enforcement_settings into quota_routing_settings when the new key is
// absent, rewrites the legacy allowedChannelIDs to ignore_quota, then flips
// the marker to "true". The reference launches it from ProviderQuotaService's
// OnStart hook in a goroutine; the hosted service keeps that non-blocking,
// log-only-failure timing (C183).
builder.Services.AddScoped<QuotaRoutingMigrationService>();
builder.Services.AddHostedService<QuotaRoutingMigrationHostedService>();

// Provider quota collection (biz/provider_quota.go,
// biz/provider_quota_settings.go, biz/channel_provider_quota_hook.go): the
// per-channel quota status table, the collection loop and its five-minute UTC
// reminder (the coordinator grain, §150), the synchronous routing-gate
// snapshot and the settings surface.
// The collection core is ported; the remaining one provider checker
// arrives in a follow-up slice (C182), so those providers reach the reference's
// "No checker for provider" branch. The service is a singleton because the
// routing gate reads its cache synchronously; every database operation runs in
// its own DI scope. ProviderQuotaService's startup cache load (the reference
// constructor call) and the settings snapshot seed run in a hosted service
// (C182).
builder.Services.AddScoped<IProviderQuotaStatusStore, ProviderQuotaStatusStore>();
builder.Services.AddSingleton<ProviderQuotaSettingsCache>();
builder.Services.AddSingleton<IProviderQuotaCheckerRegistry, ProviderQuotaCheckerRegistry>();

// Provider checkers: provider_quota.go:406-407 registers the Claude Code
// checker (a one-token request whose unified ratelimit headers carry the
// quota), provider_quota.go:418-420 the Codex checker (usage endpoint plus the
// optional reset-credit capability), provider_quota.go:422-424 the
// Antigravity checker (summary endpoint first, legacy per-model session quota
// as fallback), provider_quota.go:426-428 the xAI subscription checker
// (weekly + monthly billing payloads), provider_quota.go:430-432 the GitHub
// Copilot checker (the copilot_internal/user quota payload),
// provider_quota.go:434-436 the NanoGPT checker (the subscription usage
// endpoint's daily-image and daily/weekly-token windows) and
// provider_quota.go:438-440 the ZenMux checker (the management subscription
// endpoint's 5-hour and 7-day usage windows), provider_quota.go:442-444 the
// Cline checker (official usage-limits windows plus the account's ClinePass
// cost ledger), provider_quota.go:446-448 the Synthetic checker (the fixed
// /v2/quotas endpoint's rolling five-hour and weekly token pools) and
// provider_quota.go:450-452 the NeuralWatt checker (the fixed /v1/quota
// endpoint's monthly kWh subscription window), provider_quota.go:454-456
// the Apertis checker (the billing credits endpoint's PAYG token pool, account
// credits and subscription cycle) and provider_quota.go:458-460 the OpenCode Go
// checker (the fixed /zen/go/v1/usage endpoint's rolling, weekly and monthly
// percent windows) and provider_quota.go:462-464 the Kimi Code checker (the
// channel base URL's /usages endpoint's usage row, limit rows and booster
// wallet), provider_quota.go:466-468 the MiniMax checker (the channel base
// URL's token-plan remains endpoint's general-model 5-hour and weekly windows)
// and provider_quota.go:470-472 the ZhiPu checker (open.bigmodel.cn's per-key
// quota entries folded into rolling five-hour and weekly windows),
// provider_quota.go:474-476 the Zai checker (the fixed api.z.ai host sharing
// the ZhiPu-family collector), provider_quota.go:478-480 the Charm Hyper
// checker (the /v1/credits balance on the channel's host) and
// provider_quota.go:392 / 410-412 the Command Code checker (the alpha billing
// endpoints with the account key, degrading to the Studio cookie's internal
// endpoints) and provider_quota.go:393 / 414-416 the Ollama checker (the
// balance API authenticated with the account key, degrading to the settings
// page's session cookie).
// Checkers are stateless singletons sharing the process HTTP executor and the
// channel-proxy factory.
builder.Services.AddSingleton<ClaudeCodeQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "claudecode",
        services.GetRequiredService<ClaudeCodeQuotaChecker>()));

builder.Services.AddSingleton<CodexQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "codex",
        services.GetRequiredService<CodexQuotaChecker>()));

builder.Services.AddSingleton<AntigravityQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "antigravity",
        services.GetRequiredService<AntigravityQuotaChecker>()));

builder.Services.AddSingleton<XaiSubscriptionQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "xai_subscription",
        services.GetRequiredService<XaiSubscriptionQuotaChecker>()));

builder.Services.AddSingleton<GithubCopilotQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "github_copilot",
        services.GetRequiredService<GithubCopilotQuotaChecker>()));

builder.Services.AddSingleton<NanogptQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "nanogpt",
        services.GetRequiredService<NanogptQuotaChecker>()));

builder.Services.AddSingleton<ZenmuxQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "zenmux",
        services.GetRequiredService<ZenmuxQuotaChecker>()));

builder.Services.AddSingleton<ClineQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "cline",
        services.GetRequiredService<ClineQuotaChecker>()));

builder.Services.AddSingleton<SyntheticQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "synthetic",
        services.GetRequiredService<SyntheticQuotaChecker>()));

builder.Services.AddSingleton<NeuralWattQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "neuralwatt",
        services.GetRequiredService<NeuralWattQuotaChecker>()));

builder.Services.AddSingleton<ApertisQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "apertis",
        services.GetRequiredService<ApertisQuotaChecker>()));

builder.Services.AddSingleton<OpenCodeGoQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "opencode_go",
        services.GetRequiredService<OpenCodeGoQuotaChecker>()));

builder.Services.AddSingleton<KimiCodeQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "kimi_code",
        services.GetRequiredService<KimiCodeQuotaChecker>()));

builder.Services.AddSingleton<MinimaxQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "minimax",
        services.GetRequiredService<MinimaxQuotaChecker>()));

builder.Services.AddSingleton<ZhipuQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "zhipu",
        services.GetRequiredService<ZhipuQuotaChecker>()));

builder.Services.AddSingleton<ZaiQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "zai",
        services.GetRequiredService<ZaiQuotaChecker>()));

builder.Services.AddSingleton<CharmHyperQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "charm_hyper",
        services.GetRequiredService<CharmHyperQuotaChecker>()));

builder.Services.AddSingleton<CommandCodeQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "commandcode",
        services.GetRequiredService<CommandCodeQuotaChecker>()));

builder.Services.AddSingleton<OllamaQuotaChecker>();
builder.Services.AddSingleton<ProviderQuotaCheckerRegistration>(
    static services => new ProviderQuotaCheckerRegistration(
        "ollama",
        services.GetRequiredService<OllamaQuotaChecker>()));

builder.Services.AddScoped<ProviderQuotaCollectionSettingsService>();
builder.Services.AddScoped<IProviderQuotaCollectionSettingsService>(
    static services => services.GetRequiredService<ProviderQuotaCollectionSettingsService>());

// Claude Code safeguards settings (system_claude_code.go; post-baseline drift,
// §159). The server defaults mirror conf/conf.go:257-264; configuration-file
// binding is governed by C027.
builder.Services.AddSingleton(new ClaudeCodeSafeguardsServerDefaults());
builder.Services.AddScoped<ClaudeCodeSafeguardsSettingsService>();
builder.Services.AddScoped<IClaudeCodeSafeguardsSettingsService>(
    static services => services.GetRequiredService<ClaudeCodeSafeguardsSettingsService>());

// Codex compatibility settings and catalog test (system_codex.go,
// codex_catalog.go; post-baseline drift, §160). The catalog service keeps the
// reference's process-wide singleflight, so it is a singleton whose flights
// open their own DI scope.
builder.Services.AddScoped<CodexCompatibilitySettingsService>();
builder.Services.AddScoped<ICodexCompatibilitySettingsService>(
    static services => services.GetRequiredService<CodexCompatibilitySettingsService>());
builder.Services.AddSingleton<ICodexCatalogService, CodexCatalogService>();

builder.Services.AddSingleton<ProviderQuotaService>();
builder.Services.AddSingleton<IProviderQuotaStatusProvider>(
    static services => services.GetRequiredService<ProviderQuotaService>());
builder.Services.AddSingleton<IProviderQuotaInvalidator>(
    static services => services.GetRequiredService<ProviderQuotaService>());
builder.Services.AddSingleton<IProviderQuotaGroupRunner>(
    static services => services.GetRequiredService<ProviderQuotaService>());

// The quota collection's svc.mu becomes the singleton coordinator grain's
// turn (C230): the reminder replaces the five-minute cron, and manual checks,
// invalidations and post-reset refreshes queue behind a running sweep. Each
// due group is dispatched to the leader channel's quota grain, which keeps the
// channel's cache entry and serializes its checks.
builder.Services.AddSingleton<IQuotaGroupExecutor, GrainQuotaGroupExecutor>();
builder.Services.AddSingleton<IProviderQuotaCoordinatorClient, GrainProviderQuotaCoordinator>();
builder.Services.AddSingleton<IProviderQuotaCoordinatorOperations, GrainProviderQuotaOperations>();
builder.Services.AddSingleton<IProviderQuotaScheduleSource, GrainProviderQuotaScheduleSource>();
builder.Services.AddHostedService<ProviderQuotaInitializationHostedService>();

// Endpoint auto-detection (detectChannelEndpoints): read-only upstream probes
// built from the channel type's primary outbound and generic peers.
builder.Services.AddScoped<ChannelEndpointDetectService>();

// Channel connectivity tests (testChannel / testChannelAPIKeys /
// testChannelAPIKey): the tester reuses the chat pipeline with a specified
// channel and reads the test prompts from the system_channel_settings row
// (biz.SystemService.ChannelTestPrompts).
builder.Services.AddScoped<SystemChannelSettingsService>();
builder.Services.AddScoped<IChannelTestPromptsProvider>(
    static services => services.GetRequiredService<SystemChannelSettingsService>());
builder.Services.AddScoped<IChannelAutoSyncFrequencyProvider>(
    static services => services.GetRequiredService<SystemChannelSettingsService>());
builder.Services.AddScoped<ChannelTestService>();

// Channel probes (biz.ChannelProbeService): the channelProbeData read over the
// interval-aligned channel_probes buckets, gated by read_channels for the
// stored probe setting, and the probe writer. The reference's "* * * * *"
// UTC task is one Orleans reminder (C249); the service is scoped, so the
// last-execution guard is the singleton ChannelProbeRunState. The reference's
// unused RunProbeNow has no caller and is not exposed.
builder.Services.AddScoped<IChannelProbeStore, ChannelProbeStore>();
builder.Services.AddSingleton<ChannelProbeRunState>();
builder.Services.AddScoped<ChannelProbeService>();
builder.Services.AddSingleton<IChannelProbeRunRunner, GrainChannelProbeRunRunner>();

// Channel model sync (syncChannelModels): the per-channel fetch/merge/price
// transaction and the hourly schedule guard (channel_model_sync.go). A changed
// sync signals the enabled-channel cache grain, matching the reference's
// reloadChannelsAfterCommit. The execution window lives in the singleton
// ChannelModelSyncGrain so every silo shares one gate (§148; C228); the hosted
// service is the reference's scheduler.TaskSpec registration.
builder.Services.AddScoped<IChannelModelSyncStore, ChannelModelSyncStore>();
builder.Services.AddSingleton<IChannelModelSyncReloadHook, GrainChannelModelSyncReloadHook>();
builder.Services.AddSingleton<IChannelModelSyncGuard>(static services
    => new GrainChannelModelSyncGuard(
        services.GetRequiredService<IGrainFactory>(),
        services.GetRequiredService<ILogger<GrainChannelModelSyncGuard>>()));
builder.Services.AddScoped<ChannelModelSyncService>();
builder.Services.AddHostedService<ChannelModelSyncScheduler>();

// Built-in model catalog (biz/catalog.go): the process-wide snapshot cache with
// its bundled fallback data and the "providers-catalog-refresh" FixRate task.
// The reference reads CatalogSettingsOrDefault through the singleton system
// service; the port opens a scope per settings read (C238). The refresh
// scheduler also carries the settings mutation's reschedule seam, mirroring
// CatalogService.Reschedule(ctx, sched).
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<ICatalogService>(
    static services => services.GetRequiredService<CatalogService>());
builder.Services.AddSingleton<CatalogRefreshScheduler>();
builder.Services.AddSingleton<ICatalogRescheduleSignal>(
    static services => services.GetRequiredService<CatalogRefreshScheduler>());
builder.Services.AddHostedService(
    static services => services.GetRequiredService<CatalogRefreshScheduler>());

// Channel price writes (saveChannelModelPrices): the ordered action plan and
// its active/archived version history run in one store transaction
// (channel_price.go:374-508).
builder.Services.AddScoped<IChannelModelPriceStore, ChannelModelPriceStore>();
builder.Services.AddScoped<ChannelModelPriceService>();

// Channel override templates (channel_override_template.go): CRUD under the
// ent privacy rules plus the apply/clear settings rewrites. The management
// identity is the caller authenticated by the admin GraphQL gate
// (middleware.WithAdminGraphqlAuth); the owner default was a stand-in while
// the endpoint was open (C161).
builder.Services.AddScoped<IChannelOverrideTemplateStore, ChannelOverrideTemplateStore>();
builder.Services.AddScoped<ChannelOverrideTemplateService>();

// API keys (biz.APIKeyService): apiKeys / node(APIKey) and the create/update/
// status/profiles/rotate/bulk mutations under the APIKey privacy policy. The
// reference's ent cache invalidation is not ported (C176).
builder.Services.AddScoped<ApiKeyPrivacy>();
builder.Services.AddScoped<ApiKeyManagementService>();

// API key profile templates (biz.APIKeyProfileTemplateService): the
// apiKeyProfileTemplates connection, node(APIKeyProfileTemplate), the
// create/update/delete/load mutations and linkedProfilesCount under the
// template schema's own privacy rules.
builder.Services.AddScoped<IApiKeyProfileTemplateStore, ApiKeyProfileTemplateStore>();
builder.Services.AddScoped<ApiKeyProfileTemplatePrivacy>();
builder.Services.AddScoped<ApiKeyProfileTemplateService>();

// Models (biz.ModelService): models / queryModels / the association helpers /
// node(Model) and the create/update/status/delete/bulk mutations under the
// Model privacy policy (identical to the Channel policy). The model fetcher
// reuses the channel model catalog read by channel model sync.
builder.Services.AddScoped<IModelStore, ModelStore>();
builder.Services.AddScoped<ModelPrivacy>();
builder.Services.AddScoped<AuthorizationScope>();
builder.Services.AddScoped<IChannelModelCatalogStore>(
    static services => services.GetRequiredService<IChannelModelSyncStore>());
builder.Services.AddScoped<ModelFetcher>();
builder.Services.AddScoped<ModelManagementService>();

// Prompts (biz.PromptService management surface): prompts / node(Prompt) and
// the create/update/delete/status/bulk mutations under the Prompt privacy
// policy. The reference's asyncReloadPrompts cache refresh is not ported
// (C187/C188).
builder.Services.AddScoped<PromptPrivacy>();
builder.Services.AddScoped<PromptManagementService>();

// Data storages (biz.DataStorageService management surface): dataStorages /
// node(DataStorage) and the create/update mutations under the DataStorage
// privacy policy. The filesystem/object-store backends and the 30-minute
// ID/primary caches are not ported (C192/C196).
builder.Services.AddSingleton<DataStorageCache>();
builder.Services.AddScoped<DataStorageStore>();
builder.Services.AddScoped<IDataStorageStore>(static services =>
    new CachingDataStorageStore(
        services.GetRequiredService<DataStorageStore>(),
        services.GetRequiredService<DataStorageCache>()));
builder.Services.AddScoped<DataStoragePrivacy>();
builder.Services.AddScoped<DataStorageManagementService>();

// Backup / restore (internal/server/backup): the streamed backup document, the
// transactional restore and the auto-backup operations. The storage backends
// live in Storage/ (fs / S3 / GCS / WebDAV, C199); AutoBackupGrain carries the
// reference's daily 02:00 cron entry in the system time zone (§149), re-armed
// by the settings mutation through IAutoBackupRescheduleSignal. The reference's
// system-value cache invalidation is a no-op here.
builder.Services.AddScoped<IBackupBatchSource, BackupStore>();
builder.Services.AddScoped<IBackupRestoreStore, BackupRestoreStore>();
builder.Services.AddSingleton<DataStorageBackendFactory>();
builder.Services.AddSingleton<IDataStorageBackendFactory>(static services =>
    new CachingDataStorageBackendFactory(
        services.GetRequiredService<DataStorageBackendFactory>(),
        services.GetRequiredService<DataStorageCache>()));
builder.Services.AddScoped<BackupService>();
builder.Services.AddSingleton<IAutoBackupScheduleSource, GrainAutoBackupScheduleSource>();
builder.Services.AddSingleton<IAutoBackupRunner, GrainAutoBackupRunner>();
builder.Services.AddSingleton<IAutoBackupRescheduleSignal, GrainAutoBackupRescheduleSignal>();

// Video storage (internal/server/video_storage): the fixed-rate scan entry
// (C234) as one Orleans reminder; the settings mutation re-arms it through
// IVideoStorageRescheduleSignal. The scan itself runs the reference's
// scanAndSave in a fresh scope with its ten-minute budget.
builder.Services.AddScoped<VideoStorageWorker>();
builder.Services.AddSingleton<IVideoStorageScheduleSource, GrainVideoStorageScheduleSource>();
builder.Services.AddSingleton<IVideoStorageScanRunner, GrainVideoStorageScanRunner>();
builder.Services.AddSingleton<IVideoStorageRescheduleSignal, GrainVideoStorageRescheduleSignal>();

// Garbage collection (internal/server/gc): the storage-policy-driven cleanup
// worker and its "gc" cron entry in UTC. The reference registers the task
// through fx.Invoke during app construction, so an unparsable gc.cron fails
// startup; parsing eagerly reproduces that. The fixed three-day channel-probes
// pass and the probe writer's table are both wired (C249); the reference's
// process-wide data-storage caches are not ported (C196).
var gcOptions = GcOptions.FromConfiguration(builder.Configuration);
GcCronSchedule gcSchedule;
try
{
    gcSchedule = new GcCronSchedule(gcOptions.Cron);
}
catch (InvalidOperationException ex)
{
    throw new InvalidOperationException(
        "register task \"gc\": " + ex.Message,
        ex);
}

builder.Services.AddSingleton(gcOptions);
builder.Services.AddSingleton(gcSchedule);
builder.Services.AddScoped<IGcStore, GcStore>();
builder.Services.AddScoped<IGcCleanupService, GcWorker>();
builder.Services.AddSingleton<IGcScheduleSource>(static services =>
    new GrainGcScheduleSource(services.GetRequiredService<GcCronSchedule>()));
builder.Services.AddSingleton<IGcCleanupRunner, GrainGcCleanupRunner>();

// Trace/thread management surface (biz.TraceService / biz.ThreadService admin
// reads, the status transitions and the trace segment tree). The reference
// runs every access through an ent client carrying the privacy policy in the
// context; the port evaluates the same policies explicitly (C189). The admin
// stores are distinct from the request-path ITraceStore/IThreadStore.
builder.Services.AddScoped<ITraceAdminStore, TraceAdminStore>();
builder.Services.AddScoped<IThreadAdminStore, ThreadAdminStore>();
builder.Services.AddScoped<IRequestSegmentStore, RequestSegmentStore>();
builder.Services.AddScoped<IUsageMetadataStore, UsageMetadataStore>();
builder.Services.AddScoped<TracePrivacy>();

// Systems / OIDCIdentity query policies for the root systems and
// oidcIdentities connections (schema/system.go:52-64, oidc_identity.go:83-95).
builder.Services.AddScoped<SystemPrivacy>();
builder.Services.AddScoped<TraceSegmentService>();
builder.Services.AddScoped<TraceManagementService>();
builder.Services.AddScoped<ThreadManagementService>();
builder.Services.AddScoped<RequestManagementService>();
builder.Services.AddScoped<RequestQueryService>();
builder.Services.AddScoped<UsageLogManagementService>();
builder.Services.AddScoped<AnalyticsService>();
builder.Services.AddScoped<DashboardService>();

// Management identity: the gate middleware writes the authenticated caller
// into HttpContext.Items; the scoped seam reads it back for the resolvers.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IManagementIdentity, HttpContextManagementIdentity>();

// Self-service (me.graphql): me / myProjects / updateMe / updateMyPassword /
// unlinkOIDCIdentity read the current user's edges (roles, project users, OIDC
// identities) and reproduce UserService.UpdateOwnProfile (user.go:169-220).
builder.Services.AddScoped<IUserProfileStore, UserProfileStore>();
builder.Services.AddScoped<UserProfileService>();

// OIDC providers (oidc.providers): the registry reproduces NewOIDCService's
// startup checks (normalization, duplicate ids, discovery, manual endpoints,
// icon inlining). The reference fails startup on a bad provider list; the
// hosted service keeps that timing (C014/C168).
var oidcProviders = OidcProviderConfig.Load(builder.Configuration.GetSection("Oidc"));
builder.Services.AddSingleton(oidcProviders);
builder.Services.AddSingleton<IOidcHttpClient>(
    static _ => new OidcHttpClient(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
builder.Services.AddSingleton<OidcProviderRegistry>();
builder.Services.AddHostedService<OidcInitializationHostedService>();
builder.Services.AddScoped<OidcService>();

// The OIDC login flow (oidc.go:509-1300): state/PKCE caches, the oauth2 token
// exchange, ID-token/JWKS verification, UserInfo, and the exchange-code store.
// The reference's default cache.mode is "memory"; the Redis modes are not
// ported (C171).
var oidcLoginOptions = OidcLoginOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(oidcLoginOptions);
builder.Services.AddSingleton<IOidcCache, OidcMemoryCache>();
builder.Services.AddSingleton<IOidcTokenClient>(
    static _ => new OidcTokenClient(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
builder.Services.AddSingleton<IOidcJwksClient>(
    static _ => new OidcJwksClient(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
builder.Services.AddSingleton<IOidcIdTokenVerifier, OidcIdTokenVerifier>();
builder.Services.AddSingleton<IOidcUserInfoClient>(
    static _ => new OidcUserInfoClient(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
builder.Services.AddScoped<IOidcLoginStore, OidcLoginStore>();
builder.Services.AddScoped<OidcLoginService>();

// Management login (biz.AuthService): signin/refresh issue HS256 tokens signed
// with the systems.system_jwt_secret_key row. The linked-provider check now
// reads the configured oidc_login_only flags.
builder.Services.AddSingleton<IOidcLoginOnlyProviders, OidcLoginOnlyProviders>();
builder.Services.AddScoped<ManagementAuthService>();

// Management GraphQL (HotChocolate 16): the admin UI posts to /admin/graphql
// (routes.go:163). The endpoint exposes the ported SDL slices; the reference's
// WithAdminGraphqlAuth / WithProjectID gate is wired below.
builder.Services.AddAxonHubGraphQL();

// The OpenAPI GraphQL surface is a second, independent gqlgen server in the
// reference (internal/server/gql/openapi/graphql.go); the port hosts it as a
// named HotChocolate schema.
builder.Services.AddAxonHubOpenApiGraphQL();

builder.Host.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddMemoryGrainStorage("Default");

    // The reminder-driven management tasks need a reminder table; the in-memory
    // one keeps the reference's per-process cron state (a stop loses pending
    // occurrences and nothing is backfilled).
    silo.UseInMemoryReminderService();
});

// server.pid_file (conf/conf.go:243, main.go:70-100): write the PID before
// the HTTP server starts and remove it on stop. An empty value keeps the
// reference's no-op hook.
builder.Services.AddHostedService<PidFileHostedService>();

// The startup half of the reference's scheduled-task registration
// (backup service.go:39-55, gc gc.go:77-84). Registered after UseOrleans so
// the hosted service starts after the silo.
builder.Services.AddHostedService<GrainSchedulerActivator>();

var app = builder.Build();

// Server.Shutdown cancels the lifecycle before HTTP shutdown (server.go:101-109);
// ApplicationStopping is the host's equivalent hook. Go passes
// context.Cause(ctx), which is nil during a normal shutdown, so the manager
// substitutes "server shutdown".
var serverLifetime = app.Services.GetRequiredService<ServerLifetime>();
app.Lifetime.ApplicationStopping.Register(() => serverLifetime.Shutdown());

// SIGHUP config reload (config_reload_unix.go): re-read the startup config
// file and re-apply only server.ip_access_control. Registered after the
// lifetime callback so shutdown stops the signal loop first, matching the
// reference's hook ordering.
var configReload = new ConfigReloadService(
    (IConfigurationRoot)app.Configuration,
    ipAccessControl,
    app.Services.GetRequiredService<ILogger<ConfigReloadService>>(),
    ConfigReloadService.FindConfigFile(
        app.Environment.ContentRootPath,
        app.Environment.EnvironmentName));
app.Lifetime.ApplicationStarted.Register(configReload.Start);
app.Lifetime.ApplicationStopping.Register(configReload.Stop);

// server.request_timeout / server.llm_request_timeout (conf/conf.go:223-224),
// read once at startup like the reference's immutable server.Config.
var requestTimeouts = RequestTimeoutSettings.FromConfiguration(app.Configuration);

// The reference builds the MeterProvider in fx at startup and calls Shutdown
// from OnStop; resolving the singleton here starts the periodic reader (when
// enabled) and leaves the final flush to the host's disposal.
_ = app.Services.GetRequiredService<MeterProvider>();

// Ent runs auto-migration on every startup; the port keeps the idempotent DDL.
await using (var connection = await app.Services.GetRequiredService<PostgresConnectionFactory>().OpenAsync())
{
    await DatabaseMigrator.EnsureCreatedAsync(connection);

    // datamigrate.Migrator.Run skips every version-tracked migration until
    // the system is initialized (datamigrate/migrator.go). A fresh database
    // gets its Primary storage from the initialize transaction instead; an
    // initialized one still runs the idempotent v0.4.0 seed.
    if (await DataStorageBootstrap.IsSystemInitializedAsync(connection))
    {
        await DataStorageBootstrap.EnsurePrimaryDataStorageAsync(connection);
    }
}

// routes.go:57: IP access control is the very first global middleware. A denied
// request is answered here (302 when redirect_url is set, otherwise 404) and
// never reaches tracing, metrics, auth, the static fallback or a route.
app.UseMiddleware<IpAccessControlMiddleware>();

// Public system surface (routes.go:81-99): /health, /admin/system/status and
// /admin/system/initialize. The two admin routes sit on the reference's
// unSecureAdminGroup and are deliberately outside every auth middleware.
app.MapSystemRoutes();

// Unauthenticated admin auth (routes.go:97-98): signin and refresh sit on the
// unSecureAdminGroup, so they stay outside the gate below.
app.MapPost("/admin/auth/signin", ManagementAuthHandlers.SignInAsync);
app.MapPost("/admin/auth/refresh", ManagementAuthHandlers.RefreshAsync);

// OIDC public surface (routes.go:101-104): the login page lists the configured
// providers before any credentials exist; the optional bearer only decorates
// the list with the caller's linked identities.
RequestDelegate oidcProvidersHandler = context => OidcHandlers.GetProvidersAsync(
    context,
    context.RequestServices.GetRequiredService<OidcService>(),
    context.RequestServices.GetRequiredService<ManagementAuthService>(),
    context.RequestAborted);
app.MapGet("/oauth/oidc/providers", oidcProvidersHandler);

// The public login flow (oidc.go:74-81): authorize, IdP callback and the
// one-time exchange code redemption.
app.MapOidcLoginRoutes();

// Invitations (routes.go:87-88,112): POST /admin/invitations behind the admin
// JWT gate, and the public GET/register pair under /auth/invitations.
app.MapInvitationRoutes();

// Antigravity OAuth (routes.go:124-125): both routes sit on the admin group.
app.MapAntigravityOAuthRoutes();

// Codex OAuth (routes.go:114-116): start, exchange and auth.json decode all
// sit on the admin group.
app.MapCodexOAuthRoutes();

// xAI OAuth (routes.go:117-119): start, exchange and the SSO cookie decode.
app.MapXaiOAuthRoutes();

// Claude Code OAuth (routes.go:120-121).
app.MapClaudeCodeOAuthRoutes();

// GitHub Copilot device flow (routes.go:127-128).
app.MapCopilotOAuthRoutes();

// Request detail helpers (routes.go:145-152): the binary content download and
// the preview fallback.
app.MapRequestDetailRoutes();

// NewOIDCHandlers warns when providers are configured without a public URL
// (api/oidc.go:36-41). An empty value still fails each flow at getBaseURL.
if (oidcProviders.Count > 0 && oidcLoginOptions.PublicUrl.Length == 0)
{
    LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(0, "OidcPublicUrlMissing"),
        "OIDC is enabled but server.public_url is not configured. "
        + "This is insecure and can lead to Host header injection attacks in production.")(
        app.Logger,
        null);
}

// The management GraphQL endpoint the frontend calls
// (frontend/src/gql/graphql.ts:36). The reference mounts the route POST-only
// (routes.go:163); a GET falls through to the static fallback and its gin
// 404 envelope.
app.MapGraphQL("/admin/graphql")
    .WithOptions(static options => options.EnableGetRequests = false)
    .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));

// The OpenAPI GraphQL endpoint (routes.go:164,175): a separate gqlgen schema
// mounted POST-only on the openapi group. GET is never a transport in the
// reference either.
app.MapGraphQL("/openapi/v1/graphql", "openapi")
    .WithOptions(static options => options.EnableGetRequests = false)
    .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));

// The admin UI's GraphiQL page (routes.go:157): the gqlgen playground handler
// sits on the admin group.
app.MapGet("/admin/playground", PlaygroundPages.ServeAdminAsync);

// The playground chat (routes.go:134-140): POST only, so other methods fall
// through to gin's NoRoute and the static 404 envelope (C116).
app.MapPost("/admin/playground/chat", PlaygroundEndpoint.HandleAsync);

// The OpenAPI playground and webhook echo (routes.go:171,176): both sit on the
// openapi group.
app.MapGet("/openapi/v1/playground", PlaygroundPages.ServeOpenApiAsync);
app.MapPost("/openapi/webhook/echo", WebhookEchoEndpoint.HandleAsync);

// The downstream Responses WebSocket mode (responses_websocket.go) upgrades
// GET /v1/responses and GET /codex/responses. ASP.NET Core needs the WebSocket
// middleware in the pipeline; the reference's 2.5 minute pings and 5 minute
// read deadline map onto the keep-alive pair (C244).
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = ResponsesWebSocketEndpoint.ResponsesWebSocketPingInterval,
    KeepAliveTimeout = TimeSpan.FromMinutes(5),
});

// Global request tracing (routes.go:63): every request gets an ar-<uuid> id
// echoed in the configured response header and published for error bodies.
app.UseMiddleware<RequestTracingMiddleware>();

// Global metrics (routes.go:64): WithMetrics follows the logging/tracing
// middlewares and wraps every route plus the static NoRoute fallback. It
// records after the response with the gin FullPath() template; the fallback
// (no gin route) records the empty path.
app.UseMiddleware<HttpMetricsMiddleware>();

// routes.go:67-79: CORS follows metrics and is mounted only when enabled. The
// reference's catch-all OPTIONS route has no ASP.NET Core equivalent; unmatched
// preflights have no endpoint metadata, so the middleware applies this default
// policy to them as well.
if (corsSettings.Enabled)
{
    app.UseCors();
}

// Group timeouts (routes.go:81-104). They are registered after the global
// middlewares, matching gin: the globals run before the group's
// WithTimeout(context.WithTimeout) hook replaces the request context.
// publicGroup: favicon, health and the two invitation routes.
app.UseWhen(
    static context =>
    {
        var path = context.Request.Path;
        return (HttpMethods.IsGet(context.Request.Method)
                && (path.Value == "/favicon"
                    || path.Value == "/health"
                    || path.StartsWithSegments("/auth/invitations")))
            || (HttpMethods.IsPost(context.Request.Method)
                && path.StartsWithSegments("/auth/invitations"));
    },
    branch => branch.UseMiddleware<RequestTimeoutMiddleware>(requestTimeouts.RequestTimeout));

// unSecureAdminGroup: status/initialize/signin/refresh carry
// server.request_timeout and stay outside every auth middleware.
app.UseWhen(
    static context =>
    {
        var path = context.Request.Path;
        return (HttpMethods.IsGet(context.Request.Method)
                && path.Value == "/admin/system/status")
            || (HttpMethods.IsPost(context.Request.Method)
                && (path.Value == "/admin/system/initialize"
                    || path.Value == "/admin/auth/signin"
                    || path.Value == "/admin/auth/refresh"));
    },
    branch => branch.UseMiddleware<RequestTimeoutMiddleware>(requestTimeouts.RequestTimeout));

// oauthGroup: every OIDC login route carries server.request_timeout.
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/oauth"),
    branch => branch.UseMiddleware<RequestTimeoutMiddleware>(requestTimeouts.RequestTimeout));

// routes.go:163 mounts POST only, so a non-POST /admin/graphql never matches
// the route and gin's NoRoute hands it to static.Handler, whose API-prefix
// branch answers the JSON 404 envelope. HotChocolate's endpoint would match
// other methods and answer 405, so short-circuit them before it (C116).
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/admin/graphql")
        && !HttpMethods.IsPost(context.Request.Method),
    static branch => branch.Run(context =>
        StaticHandler.WriteApiNotFoundAsync(context, context.Request.Path.Value ?? "/")));

// The admin GraphQL gate (routes.go:161-168): WithAdminGraphqlAuth then
// WithProjectID, plus the service-account read-only wrapper the reference
// installs as a gqlgen AroundOperations hook (gql/graphql.go:187-204). The
// reference mounts these on the POST route group, so a GET /admin/graphql
// never sees them; the port branches on the method to match.
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/admin/graphql")
        && HttpMethods.IsPost(context.Request.Method),
    static branch =>
    {
        branch.UseMiddleware<AdminGraphqlAuthMiddleware>();
        branch.UseMiddleware<ProjectIdMiddleware>();
        branch.UseMiddleware<ApiKeyReadOnlyMiddleware>();
    });

// routes.go:163: WithTimeout(server.request_timeout) follows the two auth
// middlewares on the POST route.
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/admin/graphql")
        && HttpMethods.IsPost(context.Request.Method),
    branch => branch.UseMiddleware<RequestTimeoutMiddleware>(requestTimeouts.RequestTimeout));

// routes.go:175 mounts POST only, so a non-POST /openapi/v1/graphql never
// matches the route and gin's NoRoute hands it to static.Handler (C116).
app.UseWhen(
    static context => context.Request.Path.Value == "/openapi/v1/graphql"
        && !HttpMethods.IsPost(context.Request.Method),
    static branch => branch.Run(context =>
        StaticHandler.WriteApiNotFoundAsync(context, context.Request.Path.Value ?? "/")));

// The gqlgen playground handler is mounted only on GET (routes.go:157,171), so
// a HEAD request falls through to gin's NoRoute instead of serving the page.
// Endpoint routing matches HEAD against GET endpoints implicitly; restore the
// reference's 404 envelope before any group gate runs.
app.UseWhen(
    static context => HttpMethods.IsHead(context.Request.Method)
        && (context.Request.Path.Value == "/admin/playground"
            || context.Request.Path.Value == "/openapi/v1/playground"),
    static branch => branch.Run(context =>
        StaticHandler.WriteApiNotFoundAsync(context, context.Request.Path.Value ?? "/")));

// The openapi group gate (routes.go:169-178): WithIPBlocklist then
// WithOpenAPIAuth. Like every gin group, it only runs for matched routes, so
// the static fallback stays outside it (C116). The group's WithTimeout is not
// ported (C115/C014 family).
app.UseWhen(
    static context => !IsStaticFallback(context)
        && (context.Request.Path.Value == "/openapi/v1/graphql"
            || context.Request.Path.Value == "/openapi/v1/playground"
            || context.Request.Path.Value == "/openapi/webhook/echo"),
    static branch =>
    {
        branch.UseMiddleware<IpBlocklistMiddleware>();
        branch.UseMiddleware<OpenApiAuthMiddleware>();
    });

// The admin GraphiQL page sits on the admin group (routes.go:106,157): the
// plain user-JWT gate then WithProjectID.
app.UseWhen(
    static context => context.Request.Path.Value == "/admin/playground"
        && HttpMethods.IsGet(context.Request.Method),
    static branch =>
    {
        branch.UseMiddleware<AdminJwtAuthMiddleware>();
        branch.UseMiddleware<ProjectIdMiddleware>();
    });

// routes.go:109: GET /admin/playground appends WithTimeout(request_timeout)
// after the admin group middlewares.
app.UseWhen(
    static context => context.Request.Path.Value == "/admin/playground"
        && HttpMethods.IsGet(context.Request.Method),
    branch => branch.UseMiddleware<RequestTimeoutMiddleware>(requestTimeouts.RequestTimeout));

// The playground chat also sits on the admin group (routes.go:106,134): the
// same plain user-JWT gate and WithProjectID. Like every gin group middleware,
// it only runs for the matched POST route (C116).
app.UseWhen(
    static context => context.Request.Path.Value == "/admin/playground/chat"
        && HttpMethods.IsPost(context.Request.Method),
    static branch =>
    {
        branch.UseMiddleware<AdminJwtAuthMiddleware>();
        branch.UseMiddleware<ProjectIdMiddleware>();
    });

// routes.go:134-140: the playground chat uses WithTimeout(llm_request_timeout)
// (plus WithSource/WithResponseHeaders, already handled by the endpoint).
app.UseWhen(
    static context => context.Request.Path.Value == "/admin/playground/chat"
        && HttpMethods.IsPost(context.Request.Method),
    branch => branch.UseMiddleware<RequestTimeoutMiddleware>(requestTimeouts.LlmRequestTimeout));

// The OIDC link flow sits on the admin group (routes.go:106,131): the plain
// user-JWT gate then WithProjectID, with no API-key fallback.
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/admin/oidc/link"),
    static branch =>
    {
        branch.UseMiddleware<OidcLinkAuthMiddleware>();
        branch.UseMiddleware<ProjectIdMiddleware>();
    });

// Invitation creation sits on the admin group (routes.go:106,112): the same
// plain user-JWT gate and WithProjectID. The public lookup/registration pair
// (routes.go:87-88) stays outside every auth middleware.
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/admin/invitations"),
    static branch =>
    {
        branch.UseMiddleware<AdminJwtAuthMiddleware>();
        branch.UseMiddleware<ProjectIdMiddleware>();
    });

// The Antigravity OAuth pair sits on the admin group (routes.go:106,124-125):
// the same plain user-JWT gate and WithProjectID.
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/admin/antigravity/oauth"),
    static branch =>
    {
        branch.UseMiddleware<AdminJwtAuthMiddleware>();
        branch.UseMiddleware<ProjectIdMiddleware>();
    });

// The Codex OAuth surface (start / exchange / auth decode) sits on the admin
// group (routes.go:106,114-116): the same plain user-JWT gate and
// WithProjectID.
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/admin/codex"),
    static branch =>
    {
        branch.UseMiddleware<AdminJwtAuthMiddleware>();
        branch.UseMiddleware<ProjectIdMiddleware>();
    });

// The xAI OAuth surface (start / exchange / sso) sits on the admin group
// (routes.go:106,117-119): the same plain user-JWT gate and WithProjectID.
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/admin/xai"),
    static branch =>
    {
        branch.UseMiddleware<AdminJwtAuthMiddleware>();
        branch.UseMiddleware<ProjectIdMiddleware>();
    });

// The Claude Code OAuth pair sits on the admin group (routes.go:106,120-121).
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/admin/claudecode"),
    static branch =>
    {
        branch.UseMiddleware<AdminJwtAuthMiddleware>();
        branch.UseMiddleware<ProjectIdMiddleware>();
    });

// The GitHub Copilot device-flow pair sits on the admin group
// (routes.go:106,127-128).
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/admin/copilot"),
    static branch =>
    {
        branch.UseMiddleware<AdminJwtAuthMiddleware>();
        branch.UseMiddleware<ProjectIdMiddleware>();
    });

// The request detail helpers (content download and preview) sit on the admin
// group (routes.go:106,145-152): the same plain user-JWT gate and
// WithProjectID.
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/admin/requests"),
    static branch =>
    {
        branch.UseMiddleware<AdminJwtAuthMiddleware>();
        branch.UseMiddleware<ProjectIdMiddleware>();
    });

// routes.go:142-151: the content download and preview append
// WithTimeout(request_timeout) after the admin group middlewares.
app.UseWhen(
    static context => context.Request.Path.StartsWithSegments("/admin/requests")
        && HttpMethods.IsGet(context.Request.Method),
    branch => branch.UseMiddleware<RequestTimeoutMiddleware>(requestTimeouts.RequestTimeout));

// routes.go:193-195: WithTimeout(llm_request_timeout) is the first apiGroup
// middleware. routes.go:197-201 keeps GET /v1/responses on a group without the
// single-request timeout; the WebSocket handler applies it per event instead.
app.UseWhen(
    static context =>
    {
        if (IsStaticFallback(context))
        {
            return false;
        }

        var path = context.Request.Path;
        var inApiGroup = path.StartsWithSegments("/v1")
            || path.StartsWithSegments("/anthropic/v1")
            || path.StartsWithSegments("/jina/v1")
            || path.StartsWithSegments("/typesafe/v1")
            || path.StartsWithSegments("/doubao/v3");
        return inApiGroup
            && !(HttpMethods.IsGet(context.Request.Method)
                && path.Value == "/v1/responses");
    },
    branch => branch.UseMiddleware<RequestTimeoutMiddleware>(requestTimeouts.LlmRequestTimeout));

// The reference mounts WithIPBlocklist + WithAPIKeyConfig on its API group, in
// that order. The slice applies both to every ported API surface: the openai,
// jina, typesafe and anthropic groups all hang off the same apiGroup.
app.UseWhen(
    static context => !IsStaticFallback(context)
        && (context.Request.Path.StartsWithSegments("/v1")
            || context.Request.Path.StartsWithSegments("/anthropic/v1")
            || context.Request.Path.StartsWithSegments("/jina/v1")
            || context.Request.Path.StartsWithSegments("/typesafe/v1")
            || context.Request.Path.StartsWithSegments("/doubao/v3")),
    static branch =>
    {
        branch.UseMiddleware<IpBlocklistMiddleware>();
        branch.UseMiddleware<ApiKeyAuthMiddleware>();

        // routes.go:185-192: WithThread then WithTrace, both after the API key.
        branch.UseMiddleware<ThreadMiddleware>();
        branch.UseMiddleware<TraceMiddleware>();
    });

// The Codex compatibility surface (codex_routes.go:10-29): IP blocklist,
// the Enabled gate (system bypass), strict API key auth (no no-auth fallback),
// then the thread/trace pair. WithSource/WithResponseHeaders stay unregistered
// like the rest of the port; the reference's HTTP group appends
// WithTimeout(llm_request_timeout) after the common middlewares (the WebSocket
// group below has none).
app.UseWhen(
    static context => !IsStaticFallback(context)
        && context.Request.Path.StartsWithSegments("/codex"),
    static branch =>
    {
        branch.UseMiddleware<IpBlocklistMiddleware>();
        branch.UseMiddleware<CodexCompatibilityEnabledMiddleware>();
        branch.UseMiddleware<StrictApiKeyAuthMiddleware>();
        branch.UseMiddleware<ThreadMiddleware>();
        branch.UseMiddleware<TraceMiddleware>();
    });

// codex_routes.go:20,27-28: the HTTP group gets the LLM deadline; GET
// /codex/responses stays on the websocket group without it.
app.UseWhen(
    static context => !IsStaticFallback(context)
        && context.Request.Path.StartsWithSegments("/codex")
        && !(HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.Value == "/codex/responses"),
    branch => branch.UseMiddleware<RequestTimeoutMiddleware>(requestTimeouts.LlmRequestTimeout));

// routes.go:266-289: WithTimeout(llm_request_timeout) is the first middleware
// on both the /gemini/:version group and the /v1beta alias.
app.UseWhen(
    static context => !IsStaticFallback(context)
        && (context.Request.Path.StartsWithSegments("/v1beta")
            || context.Request.Path.StartsWithSegments("/gemini")),
    branch => branch.UseMiddleware<RequestTimeoutMiddleware>(requestTimeouts.LlmRequestTimeout));

// The native Gemini surface (routes.go:266-289): the /gemini/:version group
// and the /v1beta alias run IP blocklist + Gemini key auth (query `key` or
// the regular header extractor) instead of WithAPIKeyConfig, then the same
// thread/trace pair. WithSource/WithResponseHeaders stay unregistered like the
// rest of the port.
app.UseWhen(
    static context => !IsStaticFallback(context)
        && (context.Request.Path.StartsWithSegments("/v1beta")
            || context.Request.Path.StartsWithSegments("/gemini")),
    static branch =>
    {
        branch.UseMiddleware<IpBlocklistMiddleware>();
        branch.UseMiddleware<GeminiKeyAuthMiddleware>();
        branch.UseMiddleware<ThreadMiddleware>();
        branch.UseMiddleware<TraceMiddleware>();
    });

// Models (routes.go:209-210 and :250): the OpenAI list/retrieve pair and the
// Anthropic-compatible list, all over ModelService.ListEnabledModels.
app.MapGet("/v1/models", ModelsEndpoint.ListOpenAiModelsAsync);
app.MapGet("/v1/models/{*model}", ModelsEndpoint.RetrieveOpenAiModelAsync);
app.MapGet("/anthropic/v1/models", ModelsEndpoint.ListAnthropicModelsAsync);

app.MapPost("/v1/chat/completions", ChatCompletionEndpoint.HandleAsync);

// Legacy Completions API (routes.go: openaiGroup.POST("/completions")): the
// reference's CompletionHandlers.ChatCompletion reuses the shared chat handler
// with the completion inbound transformer.
static Task HandleCompletionsAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator)
    => ChatCompletionEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(new CompletionInboundTransformer()));

app.MapPost("/v1/completions", HandleCompletionsAsync);

// Anthropic Messages: both spellings the reference mounts in routes.go.
// internal/server/api/anthropic.go:55-75 wires the Claude Code safeguards into
// this handler: the static server config, the settings provider and a reviewer
// over an OpenAI-inbound copy of the same orchestrator.
static Task HandleAnthropicMessagesAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator,
    AnthropicInboundTransformer anthropic,
    IClaudeCodeSafeguardsSettingsService safeguardsSettings,
    ClaudeCodeSafeguardsServerDefaults safeguardsDefaults,
    ILoggerFactory loggerFactory)
{
    var safeguards = new SafeguardsEndpointOptions
    {
        Config = ClaudeCodeSafeguardsCodec.ToSafeguardsConfig(safeguardsDefaults),
        ConfigProvider = async cancellationToken =>
            ClaudeCodeSafeguardsCodec.ToSafeguardsConfig(
                await safeguardsSettings.GetRuntimeConfigAsync(cancellationToken)),
        ClassifierFactory = static (parent, identity, request) =>
            SafeguardsClassifier.Create(parent, identity, request.SessionId, request.SessionScope),
        Logger = loggerFactory.CreateLogger<SafeguardsEvaluator>(),
    };

    return ChatCompletionEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(anthropic),
        safeguards: safeguards);
}

app.MapPost("/v1/messages", HandleAnthropicMessagesAsync);
app.MapPost("/anthropic/v1/messages", HandleAnthropicMessagesAsync);

// Responses API (routes.go: openaiGroup.POST("/responses") and
// "/responses/compact"). Streaming is not ported in this slice.
static Task HandleResponsesAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator,
    ResponsesInboundTransformer responses)
    => ChatCompletionEndpoint.HandleAsync(context, orchestrator.WithInbound(responses));

static Task HandleResponsesCompactAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator,
    ResponsesCompactInboundTransformer compact)
    => ChatCompletionEndpoint.HandleAsync(context, orchestrator.WithInbound(compact));

// The WebSocket sibling owns a long-lived connection and applies no group
// timeout (routes.go:200-204); each frame's response.create event is processed
// with server.llm_request_timeout (responses_websocket.go:267-283). A plain GET
// answers the gin 400 envelope.
Task HandleResponsesWebSocketAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator,
    ResponsesInboundTransformer responses)
    => ResponsesWebSocketEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(responses),
        requestTimeouts.LlmRequestTimeout);

app.MapPost("/v1/responses", HandleResponsesAsync);
app.MapPost("/v1/responses/compact", HandleResponsesCompactAsync);
app.MapGet("/v1/responses", HandleResponsesWebSocketAsync);

// Images API (routes.go:215-216): CreateImage/CreateImageEdit delegate to the
// shared chat handler with the images inbound transformer. Variations stays
// unregistered, matching the reference's "DO NOT SUPPORT IMAGE VARIATION".
static Task HandleImageGenerationAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator)
    => ChatCompletionEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(ImageInboundTransformer.Generation()));

static Task HandleImageEditAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator)
    => ChatCompletionEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(ImageInboundTransformer.Edit()));

app.MapPost("/v1/images/generations", HandleImageGenerationAsync);
app.MapPost("/v1/images/edits", HandleImageEditAsync);

// Codex compatibility runtime surface (codex_routes.go:15-28): the whoami and
// accounts stubs, the catalog-filtered model list, and the OpenAI image /
// response handlers reused under /codex, except the websocket group, which
// registers after the HTTP group and has no group timeout.
app.MapGet("/codex/v1/user-auth-credential/whoami", CodexEndpoint.WhoamiAsync);
app.MapGet("/codex/api/codex/accounts/check", CodexEndpoint.AccountsCheckAsync);
app.MapGet("/codex/models", CodexEndpoint.ListModelsAsync);
app.MapPost("/codex/images/generations", HandleImageGenerationAsync);
app.MapPost("/codex/images/edits", HandleImageEditAsync);
app.MapPost("/codex/responses", HandleResponsesAsync);
app.MapGet("/codex/responses", HandleResponsesWebSocketAsync);

// Video tasks (routes.go:217-219): creation reuses the shared chat handler with
// the video inbound transformer; GET/DELETE run the biz.VideoService task
// query/delete flow (api.OpenAIHandlers.GetVideo/DeleteVideo).
static Task HandleVideoGenerationAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator)
    => ChatCompletionEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(new VideoInboundTransformer()));

app.MapPost("/v1/videos", HandleVideoGenerationAsync);
app.MapGet("/v1/videos/{id}", VideoEndpoint.GetVideoAsync);
app.MapDelete("/v1/videos/{id}", VideoEndpoint.DeleteVideoAsync);

// Doubao/Volcengine Seedance tasks (routes.go:253-258): creation reuses the
// shared chat handler with the Doubao video inbound transformer; GET/DELETE run
// the biz.VideoService flow through the same Seedance rendering.
app.MapPost("/doubao/v3/contents/generations/tasks", DoubaoVideoEndpoint.CreateTaskAsync);
app.MapGet("/doubao/v3/contents/generations/tasks/{id}", DoubaoVideoEndpoint.GetTaskAsync);
app.MapDelete("/doubao/v3/contents/generations/tasks/{id}", DoubaoVideoEndpoint.DeleteTaskAsync);

// Embeddings and moderations (routes.go:211-212): OpenAI.CreateEmbedding and
// OpenAI.CreateModeration delegate to the same chat handler with the dedicated
// inbound transformer, exactly like the reference's handler structs.
static Task HandleEmbeddingsAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator)
    => ChatCompletionEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(new EmbeddingInboundTransformer()));

static Task HandleModerationsAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator)
    => ChatCompletionEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(new ModerationInboundTransformer()));

app.MapPost("/v1/embeddings", HandleEmbeddingsAsync);
app.MapPost("/v1/moderations", HandleModerationsAsync);

// Alpha search and decisions (routes.go:213-214): each endpoint owns an inbound
// transformer over the shared chat handler, like EmbeddingHandlers and
// ModerationHandlers above.
static Task HandleAlphaSearchAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator)
    => ChatCompletionEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(new AlphaSearchInboundTransformer()));

static Task HandleDecisionsAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator)
    => ChatCompletionEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(new DecisionsInboundTransformer()));

app.MapPost("/v1/alpha/search", HandleAlphaSearchAsync);
app.MapPost("/v1/decisions", HandleDecisionsAsync);

// Audio API (routes.go:220-222): speech reads the request once to pick
// between SSE and binary streaming; transcription/translation delegate to
// the shared chat handler with the dedicated inbound transformers.
static Task HandleSpeechAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator)
    => SpeechEndpoint.HandleAsync(context, orchestrator.WithInbound(AudioInboundTransformer.Speech()));

static Task HandleTranscriptionAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator)
    => ChatCompletionEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(AudioInboundTransformer.Transcription()));

static Task HandleTranslationAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator)
    => ChatCompletionEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(AudioInboundTransformer.Translation()));

app.MapPost("/v1/audio/speech", HandleSpeechAsync);
app.MapPost("/v1/audio/transcriptions", HandleTranscriptionAsync);
app.MapPost("/v1/audio/translations", HandleTranslationAsync);

// Jina rerank (routes.go:230, 239): the OpenAI-compatible alias and the
// native /jina/v1 surface share one handler with the rerank inbound
// transformer, like RerankHandlers in jina.go.
static Task HandleRerankAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator)
    => ChatCompletionEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(new JinaRerankInboundTransformer()));

app.MapPost("/v1/rerank", HandleRerankAsync);
app.MapPost("/jina/v1/rerank", HandleRerankAsync);

// Jina embeddings (routes.go:238): the native /jina/v1 surface owns its
// inbound transformer; /v1/embeddings stays the OpenAI surface above.
static Task HandleJinaEmbeddingsAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator)
    => ChatCompletionEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(new JinaEmbeddingInboundTransformer()));

app.MapPost("/jina/v1/embeddings", HandleJinaEmbeddingsAsync);

// TypeSafe SystemOne (routes.go:233 and 244): the OpenAI-compatible
// /v1/systemone alias and the native /typesafe/v1 surface share one handler,
// like api.TypeSafeHandlers.SystemOne over the shared chat handler.
static Task HandleSystemOneAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator)
    => ChatCompletionEndpoint.HandleAsync(
        context,
        orchestrator.WithInbound(new SystemOneInboundTransformer()));

app.MapPost("/v1/systemone", HandleSystemOneAsync);
app.MapPost("/typesafe/v1/systemone", HandleSystemOneAsync);

// Native Gemini surface (routes.go:266-289): both groups share the handler
// pair. GenerateContent picks the writer from the alt query parameter;
// ListModels serves the /v1beta/models and /gemini/{gemini-api-version}/models
// literals. The parameter name keeps gin's spelling so the metrics path tag
// matches the reference (C223).
app.MapPost("/v1beta/models/{**action}", GeminiEndpoint.GenerateContentAsync);
app.MapGet("/v1beta/models", GeminiEndpoint.ListModelsAsync);
app.MapPost("/gemini/{gemini-api-version}/models/{**action}", GeminiEndpoint.GenerateContentAsync);
app.MapGet("/gemini/{gemini-api-version}/models", GeminiEndpoint.ListModelsAsync);

// The reference attaches its API middlewares to gin groups, so a request that
// misses every route reaches NoRoute without them (C116).
static bool IsStaticFallback(HttpContext context)
    => context.GetEndpoint()?.Metadata.GetMetadata<StaticFallbackMetadata>() is not null;

// Unmatched routes (routes.go:59 registers internal/server/static's handler as
// gin's NoRoute): /admin, /v1, ... answer with the JSON 404 envelope, static
// asset paths serve from disk or stay 404, everything else is the SPA. The
// reference embeds the built frontend with go:embed; the port serves
// Server:Static:Root (default frontend/dist) instead (C129).
var staticRoot = builder.Configuration["Server:Static:Root"];
staticRoot = string.IsNullOrWhiteSpace(staticRoot)
    ? System.IO.Path.Combine(app.Environment.ContentRootPath, "frontend", "dist")
    : System.IO.Path.GetFullPath(staticRoot, app.Environment.ContentRootPath);
app.MapFallback("{**path}", (RequestDelegate)new StaticHandler(staticRoot).HandleAsync)
    .WithMetadata(StaticFallbackMetadata.Instance);

app.Run();
