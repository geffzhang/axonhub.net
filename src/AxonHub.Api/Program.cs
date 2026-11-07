using AxonHub.Abstractions;
using AxonHub.Api.Auth;
using AxonHub.Api.Auth.Oidc;
using AxonHub.Api.Billing;
using AxonHub.Api.Endpoints;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Http;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Api.PromptProtection;
using AxonHub.Api.Prompts;
using AxonHub.Api.Security;
using AxonHub.Api.Settings;
using AxonHub.Api.Static;
using AxonHub.Api.Storage;
using AxonHub.Api.Traces;
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

// The reference's RequestService owns the previous-channel cache consumed by
// trace sticky routing (request.go:1733-1801); one singleton serves the
// orchestrator's selector reads and the persistence middleware's writes.
builder.Services.AddSingleton<IPreviousChannelProvider>(static services
    => new PreviousChannelCache(services.GetRequiredService<TimeProvider>()));

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
builder.Services.AddScoped<AxonHubDbContext>(static services =>
    services.GetRequiredService<IAxonHubDbContextFactory>().CreateDbContext());
builder.Services.AddScoped<IProjectStore, ProjectStore>();
builder.Services.AddScoped<IUserStore, UserStore>();
builder.Services.AddScoped<IApiKeyStore, ApiKeyStore>();
builder.Services.AddScoped<IApiKeyAuthenticator, ApiKeyAuthenticator>();
builder.Services.AddScoped<ISystemStore, SystemStore>();
builder.Services.AddScoped<ISystemSettingsAdminService, SystemSettingsAdminService>();
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

// The orchestrator's enforceQuota middleware (biz.QuotaService): API key
// profile quotas aggregated from usage_logs in the general-settings timezone.
builder.Services.AddScoped<IApiKeyQuotaChecker, ApiKeyQuotaService>();

// The orchestrator reads the retry policy through the scoped system store and
// resolves candidates through the model-association selector
// (orchestrator.DefaultSelector, candidates.go:110-398).
builder.Services.AddScoped<IModelCandidateSelector, ModelCandidateSelector>();
builder.Services.AddScoped<ChatCompletionOrchestrator>();

// Users and roles (biz.UserService / biz.RoleService). Passwords keep the
// reference's hex(bcrypt cost 10) format so rows are interchangeable with the
// Go implementation.
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddScoped<IUserAdminStore, UserAdminStore>();
builder.Services.AddScoped<IRoleStore, RoleStore>();
builder.Services.AddScoped<PermissionValidator>();
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
builder.Services.AddSingleton<IAntigravityOAuthStateCache, AntigravityOAuthStateCache>();
builder.Services.AddScoped<AntigravityOAuthService>();

// Projects (biz.ProjectService): CRUD over the projects table plus the
// project-level role presets and the creator's UserProject row. The project
// cache is not ported (C174).
builder.Services.AddScoped<ProjectManagementService>();

// Channel management reads (entgql channels / queryChannels / aggregates).
builder.Services.AddScoped<IChannelAdminStore, ChannelAdminStore>();
builder.Services.AddScoped<ChannelQueryService>();

// Channel lifecycle writes (status / delete / bulk status / bulk delete /
// bulk tag management). The entgql Transactioner wraps each mutation; the
// store reproduces that atomicity with a per-mutation transaction.
builder.Services.AddScoped<IChannelWriteStore, ChannelWriteStore>();
builder.Services.AddScoped<ChannelLifecycleService>();

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
// schedule, the synchronous routing-gate snapshot and the settings surface.
// The collection core is ported; the remaining seventeen provider checkers
// arrive in follow-up slices (C182), so those providers reach the reference's
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
// optional reset-credit capability) and provider_quota.go:422-424 the
// Antigravity checker (summary endpoint first, legacy per-model session quota
// as fallback). Checkers are stateless singletons sharing the process HTTP
// executor and the channel-proxy factory.
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

