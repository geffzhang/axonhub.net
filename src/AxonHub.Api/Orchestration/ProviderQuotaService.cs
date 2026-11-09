using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of biz.ProviderQuotaService (internal/server/biz/provider_quota.go):
/// collects the provider-side quota a channel's upstream account reports,
/// persists it per channel, and exposes the cached result to the routing gate.
/// </summary>
/// <remarks>
/// <para>
/// The reference constructs its nineteen provider checkers in
/// <c>registerProviderQuotaSupport</c> (provider_quota.go:374-480). The port
/// registers all of them through <see cref="IProviderQuotaCheckerRegistry"/>;
/// the registry's key set matches the reference exactly (C182).
/// </para>
/// <para>
/// The reference's <c>ProviderQuotaServiceParams.CheckInterval</c> and
/// <c>WarningCheckIntervalRatio</c> are named fx values that no module
/// provides, so the service always runs on its 5-minute default and the
/// ratio-4 fallback even though <c>conf</c> declares different defaults. The
/// port hardcodes those two values (C182).
/// </para>
/// <para>
/// The service is a singleton because the channel routing gate reads
/// <see cref="GetQuotaStatus"/> synchronously. Every database operation runs in
/// its own DI scope: groups are checked up to eight at a time, and one
/// <see cref="AxonHub.Persistence.AxonHubDbContext"/> is not safe for concurrent use.
/// </para>
/// </remarks>
public sealed class ProviderQuotaService :
    IProviderQuotaStatusProvider,
    IProviderQuotaInvalidator,
    IProviderQuotaGroupRunner
{
    /// <summary>Port of maxConcurrentQuotaChecks (provider_quota.go:26).</summary>
    public const int MaxConcurrentQuotaChecks = 8;

    /// <summary>Port of quotaErrorCodeCheckFailed.</summary>
    public const string ErrorCodeCheckFailed = "check_failed";

    /// <summary>Port of quotaErrorCodeMissingCredentials.</summary>
    public const string ErrorCodeMissingCredentials = "missing_credentials";

    /// <summary>Port of maxQuotaErrorBackoffMultiplier.</summary>
    public const int MaxQuotaErrorBackoffMultiplier = 8;

    /// <summary>Port of maxQuotaErrorBackoffSteps.</summary>
    public const int MaxQuotaErrorBackoffSteps = 4;

    /// <summary>getCheckInterval's fallback (provider_quota.go:534-540).</summary>
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>getWarningCheckInterval's fallback ratio (provider_quota.go:518-525).</summary>
    public const int DefaultWarningCheckIntervalRatio = 4;

    /// <summary>
    /// Port of providerQuotaChannelTypes (provider_quota.go:54-83): the channel
    /// types that can carry a provider quota status, in the reference's order.
    /// </summary>
    public static IReadOnlyList<string> ProviderQuotaChannelTypes { get; } =
    [
        "claudecode",
        "codex",
        "antigravity",
        "xai_subscription",
        "github_copilot",
        "nanogpt",
        "nanogpt_responses",
        "zenmux",
        "zenmux_responses",
        "zenmux_anthropic",
        "zenmux_gemini",
        "zenmux_video",
        "cline",
        "openai",
        "openai_responses",
        "opencode_go",
        "opencode_go_anthropic",
        "moonshot_coding",
        "minimax",
        "minimax_anthropic",
        "zhipu",
        "zhipu_anthropic",
        "zai",
        "zai_anthropic",
        "commandcode",
        "commandcode_anthropic",
        "ollama",
        "ollama_anthropic",
    ];

    private static readonly Action<ILogger, Exception?> LogCacheLoadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaCacheLoadFailed"),
            "Failed to load quota cache from DB");

    private static readonly Action<ILogger, Exception?> LogChannelQueryFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaChannelQueryFailed"),
            "Failed to query channels for quota check");

    private static readonly Action<ILogger, string, Exception?> LogSettingsReadFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(0, "ProviderQuotaSettingsReadFailed"),
            "failed to read provider quota collection settings: provider={Provider}");

    private static readonly Action<ILogger, string, long, Exception?> LogNoChecker =
        LoggerMessage.Define<string, long>(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaNoChecker"),
            "No checker for provider: provider={Provider} channel_id={ChannelId}");

    private static readonly Action<ILogger, long, string, string, Exception?> LogCheckFailed =
        LoggerMessage.Define<long, string, string>(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaCheckFailed"),
            "Quota check failed: channel_id={ChannelId} channel_name={ChannelName} provider={Provider}");

    private static readonly Action<ILogger, long, string, Exception?> LogSaveStatusFailed =
        LoggerMessage.Define<long, string>(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaSaveStatusFailed"),
            "Failed to save quota status: channel_id={ChannelId} provider={Provider}");

    private static readonly Action<ILogger, long, string, Exception?> LogSaveErrorFailed =
        LoggerMessage.Define<long, string>(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaSaveErrorFailed"),
            "Failed to save quota error: channel_id={ChannelId} provider={Provider}");

    private static readonly Action<ILogger, long, string, Exception?> LogResetForErrorFailed =
        LoggerMessage.Define<long, string>(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaResetForErrorFailed"),
            "Failed to reset quota status after quota check error: channel_id={ChannelId} provider={Provider}");

    private static readonly Action<ILogger, long, string, Exception?> LogInvalidateFailed =
        LoggerMessage.Define<long, string>(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaInvalidateFailed"),
            "Failed to invalidate provider quota after credentials disappeared: channel_id={ChannelId} provider={Provider}");

    private static readonly Action<ILogger, long, string, Exception?> LogListResetsFailed =
        LoggerMessage.Define<long, string>(
            LogLevel.Warning,
            new EventId(0, "ProviderQuotaListResetsFailed"),
            "Failed to list provider quota resets: channel_id={ChannelId} provider={Provider}");

    private static readonly Action<ILogger, long, Exception?> LogCostAggregationFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "ProviderQuotaCostAggregationFailed"),
            "Failed to aggregate channel cost for quota period: channel_id={ChannelId}");

    private static readonly Action<ILogger, int, int, Exception?> LogIntervalRounded =
        LoggerMessage.Define<int, int>(
            LogLevel.Warning,
            new EventId(0, "ProviderQuotaIntervalRounded"),
            "Quota check interval does not divide evenly into 60 minutes, rounding to nearest supported interval: requested_minutes={RequestedMinutes} rounded_minutes={RoundedMinutes}");

    private static readonly Action<ILogger, Exception?> LogGroupFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaGroupFailed"),
            "quota check group failed");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProviderQuotaSettingsCache _settingsCache;
    private readonly IProviderQuotaCheckerRegistry _checkers;
    private readonly TimeProvider _time;
    private readonly ILogger<ProviderQuotaService> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, QuotaChannelStatus> _quotaCache = new();
    private readonly IQuotaGroupExecutor? _groupExecutor;
    private readonly IProviderQuotaCoordinatorClient? _coordinator;

    /// <summary>
    /// Port of <c>svc.mu</c>: serializes the whole collection run with cache
    /// invalidation and post-reset refreshes. In production the coordinator
    /// grain provides the cluster-wide exclusion and the process-local mutex
    /// only guards the direct entries (a single silo, tests); a service
    /// constructed without a coordinator keeps the reference's process-local
    /// semantics (§150; C230).
    /// </summary>
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public ProviderQuotaService(
        IServiceScopeFactory scopeFactory,
        ProviderQuotaSettingsCache settingsCache,
        IProviderQuotaCheckerRegistry checkers,
        TimeProvider time,
        ILogger<ProviderQuotaService> logger,
        IQuotaGroupExecutor? groupExecutor = null,
        IProviderQuotaCoordinatorClient? coordinator = null)
    {
        _scopeFactory = scopeFactory;
        _settingsCache = settingsCache;
        _checkers = checkers;
        _time = time;
        _logger = logger;
        _groupExecutor = groupExecutor;
        _coordinator = coordinator;
    }

    /// <summary>
    /// Port of the constructor's <c>loadQuotaCache</c> call (provider_quota.go:369,
    /// 542-559): every live row becomes a cache entry. The reference logs and
    /// continues when the query fails, leaving the cache empty.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProviderQuotaStatusRecord> records;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IProviderQuotaStatusStore>();
            records = await store.ListAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCacheLoadFailed(_logger, ex);
            return;
        }

        foreach (var record in records)
        {
            _quotaCache[record.ChannelId] = new QuotaChannelStatus
            {
                ProviderType = record.ProviderType,
                Status = record.Status,
                Ready = record.Ready,
                Limits = ProviderQuotaData.CloneLimits(
                    ExtractLimitsFromQuotaData(record.QuotaData)),
            };
        }
    }

    /// <summary>
    /// Port of <c>GetQuotaStatus</c> (provider_quota.go:561-584). The reference
    /// consults <c>ProviderQuotaCollectionSettingsOrDefault</c> for every
    /// cached status with a provider type; the port reads the
    /// <see cref="ProviderQuotaSettingsCache"/> snapshot because this member is
    /// synchronous.
    /// </summary>
    public QuotaChannelStatus? GetQuotaStatus(long channelId)
    {
        if (!_quotaCache.TryGetValue(channelId, out var status))
        {
            return null;
        }

        if (status.ProviderType.Length > 0)
        {
            var settings = _settingsCache.Current;
            if (!settings.Enabled
                || !settings.Providers.TryGetValue(status.ProviderType, out var enabled)
                || !enabled)
            {
                return null;
            }
        }

        return new QuotaChannelStatus
        {
            ProviderType = status.ProviderType,
            Status = status.Status,
            Ready = status.Ready,
            Limits = ProviderQuotaData.CloneLimits(status.Limits),
        };
    }

    /// <summary>
    /// Port of <c>InvalidateChannelQuota</c> (provider_quota.go:595-603). The
    /// channel service calls it when a channel's provider identity changes or
    /// its quota cookie is cleared (channel_provider_quota_hook.go). In
    /// production the coordinator grain serializes it with the collection run
    /// (§150); a directly constructed service keeps the process-local mutex.
    /// </summary>
    public Task InvalidateChannelQuotaAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        if (_coordinator is not null)
        {
            return _coordinator.InvalidateAsync(channelId, cancellationToken);
        }

        return InvalidateChannelQuotaLockedEntryAsync(channelId, cancellationToken);
    }

    /// <summary>
    /// The invalidation without the process-local mutex, for the coordinator's
    /// operations (the coordinator grain's turn is the exclusion).
    /// </summary>
    public async Task InvalidateChannelQuotaCoreAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        await InvalidateChannelQuotaLockedAsync(scope.ServiceProvider, channelId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task InvalidateChannelQuotaLockedEntryAsync(
        long channelId,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InvalidateChannelQuotaCoreAsync(channelId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Port of <c>ManualCheck</c> (provider_quota.go:620-623). The reference
    /// runs it under <c>svc.mu</c>, so it waits for a running sweep and blocks
    /// the next one; the coordinator grain provides that exclusion (§150).
    /// </summary>
    public Task ManualCheckAsync(CancellationToken cancellationToken = default)
    {
        if (_coordinator is not null)
        {
            return _coordinator.RunManualAsync(cancellationToken);
        }

        return RunLockedAsync(force: true, cancellationToken);
    }

    /// <summary>
    /// Port of <c>runQuotaCheckScheduled</c> (provider_quota_internal.go). The
    /// production schedule is the coordinator grain's reminder; this direct
    /// entry keeps the process-local mutex for callers outside the grain
    /// (tests and tools).
    /// </summary>
    public Task RunScheduledCheckAsync(CancellationToken cancellationToken = default)
        => RunLockedAsync(force: false, cancellationToken);

    private async Task RunLockedAsync(bool force, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunQuotaCheckAsync(force, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Port of <c>runQuotaCheck</c> (provider_quota.go:717-781).</summary>
    public async Task RunQuotaCheckAsync(bool force, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settingsService = scope.ServiceProvider
            .GetRequiredService<IProviderQuotaCollectionSettingsService>();
        var settings = await settingsService.GetOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        _settingsCache.Update(settings);
        if (!settings.Enabled)
        {
            return;
        }

        var now = _time.GetUtcNow();

        IReadOnlyList<ChannelRecord> channels;
        IReadOnlyList<ProviderQuotaStatusRecord> statuses;
        try
        {
            var channelStore = scope.ServiceProvider.GetRequiredService<IChannelStore>();
            var statusStore = scope.ServiceProvider.GetRequiredService<IProviderQuotaStatusStore>();
            channels = await channelStore
                .ListEnabledByTypesAsync(ProviderQuotaChannelTypes, cancellationToken)
                .ConfigureAwait(false);
            statuses = await statusStore.ListAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogChannelQueryFailed(_logger, ex);
            return;
        }

        var statusByChannel = new Dictionary<long, ProviderQuotaStatusRecord>();
        foreach (var status in statuses)
        {
            statusByChannel[status.ChannelId] = status;
        }

        var candidates = new List<QuotaCheckChannel>();
        foreach (var channel in channels)
        {
            var providerType = GetProviderType(channel);
            if (providerType.Length == 0
                || !settings.Providers.TryGetValue(providerType, out var providerEnabled)
                || !providerEnabled)
            {
                continue;
            }

            statusByChannel.TryGetValue(channel.Id, out var status);
            candidates.Add(new QuotaCheckChannel(channel, status));
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var groups = GroupChannelsByQuotaAccount(candidates);
        if (!force)
        {
            groups = [.. groups.Where(group => IsDue(group, now))];
        }

        if (groups.Count == 0)
        {
            return;
        }

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(MaxConcurrentQuotaChecks, groups.Count),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(groups, options, async (group, token) =>
        {
            try
            {
                await ExecuteGroupAsync(group, now, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The reference's group goroutine only returns nil; checker and
                // persistence failures are already handled inside. This catch
                // guards an unexpected bug from taking down the whole run.
                LogGroupFailed(_logger, ex);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one due group: through the leader channel's grain when production
    /// wired the executor (§150), otherwise in-process (the reference's
    /// goroutine body, provider_quota.go:779-786).
    /// </summary>
    private async Task ExecuteGroupAsync(
        QuotaCheckGroup group,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_groupExecutor is not null)
        {
            await _groupExecutor
                .ExecuteAsync(ToCheckGroup(group), now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await using var groupScope = _scopeFactory.CreateAsyncScope();
        await CheckChannelQuotaAsync(groupScope.ServiceProvider, group, now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The API half of <see cref="IProviderQuotaGrain.CheckGroupAsync"/>
    /// (§150): rebuilds the group from the grain's snapshot and runs the
    /// reference's <c>checkChannelQuota</c> (provider_quota.go:792-864), then
    /// reports every member's resulting cache entry so member grains can mirror
    /// it.
    /// </summary>
    public async Task<ProviderQuotaGroupCheckResult> RunGroupAsync(
        ProviderQuotaCheckGroup group,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        var checkGroup = new QuotaCheckGroup(group.AccountKey);
        foreach (var member in group.Members)
        {
            checkGroup.Members.Add(new QuotaCheckChannel(
                ToChannelRecord(member),
                ToStatusRecord(member.Status)));
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        await CheckChannelQuotaAsync(scope.ServiceProvider, checkGroup, now, cancellationToken)
            .ConfigureAwait(false);

        var result = new ProviderQuotaGroupCheckResult();
        foreach (var member in group.Members)
        {
            result.Members.Add(new ProviderQuotaMemberStatus
            {
                ChannelId = member.Id,
                Status = TryGetCachedStatus(member.Id),
            });
        }

        return result;
    }

    /// <summary>
    /// Port of <c>checkChannelQuota</c> (provider_quota.go:786-864). The caller
    /// holds <c>svc.mu</c>; that is why invalidation runs through the locked
    /// helper here.
    /// </summary>
    private async Task CheckChannelQuotaAsync(
        IServiceProvider services,
        QuotaCheckGroup group,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var channel = group.Members[0].Channel;
        var providerType = GetProviderType(channel);
        if (providerType.Length == 0)
        {
            return;
        }

        var settingsService = services
            .GetRequiredService<IProviderQuotaCollectionSettingsService>();
        try
        {
            if (!await settingsService.IsEnabledAsync(providerType, cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The reference logs and keeps going: a settings read failure must
            // not stop the channel check.
            LogSettingsReadFailed(_logger, providerType, ex);
        }

        if (!HasCredentialsForProvider(channel))
        {
            try
            {
                await InvalidateChannelQuotaLockedAsync(services, channel.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogInvalidateFailed(_logger, channel.Id, providerType, ex);
            }

            return;
        }

        if (!_checkers.TryGet(providerType, out var checker))
        {
            LogNoChecker(_logger, providerType, channel.Id, null);
            return;
        }

        QuotaData quotaData;
        try
        {
            quotaData = await checker.CheckQuotaAsync(channel, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception quotaError)
        {
            LogCheckFailed(_logger, channel.Id, channel.Name, providerType, quotaError);

            var failures = NextQuotaGroupErrorCount(group.Members, providerType);
            foreach (var member in group.Members)
            {
                await SaveQuotaErrorAsync(
                        services,
                        member.Channel,
                        member.Status,
                        providerType,
                        group.AccountKey,
                        quotaError,
                        failures,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        quotaData = ProviderQuotaData.Normalize(quotaData, now);

        var resetList = new ResetList { Supported = false };
        if (checker is IQuotaResetter resetter)
        {
            resetList.Supported = true;
            try
            {
                resetList = await resetter.ListResetsAsync(channel, cancellationToken)
                    .ConfigureAwait(false);
                resetList.Supported = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                resetList.Error = ex.Message;
                LogListResetsFailed(_logger, channel.Id, providerType, ex);
            }
        }

        quotaData.Resets = resetList;

        foreach (var member in group.Members)
        {
            var memberQuotaData = new QuotaData
            {
                Status = quotaData.Status,
                ProviderType = quotaData.ProviderType,
                RawData = quotaData.RawData,
                NextResetAt = quotaData.NextResetAt,
                Ready = quotaData.Ready,
                Limits = ProviderQuotaData.CloneLimits(quotaData.Limits),
                Resets = quotaData.Resets,
            };

            await FillPeriodQuotasAsync(
                    services, member.Channel.Id, memberQuotaData, now, cancellationToken)
                .ConfigureAwait(false);
            await SaveQuotaStatusAsync(
                    services,
                    member.Channel.Id,
                    providerType,
                    group.AccountKey,
                    memberQuotaData,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Port of <c>saveQuotaStatus</c> (provider_quota.go:866-911).</summary>
    private async Task SaveQuotaStatusAsync(
        IServiceProvider services,
        long channelId,
        string providerType,
        string accountKey,
        QuotaData quotaData,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nextCheck = now + NextCheckIntervalForStatus(quotaData.Status);
        var record = new ProviderQuotaStatusRecord
        {
            ChannelId = channelId,
            ProviderType = providerType,
            AccountKey = accountKey,
            Status = quotaData.Status,
            QuotaData = MergeLimitsIntoQuotaData(quotaData),
            NextResetAt = quotaData.NextResetAt?.UtcDateTime,
            Ready = quotaData.Ready,
            NextCheckAt = nextCheck.UtcDateTime,
        };

        try
        {
            var store = services.GetRequiredService<IProviderQuotaStatusStore>();
            await store.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSaveStatusFailed(_logger, channelId, providerType, ex);
            return;
        }

        UpdateQuotaCache(channelId, providerType, quotaData.Status, quotaData.Ready, quotaData.Limits);
    }

    /// <summary>Port of <c>saveQuotaError</c> (provider_quota.go:913-1010).</summary>
    private async Task SaveQuotaErrorAsync(
        IServiceProvider services,
        ChannelRecord channel,
        ProviderQuotaStatusRecord? existing,
        string providerType,
        string accountKey,
        Exception quotaError,
        int failures,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nextCheck = now + QuotaErrorBackoff(GetCheckInterval(), failures);
        var errorCode = QuotaErrorCode(quotaError);
        var store = services.GetRequiredService<IProviderQuotaStatusStore>();

        if (existing is not null)
        {
            var providerChanged = existing.ProviderType != providerType;
            var invalidCredentials =
                GoErrors.As<InvalidProviderQuotaCredentialsException>(quotaError) is not null;
            if (providerChanged || invalidCredentials)
            {
                var resetNextCheck = now + QuotaErrorBackoff(GetCheckInterval(), 1);
                var resetData = AxonHub.Llm.Json.GoJson.MarshalToString(
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["error_code"] = errorCode,
                        ["error_count"] = 1,
                    });

                try
                {
                    await store.ResetForProviderErrorAsync(
                            existing.Id,
                            providerType,
                            accountKey,
                            resetData,
                            resetNextCheck.UtcDateTime,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogResetForErrorFailed(_logger, channel.Id, providerType, ex);
                    return;
                }

                UpdateQuotaCache(
                    channel.Id,
                    providerType,
                    ProviderQuotaStatuses.Unknown,
                    ready: false,
                    limits: null);
                return;
            }

            var merged = MergeQuotaErrorData(existing.QuotaData, errorCode, failures);
            try
            {
                await store.UpdateErrorAsync(
                        existing.Id,
                        accountKey,
                        merged,
                        nextCheck.UtcDateTime,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSaveErrorFailed(_logger, channel.Id, providerType, ex);
                return;
            }

            var existingLimits = ExtractLimitsFromQuotaData(existing.QuotaData);
            UpdateQuotaCache(
                channel.Id,
                providerType,
                existing.Status,
                existing.Ready,
                existingLimits);
            return;
        }

        var data = AxonHub.Llm.Json.GoJson.MarshalToString(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["error_code"] = errorCode,
                ["error_count"] = failures,
            });

        try
        {
            await store.CreateAsync(
                    new ProviderQuotaStatusRecord
                    {
                        ChannelId = channel.Id,
                        ProviderType = providerType,
                        AccountKey = accountKey,
                        Status = ProviderQuotaStatuses.Unknown,
                        Ready = false,
                        QuotaData = data,
                        NextResetAt = null,
                        NextCheckAt = nextCheck.UtcDateTime,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSaveErrorFailed(_logger, channel.Id, providerType, ex);
            return;
        }

        UpdateQuotaCache(channel.Id, providerType, ProviderQuotaStatuses.Unknown, ready: false, limits: null);
    }

    /// <summary>Port of <c>fillPeriodQuotas</c> (provider_quota_cost.go:24-83).</summary>
    private async Task FillPeriodQuotasAsync(
        IServiceProvider services,
        long channelId,
        QuotaData quotaData,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (quotaData.ProviderType == "zenmux")
        {
            foreach (var limit in quotaData.Limits)
            {
                limit.PeriodCost = null;
                limit.PeriodQuota = null;
            }

            return;
        }

        if (quotaData.ProviderType == "synthetic")
        {
            // Synthetic reports the weekly pool's money in its own credits and
            // regenerates it in ticks, so the checker's figures stay authoritative.
            return;
        }

        var costs = new Dictionary<DateTimeOffset, double>();
        var usageLogs = services.GetRequiredService<IUsageLogStore>();

        foreach (var limit in quotaData.Limits)
        {
            limit.PeriodCost = null;

            var start = limit.PeriodStart;
            if (start is not { } periodStart
                || periodStart >= now
                || limit.UsageRatio <= 0
                || !double.IsFinite(limit.UsageRatio))
            {
                continue;
            }

            if (!costs.TryGetValue(periodStart, out var cost))
            {
                try
                {
                    cost = await usageLogs.SumCostByChannelAsync(
                            channelId,
                            periodStart.UtcDateTime,
                            now.UtcDateTime,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogCostAggregationFailed(_logger, channelId, ex);
                    continue;
                }

                costs[periodStart] = cost;
            }

            if (cost <= 0)
            {
                continue;
            }

            limit.PeriodCost = cost;
        }

        ProviderQuotaData.FillPeriodQuotas(quotaData);
    }

    /// <summary>
    /// Port of <c>invalidateChannelQuotaLocked</c> (provider_quota.go:605-618):
    /// the cache entry is removed even when the hard delete fails.
    /// </summary>
    private async Task InvalidateChannelQuotaLockedAsync(
        IServiceProvider services,
        long channelId,
        CancellationToken cancellationToken)
    {
        try
        {
            var store = services.GetRequiredService<IProviderQuotaStatusStore>();
            await store.HardDeleteByChannelIdAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to invalidate provider quota status: " + ex.Message,
                ex);
        }
        finally
        {
            _quotaCache.TryRemove(channelId, out _);
        }
    }

    /// <summary>Port of <c>updateQuotaCache</c> (provider_quota.go:586-593).</summary>
    private void UpdateQuotaCache(
        long channelId,
        string providerType,
        string status,
        bool ready,
        IReadOnlyList<QuotaLimitStatus>? limits)
    {
        _quotaCache[channelId] = new QuotaChannelStatus
        {
            ProviderType = providerType,
            Status = status,
            Ready = ready,
            Limits = ProviderQuotaData.CloneLimits(limits),
        };
    }

    /// <summary>Port of <c>ListResets</c> (provider_quota.go:628-662).</summary>
    public async Task<ResetList> ListResetsAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var channelStore = scope.ServiceProvider.GetRequiredService<IChannelStore>();
        var channel = await channelStore.GetByIdAsync(channelId, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new InvalidOperationException("failed to load channel: ent: channel not found");

        var providerType = GetProviderType(channel);
        if (!_checkers.TryGet(providerType, out var checker) || checker is not IQuotaResetter resetter)
        {
            return new ResetList { Supported = false };
        }

        var settings = scope.ServiceProvider
            .GetRequiredService<IProviderQuotaCollectionSettingsService>();
        bool enabled;
        try
        {
            enabled = await settings.IsEnabledAsync(providerType, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to read provider quota collection settings: " + ex.Message,
                ex);
        }

        if (!enabled)
        {
            throw new InvalidOperationException(
                "provider quota collection is disabled for " + providerType);
        }

        if (!HasCredentialsForProvider(channel))
        {
            throw new InvalidOperationException("channel has no credentials");
        }

        ResetList resets;
        try
        {
            resets = await resetter.ListResetsAsync(channel, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to list " + providerType + " quota resets: " + ex.Message,
                ex);
        }

        resets.Supported = true;
        return resets;
    }

    /// <summary>Port of <c>ResetChannelQuotaNow</c> (provider_quota.go:665-708).</summary>
    public async Task ResetChannelQuotaNowAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var channelStore = scope.ServiceProvider.GetRequiredService<IChannelStore>();
        var channel = await channelStore.GetByIdAsync(channelId, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new InvalidOperationException("failed to load channel: ent: channel not found");

        var providerType = GetProviderType(channel);
        if (!_checkers.TryGet(providerType, out var checker) || checker is not IQuotaResetter resetter)
        {
            throw new InvalidOperationException(
                new ProviderQuotaResetUnsupportedException().Message
                + " for provider " + GoCast.GoQuote(providerType));
        }

        var settings = scope.ServiceProvider
            .GetRequiredService<IProviderQuotaCollectionSettingsService>();
        bool enabled;
        try
        {
            enabled = await settings.IsEnabledAsync(providerType, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to read provider quota collection settings: " + ex.Message,
                ex);
        }

        if (!enabled)
        {
            throw new InvalidOperationException(
                "provider quota collection is disabled for " + providerType);
        }

        if (!HasCredentialsForProvider(channel))
        {
            throw new InvalidOperationException("channel has no credentials");
        }

        try
        {
            await resetter.ResetAsync(channel, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to reset " + providerType + " quota: " + ex.Message,
                ex);
        }

        // Refresh the quota status immediately so the UI reflects the reset.
        // The reference's refresh group has no loaded status edge, so a failed
        // refresh's create-or-update decision starts from "no row" here as
        // well. In production the coordinator grain serializes the refresh
        // with the collection run (§150); a directly constructed service keeps
        // the process-local mutex.
        if (_coordinator is not null)
        {
            await _coordinator.RefreshChannelAsync(
                    new ProviderQuotaRefreshRequest
                    {
                        AccountKey = QuotaAccountKey(providerType, channel),
                        Channel = ToChannelSnapshot(channel, status: null),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var group = new QuotaCheckGroup(QuotaAccountKey(providerType, channel));
            group.Members.Add(new QuotaCheckChannel(channel, status: null));
            await CheckChannelQuotaAsync(scope.ServiceProvider, group, now, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// The coordinator's post-reset refresh (provider_quota.go:701-713): one
    /// channel, no loaded status edge, run through the leader grain when the
    /// executor is wired.
    /// </summary>
    public async Task RefreshChannelQuotaAsync(
        ProviderQuotaRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var group = new QuotaCheckGroup(request.AccountKey);
        group.Members.Add(new QuotaCheckChannel(
            ToChannelRecord(request.Channel),
            status: null));

        await ExecuteGroupAsync(group, _time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of <c>getProviderType</c> (provider_quota.go:1012-1049).
    /// </summary>
    public static string GetProviderType(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type switch
        {
            "claudecode" => "claudecode",
            "codex" => "codex",
            "antigravity" => "antigravity",
            "xai_subscription" => "xai_subscription",
            "github_copilot" => "github_copilot",
            "nanogpt" or "nanogpt_responses" => "nanogpt",
            "zenmux" or "zenmux_responses" or "zenmux_anthropic" or "zenmux_gemini"
                or "zenmux_video" => "zenmux",
            "cline" => "cline",
            "openai" or "openai_responses" =>
                ProviderQuotaUrlDetection.DetectProviderFromUrl(channel.BaseUrl),
            "opencode_go" or "opencode_go_anthropic" => "opencode_go",
            "moonshot_coding" => "kimi_code",
            "minimax" or "minimax_anthropic" => "minimax",
            "zhipu" or "zhipu_anthropic" => "zhipu",
            "zai" or "zai_anthropic" => "zai",
            "commandcode" or "commandcode_anthropic" => "commandcode",
            "ollama" or "ollama_anthropic" => "ollama",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Port of <c>hasCredentialsForProvider</c> (provider_quota.go:1051-1095).
    /// Stored settings/credentials JSON is decoded leniently here; the
    /// reference's ent decoder would fail the whole channel query on malformed
    /// JSON (C013/C182).
    /// </summary>
    private bool HasCredentialsForProvider(ChannelRecord channel)
    {
        var credentials = ParseCredentials(channel.Credentials);
        var type = channel.Type;

        if (type is "zenmux" or "zenmux_responses" or "zenmux_anthropic"
            or "zenmux_gemini" or "zenmux_video")
        {
            return !string.IsNullOrWhiteSpace(credentials.ManagementApiKey);
        }

        if (type is "openai" or "openai_responses")
        {
            var detected = ProviderQuotaUrlDetection.DetectProviderFromUrl(channel.BaseUrl);
            if (ProviderQuotaUrlDetection.DetectedProviders.Contains(detected))
            {
                return !string.IsNullOrWhiteSpace(credentials.ApiKey)
                    || credentials.ApiKeys is { Count: > 0 };
            }
        }

        if (type is "codex" or "claudecode" or "xai_subscription")
        {
            return credentials.OAuth is not null || IsOAuthJsonForQuota(credentials.ApiKey);
        }

        if (type == "cline")
        {
            if (!string.IsNullOrWhiteSpace(credentials.ApiKey))
            {
                return true;
            }

            if (credentials.ApiKeys is not null)
            {
                foreach (var apiKey in credentials.ApiKeys)
                {
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        if (ChannelValidation.IsCommandCodeChannelType(type))
        {
            return CommandCodeQuotaChecker.HasQuotaCredentials(channel);
        }

        if (type is "ollama" or "ollama_anthropic")
        {
            return OllamaQuotaChecker.HasQuotaCredentials(channel, _time.GetUtcNow());
        }

        return credentials.OAuth is not null
            || IsOAuthJsonForQuota(credentials.ApiKey)
            || !string.IsNullOrWhiteSpace(credentials.ApiKey)
            || credentials.ApiKeys is { Count: > 0 };
    }

    /// <summary>Port of provider_quota.isOAuthJSON (channel_llm.go:1625-1628).</summary>
    private static bool IsOAuthJsonForQuota(string? apiKey)
    {
        var value = apiKey ?? string.Empty;
        var trimmed = value.Trim();
        return trimmed.StartsWith('{') && value.Contains("access_token", StringComparison.Ordinal);
    }

    private static ChannelSettingsDraft? ParseSettingsLenient(string? raw)
    {
        try
        {
            return ChannelWriteSerialization.ParseSettings(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChannelCredentialsDraft ParseCredentials(string? raw)
    {
        try
        {
            return ChannelWriteSerialization.ParseCredentials(raw);
        }
        catch (JsonException)
        {
            return new ChannelCredentialsDraft();
        }
    }

    /// <summary>
    /// Converts a planned group into the grain boundary shape (§150). The
    /// snapshot carries exactly the channel and status columns the check reads,
    /// so the grain path sees the same values the in-process path holds.
    /// </summary>
    private static ProviderQuotaCheckGroup ToCheckGroup(QuotaCheckGroup group)
    {
        var descriptor = new ProviderQuotaCheckGroup { AccountKey = group.AccountKey };
        foreach (var member in group.Members)
        {
            descriptor.Members.Add(ToChannelSnapshot(member.Channel, member.Status));
        }

        return descriptor;
    }

    private static ProviderQuotaChannelSnapshot ToChannelSnapshot(
        ChannelRecord channel,
        ProviderQuotaStatusRecord? status) => new()
    {
        Id = channel.Id,
        Name = channel.Name,
        Type = channel.Type,
        BaseUrl = channel.BaseUrl,
        Credentials = channel.Credentials,
        Settings = channel.Settings,
        SupportedModels = channel.SupportedModels,
        ManualModels = channel.ManualModels,
        DisabledApiKeys = channel.DisabledApiKeys,
        Status = status is null ? null : ToStoredStatus(status),
    };

    private static ProviderQuotaStoredStatus ToStoredStatus(ProviderQuotaStatusRecord status) => new()
    {
        Id = status.Id,
        ChannelId = status.ChannelId,
        ProviderType = status.ProviderType,
        Status = status.Status,
        QuotaData = status.QuotaData,
        NextResetAt = status.NextResetAt,
        Ready = status.Ready,
        NextCheckAt = status.NextCheckAt,
        AccountKey = status.AccountKey,
    };

    private static ChannelRecord ToChannelRecord(ProviderQuotaChannelSnapshot snapshot) => new()
    {
        Id = snapshot.Id,
        Name = snapshot.Name,
        Type = snapshot.Type,
        BaseUrl = snapshot.BaseUrl,
        Credentials = snapshot.Credentials,
        Settings = snapshot.Settings,
        SupportedModels = snapshot.SupportedModels,
        ManualModels = snapshot.ManualModels,
        DisabledApiKeys = snapshot.DisabledApiKeys,
    };

    private static ProviderQuotaStatusRecord? ToStatusRecord(ProviderQuotaStoredStatus? status)
        => status is null
            ? null
            : new ProviderQuotaStatusRecord
            {
                Id = status.Id,
                ChannelId = status.ChannelId,
                ProviderType = status.ProviderType,
                Status = status.Status,
                QuotaData = status.QuotaData,
                NextResetAt = status.NextResetAt,
                Ready = status.Ready,
                NextCheckAt = status.NextCheckAt,
                AccountKey = status.AccountKey,
            };

    /// <summary>
    /// The raw cache entry for a channel (provider_quota.go:586-593). Unlike
    /// <see cref="GetQuotaStatus"/> this does not apply the collection settings
    /// gate: the grain mirrors the cache, not the routing view.
    /// </summary>
    private ProviderQuotaStatusSnapshot? TryGetCachedStatus(long channelId)
    {
        if (!_quotaCache.TryGetValue(channelId, out var status))
        {
            return null;
        }

        var snapshot = new ProviderQuotaStatusSnapshot
        {
            ChannelId = channelId,
            ProviderType = status.ProviderType,
            Status = status.Status,
            Ready = status.Ready,
        };

        foreach (var limit in status.Limits)
        {
            snapshot.Limits.Add(ToLimitSnapshot(limit));
        }

        return snapshot;
    }

    private static ProviderQuotaLimitSnapshot ToLimitSnapshot(QuotaLimitStatus limit) => new()
    {
        Type = limit.Type,
        Status = limit.Status,
        UsageRatio = limit.UsageRatio,
        Ready = limit.Ready,
        NextResetAt = limit.NextResetAt,
        AvailabilityGroup = limit.AvailabilityGroup,
        Window = limit.Window,
        Account = limit.Account,
        PeriodStart = limit.PeriodStart,
        PeriodCost = limit.PeriodCost,
        PeriodQuota = limit.PeriodQuota,
    };

    /// <summary>Port of <c>groupChannelsByQuotaAccount</c> (provider_quota_account.go:39-64).</summary>
    private List<QuotaCheckGroup> GroupChannelsByQuotaAccount(
        IReadOnlyList<QuotaCheckChannel> channels)
    {
        var groups = new List<QuotaCheckGroup>(channels.Count);
        var groupIndexes = new Dictionary<QuotaCheckGroupKey, int>();

        foreach (var candidate in channels)
        {
            var accountKey = QuotaAccountKey(GetProviderType(candidate.Channel), candidate.Channel);
            if (accountKey.Length == 0)
            {
                var single = new QuotaCheckGroup(accountKey: string.Empty);
                single.Members.Add(candidate);
                groups.Add(single);
                continue;
            }

            var groupKey = QuotaCheckGroupKey.FromChannel(accountKey, candidate.Channel);
            if (groupIndexes.TryGetValue(groupKey, out var index))
            {
                groups[index].Members.Add(candidate);
                continue;
            }

            groupIndexes[groupKey] = groups.Count;
            var group = new QuotaCheckGroup(accountKey);
            group.Members.Add(candidate);
            groups.Add(group);
        }

        return groups;
    }

    /// <summary>Port of quotaAccountKey (provider_quota_account.go:25-37).</summary>
    public static string QuotaAccountKey(string providerType, ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (providerType != "zenmux")
        {
            return string.Empty;
        }

        var credentials = ParseCredentials(channel.Credentials);
        var managementKey = credentials.ManagementApiKey?.Trim() ?? string.Empty;
        if (managementKey.Length == 0)
        {
            return string.Empty;
        }

        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(providerType + ":" + managementKey));
        return Convert.ToHexStringLower(digest)[..16];
    }

    /// <summary>Port of quotaCheckGroupIsDue (provider_quota_account.go:75-83).</summary>
    private static bool IsDue(QuotaCheckGroup group, DateTimeOffset now)
    {
        foreach (var member in group.Members)
        {
            if (member.Status is null
                || AsUtc(member.Status.NextCheckAt) <= now.UtcDateTime)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of nextQuotaGroupErrorCount (provider_quota_account.go:85-99).</summary>
    private static int NextQuotaGroupErrorCount(
        IReadOnlyList<QuotaCheckChannel> members,
        string providerType)
    {
        var previousFailures = 0;
        foreach (var member in members)
        {
            var existing = member.Status;
            if (existing is null || existing.ProviderType != providerType)
            {
                continue;
            }

            previousFailures = Math.Max(previousFailures, QuotaErrorCount(existing.QuotaData));
        }

        return NextQuotaErrorCount(previousFailures);
    }

    /// <summary>Port of quotaErrorCount (provider_quota.go:102-115).</summary>
    internal static int QuotaErrorCount(string? quotaData)
    {
        if (string.IsNullOrWhiteSpace(quotaData))
        {
            return 0;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(quotaData);
        }
        catch (JsonException)
        {
            return 0;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error_count", out var value)
                || value.ValueKind != JsonValueKind.Number)
            {
                return 0;
            }

            // Go stores the counter as an int in-process and reads it back as a
            // float64 from JSON; the cast truncates toward zero in both.
            return (int)value.GetDouble();
        }
    }

    /// <summary>Port of nextQuotaErrorCount (provider_quota.go:117-126).</summary>
    internal static int NextQuotaErrorCount(int previous)
        => previous + 1 > MaxQuotaErrorBackoffSteps
            ? MaxQuotaErrorBackoffSteps
            : previous + 1;

    /// <summary>Port of quotaErrorBackoff (provider_quota.go:85-100).</summary>
    internal static TimeSpan QuotaErrorBackoff(TimeSpan @base, int failures)
    {
        var multiplier = 1;
        for (var i = 1; i < failures && multiplier < MaxQuotaErrorBackoffMultiplier; i++)
        {
            multiplier *= 2;
        }

        if (multiplier > MaxQuotaErrorBackoffMultiplier)
        {
            multiplier = MaxQuotaErrorBackoffMultiplier;
        }

        return @base * multiplier;
    }

    /// <summary>Port of quotaErrorCode (provider_quota.go:46-52).</summary>
    internal static string QuotaErrorCode(Exception? error)
        => error is not null && error.Message == "channel has no credentials"
            ? ErrorCodeMissingCredentials
            : ErrorCodeCheckFailed;

    /// <summary>Port of nextCheckIntervalForStatus (provider_quota.go:527-532).</summary>
    private TimeSpan NextCheckIntervalForStatus(string status)
        => status == ProviderQuotaStatuses.Warning
            ? GetWarningCheckInterval()
            : GetCheckInterval();

    /// <summary>Port of getWarningCheckInterval (provider_quota.go:518-525).</summary>
    private TimeSpan GetWarningCheckInterval()
        => GetCheckInterval() * DefaultWarningCheckIntervalRatio;

    /// <summary>Port of getCheckInterval (provider_quota.go:534-540).</summary>
    private static TimeSpan GetCheckInterval() => DefaultCheckInterval;

    /// <summary>Port of <c>intervalToCronExpr</c> (provider_quota.go:482-516).</summary>
    public static string IntervalToCronExpr(TimeSpan interval, ILogger? logger = null)
    {
        var minutes = (int)interval.TotalMinutes;
        var hours = (int)interval.TotalHours;

        // Hourly or longer intervals.
        if (hours >= 1 && minutes % 60 == 0)
        {
            return hours == 1 ? "0 * * * *" : "0 */" + hours + " * * *";
        }

        // Minute intervals that divide evenly into 60.
        if (minutes > 0 && 60 % minutes == 0)
        {
            return "*/" + minutes + " * * * *";
        }

        // Round down to the nearest supported interval.
        int[] supportedIntervals = [1, 2, 3, 4, 5, 6, 10, 12, 15, 20, 30, 60];
        var rounded = 60;
        foreach (var candidate in supportedIntervals)
        {
            if (candidate <= minutes)
            {
                rounded = candidate;
            }
        }

        if (logger is not null)
        {
            LogIntervalRounded(logger, minutes, rounded, null);
        }

        return "*/" + rounded + " * * * *";
    }

    /// <summary>
    /// Port of <c>mergeLimitsIntoQuotaData</c> (provider_quota.go:1097-1139).
    /// </summary>
    internal static string MergeLimitsIntoQuotaData(QuotaData quotaData)
    {
        ArgumentNullException.ThrowIfNull(quotaData);

        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in quotaData.RawData)
        {
            data[key] = value;
        }

        if (quotaData.Resets is not null)
        {
            data["_resets"] = quotaData.Resets;
        }

        if (quotaData.Limits.Count > 0)
        {
            var limitMaps = new List<Dictionary<string, object?>>(quotaData.Limits.Count);
            foreach (var limit in quotaData.Limits)
            {
                var map = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = limit.Type,
                    ["status"] = limit.Status,
                    ["usageRatio"] = limit.UsageRatio,
                    ["ready"] = limit.Ready,
                };

                if (limit.NextResetAt is { } nextResetAt)
                {
                    map["nextResetAt"] = FormatRfc3339(nextResetAt);
                }

                if (!string.IsNullOrEmpty(limit.Window))
                {
                    map["window"] = limit.Window;
                }

                if (!string.IsNullOrEmpty(limit.Account))
                {
                    map["account"] = limit.Account;
                }

                if (!string.IsNullOrEmpty(limit.AvailabilityGroup))
                {
                    map["availabilityGroup"] = limit.AvailabilityGroup;
                }

                if (limit.PeriodStart is { } periodStart)
                {
                    map["periodStart"] = FormatRfc3339(periodStart);
                }

                if (limit.PeriodCost is { } periodCost)
                {
                    map["periodCost"] = periodCost;
                }

                if (limit.PeriodQuota is { } periodQuota)
                {
                    map["periodQuota"] = periodQuota;
                }

                limitMaps.Add(map);
            }

            data["_limits"] = limitMaps;
        }

        return AxonHub.Llm.Json.GoJson.MarshalToString(data);
    }

    /// <summary>
    /// Port of <c>extractLimitsFromQuotaData</c> (provider_quota.go:1141-1219).
    /// Go's zero <c>QuotaLimitStatus.Type</c> is the empty string, while the
    /// port's initializer defaults to <c>token</c>, so an absent type is reset
    /// explicitly.
    /// </summary>
    internal static List<QuotaLimitStatus>? ExtractLimitsFromQuotaData(string? quotaData)
    {
        if (string.IsNullOrWhiteSpace(quotaData))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(quotaData);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("_limits", out var rawLimits)
                || rawLimits.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var limits = new List<QuotaLimitStatus>();
            foreach (var element in rawLimits.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var limit = new QuotaLimitStatus
                {
                    // Go's zero value; the port's initializer is "token".
                    Type = string.Empty,
                };

                if (TryGetString(element, "type", out var type))
                {
                    limit.Type = type;
                }

                if (TryGetString(element, "status", out var status))
                {
                    limit.Status = status;
                }

                if (TryGetNumber(element, "usageRatio", out var usageRatio))
                {
                    limit.UsageRatio = usageRatio;
                }

                if (TryGetBoolean(element, "ready", out var ready))
                {
                    limit.Ready = ready;
                }

                if (TryGetString(element, "nextResetAt", out var nextResetAt)
                    && TryParseRfc3339(nextResetAt, out var nextReset))
                {
                    limit.NextResetAt = nextReset;
                }

                if (TryGetString(element, "window", out var window))
                {
                    limit.Window = window;
                }

                if (TryGetString(element, "account", out var account))
                {
                    limit.Account = account;
                }

                if (TryGetString(element, "availabilityGroup", out var availabilityGroup))
                {
                    limit.AvailabilityGroup = availabilityGroup;
                }

                if (TryGetString(element, "periodStart", out var periodStart)
                    && TryParseRfc3339(periodStart, out var periodStartAt))
                {
                    limit.PeriodStart = periodStartAt;
                }

                if (TryGetNumber(element, "periodCost", out var periodCost))
                {
                    limit.PeriodCost = periodCost;
                }

                if (TryGetNumber(element, "periodQuota", out var periodQuota))
                {
                    limit.PeriodQuota = periodQuota;
                }

                limits.Add(limit);
            }

            return limits;
        }
    }

    /// <summary>
    /// Port of the error-data merge in saveQuotaError: existing quota data plus
    /// the new <c>error_code</c>/<c>error_count</c>, with the legacy
    /// <c>error</c> key removed.
    /// </summary>
    internal static string MergeQuotaErrorData(
        string? existingQuotaData,
        string errorCode,
        int failures)
    {
        JsonObject merged;
        try
        {
            merged = string.IsNullOrWhiteSpace(existingQuotaData)
                ? new JsonObject()
                : JsonNode.Parse(existingQuotaData) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            // Ent would have failed to decode the stored column; keep going with
            // an empty document (C013/C182).
            merged = new JsonObject();
        }

        merged["error_code"] = errorCode;
        merged["error_count"] = failures;
        merged.Remove("error");
        return AxonHub.Llm.Json.GoJson.MarshalToString(merged);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetNumber(JsonElement element, string name, out double value)
    {
        if (element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number)
        {
            value = property.GetDouble();
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryGetBoolean(JsonElement element, string name, out bool value)
    {
        if (element.TryGetProperty(name, out var property))
        {
            if (property.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }

            if (property.ValueKind == JsonValueKind.False)
            {
                value = false;
                return true;
            }
        }

        value = false;
        return false;
    }

    private static bool TryParseRfc3339(string value, out DateTimeOffset parsed)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed);

    /// <summary>
    /// Go <c>time.Format(time.RFC3339)</c>: second precision, <c>Z</c> for a
    /// zero offset, <c>±hh:mm</c> otherwise.
    /// </summary>
    internal static string FormatRfc3339(DateTimeOffset value)
        => value.Offset == TimeSpan.Zero
            ? value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "Z"
            : value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static DateTime AsUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // timestamptz values arrive as UTC; test doubles may omit the kind.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private sealed class QuotaCheckChannel(ChannelRecord channel, ProviderQuotaStatusRecord? status)
    {
        public ChannelRecord Channel { get; } = channel;

        public ProviderQuotaStatusRecord? Status { get; } = status;
    }

    private sealed class QuotaCheckGroup(string accountKey)
    {
        public string AccountKey { get; } = accountKey;

        public List<QuotaCheckChannel> Members { get; } = [];
    }

    /// <summary>
    /// Port of quotaCheckGroupKey (provider_quota_account.go:19-23): the proxy
    /// struct compared field by field (<c>httpclient.ProxyConfig</c>).
    /// </summary>
    private readonly record struct QuotaCheckGroupKey(
        string AccountKey,
        bool HasProxy,
        string ProxyType,
        string? ProxyUrl,
        string? ProxyUsername,
        string? ProxyPassword,
        bool ProxyDisableConnectionReuse)
    {
        public static QuotaCheckGroupKey FromChannel(string accountKey, ChannelRecord channel)
        {
            var settings = ParseSettingsLenient(channel.Settings);
            if (settings?.Proxy is not { } proxy)
            {
                return new QuotaCheckGroupKey(accountKey, false, string.Empty, null, null, null, false);
            }

            return new QuotaCheckGroupKey(
                accountKey,
                true,
                proxy.Type,
                proxy.Url,
                proxy.Username,
                proxy.Password,
                proxy.DisableConnectionReuse ?? false);
        }
    }
}

/// <summary>
/// Port of the quota-invalidation hook
/// (internal/server/biz/channel_provider_quota_hook.go): the channel service
/// drops the channel's persisted and cached quota when its provider identity
/// changes or its quota cookie is cleared.
/// </summary>
public interface IProviderQuotaInvalidator
{
    Task InvalidateChannelQuotaAsync(
        long channelId,
        CancellationToken cancellationToken = default);
}