builder.Services.AddScoped<ProviderQuotaCollectionSettingsService>();
builder.Services.AddScoped<IProviderQuotaCollectionSettingsService>(
    static services => services.GetRequiredService<ProviderQuotaCollectionSettingsService>());
builder.Services.AddSingleton<ProviderQuotaService>();
builder.Services.AddSingleton<IProviderQuotaStatusProvider>(
    static services => services.GetRequiredService<ProviderQuotaService>());
builder.Services.AddSingleton<IProviderQuotaInvalidator>(
    static services => services.GetRequiredService<ProviderQuotaService>());
builder.Services.AddHostedService<ProviderQuotaInitializationHostedService>();
builder.Services.AddHostedService<ProviderQuotaScheduler>();

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

// Channel model sync (syncChannelModels): the per-channel fetch/merge/price
// transaction and the hourly schedule guard (channel_model_sync.go). The
// reference's channel-cache reload becomes a no-op until the cache is ported
// (C158).
builder.Services.AddScoped<IChannelModelSyncStore, ChannelModelSyncStore>();
builder.Services.AddSingleton<IChannelModelSyncReloadHook, NullChannelModelSyncReloadHook>();
builder.Services.AddScoped<ChannelModelSyncService>();

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
builder.Services.AddScoped<IDataStorageStore, DataStorageStore>();
builder.Services.AddScoped<DataStoragePrivacy>();
builder.Services.AddScoped<DataStorageManagementService>();

// Backup / restore (internal/server/backup): the streamed backup document, the
// transactional restore and the auto-backup operations. The storage backends
// live in Storage/ (fs / S3 / GCS / WebDAV, C199); AutoBackupScheduler carries
// the reference's daily 02:00 cron entry in the system time zone. The
// reference's system-value cache invalidation is a no-op here.
builder.Services.AddScoped<IBackupBatchSource, BackupStore>();
builder.Services.AddScoped<IBackupRestoreStore, BackupRestoreStore>();
builder.Services.AddSingleton<IDataStorageBackendFactory, DataStorageBackendFactory>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddHostedService<AutoBackupScheduler>();

// Garbage collection (internal/server/gc): the storage-policy-driven cleanup
// worker and its "gc" cron entry in UTC. The reference registers the task
// through fx.Invoke during app construction, so an unparsable gc.cron fails
// startup; parsing eagerly reproduces that. Channel probes have no table in
// the port schema (C195), and the reference's process-wide data-storage caches
// are not ported (C196).
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
builder.Services.AddHostedService<GcScheduler>();

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

// Global request tracing (routes.go:63): every request gets an ar-<uuid> id
// echoed in the configured response header and published for error bodies.
app.UseMiddleware<RequestTracingMiddleware>();

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

// The native Gemini surface (routes.go:266-289): the /gemini/:version group
// and the /v1beta alias run IP blocklist + Gemini key auth (query `key` or
// the regular header extractor) instead of WithAPIKeyConfig, then the same
// thread/trace pair. WithTimeout/WithSource/WithResponseHeaders stay
// unregistered like the rest of the port (C115).
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
static Task HandleAnthropicMessagesAsync(
    HttpContext context,
    ChatCompletionOrchestrator orchestrator,
    AnthropicInboundTransformer anthropic)
    => ChatCompletionEndpoint.HandleAsync(context, orchestrator.WithInbound(anthropic));

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

app.MapPost("/v1/responses", HandleResponsesAsync);
app.MapPost("/v1/responses/compact", HandleResponsesCompactAsync);

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
// ListModels serves the /v1beta/models and /gemini/{apiVersion}/models
// literals.
app.MapPost("/v1beta/models/{**action}", GeminiEndpoint.GenerateContentAsync);
app.MapGet("/v1beta/models", GeminiEndpoint.ListModelsAsync);
app.MapPost("/gemini/{apiVersion}/models/{**action}", GeminiEndpoint.GenerateContentAsync);
app.MapGet("/gemini/{apiVersion}/models", GeminiEndpoint.ListModelsAsync);

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
