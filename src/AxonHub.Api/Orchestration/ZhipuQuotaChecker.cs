using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the ZhiPu quota checker
/// (internal/server/biz/provider_quota/zhipu_checker.go): queries
/// open.bigmodel.cn once per channel API key and folds the per-account answers
/// into one payload. The shared <c>collectZhipuFamilyQuota</c> collector is the
/// piece the zai and charm_hyper checkers reuse; this slice registers the
/// zhipu provider.
/// </summary>
public sealed class ZhipuQuotaChecker : IQuotaChecker
{
    private const string ProviderType = "zhipu";

    /// <summary>zhipuDefaultQuotaBaseURL (zhipu_checker.go:21).</summary>
    internal const string DefaultQuotaBaseUrl = "https://open.bigmodel.cn";

    /// <summary>The path buildZhipuQuotaURL always appends (zhipu_checker.go:717-721).</summary>
    internal const string QuotaPath = "/api/monitor/usage/quota/limit";

    /// <summary>
    /// zhipuAccountsAvailabilityGroup (zhipu_checker.go:28): every account of a
    /// multi-key channel shares this group, so routing ORs the accounts.
    /// </summary>
    internal const string AccountsAvailabilityGroup = "zhipu_accounts";

    /// <summary>maxZhipuQuotaDisabledAccounts (zhipu_checker.go:33).</summary>
    internal const int MaxQuotaDisabledAccounts = 32;

    /// <summary>zhipuWindowFiveHour (zhipu_checker.go:38).</summary>
    internal const string WindowFiveHour = "five_hour";

    /// <summary>zhipuWindowWeekly (zhipu_checker.go:39).</summary>
    internal const string WindowWeekly = "weekly_limit";

    /// <summary>zhipuCreditUnitHour (zhipu_checker.go:44).</summary>
    internal const long CreditUnitHour = 3;

    /// <summary>zhipuCreditUnitWeek (zhipu_checker.go:45).</summary>
    internal const long CreditUnitWeek = 6;

    private const string RequestFailedPrefix = "zhipu quota request failed: ";

    private const string ParseResponseErrorPrefix = "failed to parse zhipu quota response: ";

    private const string NoApiKeyMessage = "channel has no API key";

    private const string NoDataMessage = "zhipu quota response contains no data";

    private const string NoWindowsMessage =
        "zhipu quota response contains no TOKENS_LIMIT or CREDIT_LIMIT entries";

    // time.Hour is 3.6e12 int64 nanoseconds; the reference multiplies
    // time.Duration values and lets overflow wrap (zhipu_checker.go:612-623).
    private const long NanosecondsPerHour = 3_600_000_000_000L;

    private const long FiveHourNanoseconds = 5 * NanosecondsPerHour;

    private const long WeeklyNanoseconds = 7 * 24 * NanosecondsPerHour;

    internal static readonly ZhipuWindowSpec FiveHourSpec =
        new(WindowFiveHour, QuotaWindows.FiveHours, TimeSpan.FromHours(5));

    internal static readonly ZhipuWindowSpec WeeklySpec =
        new(WindowWeekly, QuotaWindows.Weekly, TimeSpan.FromDays(7));

    private static readonly Action<ILogger, long, int, int, Exception?> LogTruncatesDisabledKeys =
        LoggerMessage.Define<long, int, int>(
            LogLevel.Warning,
            new EventId(0, "ZhipuQuotaTruncatesDisabledKeys"),
            "zhipu quota check truncates disabled keys: channel_id={ChannelId}"
            + " disabled_keys={DisabledKeys} checked={Checked}");

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ZhipuQuotaChecker> _logger;

    public ZhipuQuotaChecker(
        IExecutor executor,
        IProxyExecutorFactory proxyExecutors,
        TimeProvider timeProvider,
        ILogger<ZhipuQuotaChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(proxyExecutors);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _executor = executor;
        _proxyExecutors = proxyExecutors;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Port of (*ZhipuQuotaChecker).CheckQuota (zhipu_checker.go:130-132).
    /// </summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return Task.FromResult(CollectFamilyQuota(
            channel,
            ProviderType,
            BuildQuotaUrl(channel.BaseUrl),
            cancellationToken));
    }

    /// <summary>
    /// Port of (*ZhipuQuotaChecker).SupportsChannel (zhipu_checker.go:134-137).
    /// </summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type is ChannelOutboundFactory.ZhipuChannelType
            or ChannelOutboundFactory.ZhipuAnthropicChannelType;
    }

    /// <summary>
    /// Port of collectZhipuFamilyQuota (zhipu_checker.go:142-240): one request
    /// per key, failures recorded per account, and a channel-level error only
    /// when no serving key could be read at all.
    /// </summary>
    internal QuotaData CollectFamilyQuota(
        ChannelRecord channel,
        string providerType,
        string quotaUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var keys = ChannelApiKeys(channel);
        if (keys.Count == 0)
        {
            throw new InvalidOperationException(NoApiKeyMessage);
        }

        // provider_quota.go:46-48: a channel proxy replaces the shared client.
        var executor = ResolveExecutor(channel);
        var disabled = DisabledKeySet(channel);

        // Serving keys decide the channel status, so every one of them is
        // checked; parked keys are display-only and bounded.
        var enabledKeys = new List<string>(keys.Count);
        var disabledKeys = new List<string>(keys.Count);
        foreach (var key in keys)
        {
            if (disabled.Contains(key))
            {
                disabledKeys.Add(key);
            }
            else
            {
                enabledKeys.Add(key);
            }
        }

        if (disabledKeys.Count > MaxQuotaDisabledAccounts)
        {
            LogTruncatesDisabledKeys(
                _logger, channel.Id, disabledKeys.Count, MaxQuotaDisabledAccounts, null);
            disabledKeys.RemoveRange(
                MaxQuotaDisabledAccounts,
                disabledKeys.Count - MaxQuotaDisabledAccounts);
        }

        var targets = new List<string>(enabledKeys.Count + disabledKeys.Count);
        targets.AddRange(enabledKeys);
        targets.AddRange(disabledKeys);

        var accounts = new List<ZhipuAccountQuota>(targets.Count);
        var failures = 0;
        var enabledSuccesses = 0;
        Exception? firstError = null;

        foreach (var key in targets)
        {
            var account = new ZhipuAccountQuota
            {
                Ref = KeyRef(key),
                Suffix = KeySuffix(key),
                Disabled = disabled.Contains(key),
            };

            try
            {
                var snapshot = FetchAccountQuota(executor, quotaUrl, key, cancellationToken);
                if (!account.Disabled)
                {
                    enabledSuccesses++;
                }

                account.Status = snapshot.Status;
                account.Ready = snapshot.Ready;
                account.Level = snapshot.Level.Length == 0 ? null : snapshot.Level;
                account.Rows = snapshot.Rows.Count == 0 ? null : snapshot.Rows;
            }
            catch (Exception ex)
            {
                failures++;
                account.Status = ProviderQuotaStatuses.Unknown;
                account.Error = ex.Message;
                firstError ??= ex;
            }

            accounts.Add(account);
        }

        if (accounts.Count == 0 || failures == accounts.Count)
        {
            // Every key failed: keep the underlying reason instead of a
            // generic summary. The count is the untruncated key count.
            throw GoErrors.Wrap(
                firstError!,
                "zhipu quota check failed for all "
                + Invariant(keys.Count)
                + " keys: ");
        }

        if (enabledKeys.Count > 0 && enabledSuccesses == 0)
        {
            // No serving key could be read; disabled accounts must not mask the
            // failure into a bogus exhausted verdict.
            throw GoErrors.Wrap(
                firstError!,
                "zhipu quota check failed for all "
                + Invariant(enabledKeys.Count)
                + " enabled keys: ");
        }

        return BuildQuotaData(providerType, accounts, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Port of fetchZhipuAccountQuota (zhipu_checker.go:242-262): the API key
    /// is sent verbatim as the Authorization header (no bearer prefix).
    /// </summary>
    private static ZhipuAccountSnapshot FetchAccountQuota(
        IExecutor executor,
        string quotaUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Authorization", apiKey);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept-Language", "en-US,en");

        var request = new HttpRequestData
        {
            Method = "GET",
            Url = quotaUrl,
            Headers = headers,
        };

        HttpResponseData response;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, RequestFailedPrefix);
        }

        return ParseAccountSnapshot(response.Body ?? []);
    }

    /// <summary>
    /// Port of buildZhipuQuotaData (zhipu_checker.go:264-288).
    /// </summary>
    internal static QuotaData BuildQuotaData(
        string providerType,
        List<ZhipuAccountQuota> accounts,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var usable = new List<ZhipuAccountQuota>(accounts.Count);
        foreach (var account in accounts)
        {
            if (!account.Disabled
                && string.IsNullOrEmpty(account.Error)
                && account.Rows is { Count: > 0 })
            {
                usable.Add(account);
            }
        }

        var (status, ready) = AggregateStatus(usable);

        var rawData = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["level"] = AggregateLevel(accounts),
            ["rows"] = WorstRows(usable),
            ["accounts"] = accounts,
        };

        return ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = status,
                ProviderType = providerType,
                RawData = rawData,
                NextResetAt = EarliestReset(usable),
                Ready = ready,
                Limits = ChannelLimits(accounts, usable),
            },
            now);
    }

    /// <summary>
    /// Port of zhipuAggregateStatus (zhipu_checker.go:290-318): only a pool
    /// without a single usable account is exhausted.
    /// </summary>
    internal static (string Status, bool Ready) AggregateStatus(
        IReadOnlyList<ZhipuAccountQuota> usable)
    {
        ArgumentNullException.ThrowIfNull(usable);

        if (usable.Count == 0)
        {
            return (ProviderQuotaStatuses.Exhausted, false);
        }

        var status = string.Empty;
        foreach (var account in usable)
        {
            if (account.Status == ProviderQuotaStatuses.Exhausted)
            {
                continue;
            }

            if (status.Length == 0)
            {
                status = account.Status;
                continue;
            }

            status = WorseStatus(status, account.Status);
        }

        if (status.Length == 0)
        {
            return (ProviderQuotaStatuses.Exhausted, false);
        }

        return (status, ProviderQuotaStatuses.IsReadyStatus(status));
    }

    /// <summary>
    /// Port of zhipuChannelLimits (zhipu_checker.go:320-330): a single-key
    /// channel keeps one limit per window (AND semantics); several keys report
    /// the binding window of every usable account in one availability group.
    /// </summary>
    internal static List<QuotaLimitStatus> ChannelLimits(
        IReadOnlyList<ZhipuAccountQuota> accounts,
        IReadOnlyList<ZhipuAccountQuota> usable)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(usable);

        if (accounts.Count == 1 && usable.Count == 1)
        {
            return WindowLimits(accounts[0]);
        }

        return BindingLimits(usable);
    }

    /// <summary>Port of zhipuWindowLimits (zhipu_checker.go:332-348).</summary>
    internal static List<QuotaLimitStatus> WindowLimits(ZhipuAccountQuota account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var rows = account.Rows ?? [];
        var limits = new List<QuotaLimitStatus>(rows.Count);
        foreach (var row in rows)
        {
            if (!TrySpecByName(row.Window, out var spec))
            {
                continue;
            }

            var limit = ProviderQuotaData.NewTokenLimitStatus(
                row.Status,
                row.UsedPercent / 100.0,
                RowResetAt(row));
            limits.Add(ProviderQuotaData.WithWindow(limit, spec.Label, spec.Length));
        }

        return limits;
    }

    /// <summary>Port of zhipuBindingLimits (zhipu_checker.go:350-377).</summary>
    internal static List<QuotaLimitStatus> BindingLimits(
        IReadOnlyList<ZhipuAccountQuota> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var limits = new List<QuotaLimitStatus>(accounts.Count);
        foreach (var account in accounts)
        {
            var (row, spec) = BindingRow(account.Rows);
            if (row is null || spec is null)
            {
                continue;
            }

            limits.Add(new QuotaLimitStatus
            {
                Type = QuotaLimitTypes.Token,
                Status = row.Status,
                UsageRatio = row.UsedPercent / 100.0,
                Ready = ProviderQuotaStatuses.IsReadyStatus(row.Status),
                NextResetAt = RowResetAt(row),
                AvailabilityGroup = AccountsAvailabilityGroup,
                Window = spec.Label,
                Account = account.Suffix,
            });
        }

        return limits;
    }

    /// <summary>Port of zhipuBindingRow (zhipu_checker.go:379-401).</summary>
    internal static (ZhipuWindowRow? Row, ZhipuWindowSpec? Spec) BindingRow(
        IReadOnlyList<ZhipuWindowRow>? rows)
    {
        ZhipuWindowRow? binding = null;
        ZhipuWindowSpec? spec = null;
        foreach (var row in rows ?? [])
        {
            if (!TrySpecByName(row.Window, out var candidate))
            {
                continue;
            }

            if (binding is null || row.UsedPercent > binding.UsedPercent)
            {
                binding = row;
                spec = candidate;
            }
        }

        return (binding, spec);
    }

    /// <summary>
    /// Port of zhipuWorstRows (zhipu_checker.go:403-430): the most used account
    /// per window, always in five-hour then weekly order.
    /// </summary>
    internal static List<ZhipuWindowRow> WorstRows(
        IReadOnlyList<ZhipuAccountQuota> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var rows = new List<ZhipuWindowRow>(2);
        foreach (var spec in new[] { FiveHourSpec, WeeklySpec })
        {
            ZhipuWindowRow? worst = null;
            foreach (var account in accounts)
            {
                foreach (var row in account.Rows ?? [])
                {
                    if (row.Window != spec.Name)
                    {
                        continue;
                    }

                    if (worst is null || row.UsedPercent > worst.UsedPercent)
                    {
                        worst = row;
                    }
                }
            }

            if (worst is not null)
            {
                rows.Add(worst);
            }
        }

        return rows;
    }

    /// <summary>Port of zhipuAggregateLevel (zhipu_checker.go:432-440).</summary>
    internal static string AggregateLevel(IReadOnlyList<ZhipuAccountQuota> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        foreach (var account in accounts)
        {
            if (!string.IsNullOrEmpty(account.Level))
            {
                return account.Level;
            }
        }

        return string.Empty;
    }

    /// <summary>Port of zhipuEarliestReset (zhipu_checker.go:442-459).</summary>
    internal static DateTimeOffset? EarliestReset(
        IReadOnlyList<ZhipuAccountQuota> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        DateTimeOffset? earliest = null;
        foreach (var account in accounts)
        {
            foreach (var row in account.Rows ?? [])
            {
                var resetAt = RowResetAt(row);
                if (resetAt is null)
                {
                    continue;
                }

                if (earliest is null || resetAt < earliest)
                {
                    earliest = resetAt;
                }
            }
        }

        return earliest;
    }

    /// <summary>
    /// Port of zhipuRowResetAt (zhipu_checker.go:461-472): the stored text is
    /// RFC3339 with whole seconds, so the millisecond part of a reset is
    /// dropped by the round-trip.
    /// </summary>
    internal static DateTimeOffset? RowResetAt(ZhipuWindowRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.ResetAt is null)
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(
            row.ResetAt,
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var resetAt)
            ? resetAt
            : null;
    }

    /// <summary>
    /// Port of parseZhipuFamilyQuotaResponse (zhipu_checker.go:479-495), kept
    /// for the family checkers that parse a single account payload.
    /// </summary>
    internal static QuotaData ParseFamilyQuotaResponse(
        byte[] body,
        string providerType,
        DateTimeOffset now)
    {
        var snapshot = ParseAccountSnapshot(body);

        return BuildQuotaData(
            providerType,
            [
                new ZhipuAccountQuota
                {
                    Status = snapshot.Status,
                    Ready = snapshot.Ready,
                    Level = snapshot.Level.Length == 0 ? null : snapshot.Level,
                    Rows = snapshot.Rows.Count == 0 ? null : snapshot.Rows,
                },
            ],
            now);
    }

    /// <summary>
    /// Port of parseZhipuAccountSnapshot (zhipu_checker.go:497-536).
    /// </summary>
    internal static ZhipuAccountSnapshot ParseAccountSnapshot(byte[] body)
    {
        ZhipuQuotaResponse response;
        try
        {
            response = ParseQuotaResponse(body);
        }
        catch (JsonException ex)
        {
            throw GoErrors.Wrap(ex, ParseResponseErrorPrefix);
        }

        if (!response.Success)
        {
            var msg = response.Msg;
            if (msg.Length == 0)
            {
                msg = "API error code " + Invariant(response.Code);
            }

            throw new InvalidOperationException("zhipu API error: " + msg);
        }

        if (response.Data is null)
        {
            throw new InvalidOperationException(NoDataMessage);
        }

        var windows = ResponseWindows(response.Data.Limits);
        if (windows.Count == 0)
        {
            throw new InvalidOperationException(NoWindowsMessage);
        }

        var snapshot = new ZhipuAccountSnapshot
        {
            Level = response.Data.Level,
            Status = ProviderQuotaStatuses.Available,
        };

        foreach (var window in windows)
        {
            var (row, resetAt) = ToRow(window);
            snapshot.Rows.Add(row);
            snapshot.Status = WorseStatus(snapshot.Status, row.Status);

            if (resetAt is { } reset
                && (snapshot.NextResetAt is null || reset < snapshot.NextResetAt))
            {
                snapshot.NextResetAt = reset;
            }
        }

        snapshot.Ready = ProviderQuotaStatuses.IsReadyStatus(snapshot.Status);
        return snapshot;
    }

    /// <summary>Port of (zhipuResponseWindow).toRow (zhipu_checker.go:544-563).</summary>
    private static (ZhipuWindowRow Row, DateTimeOffset? ResetAt) ToRow(
        ZhipuResponseWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var entry = window.Entry;
        var ratio = entry.Percentage / 100.0;
        var row = new ZhipuWindowRow
        {
            Window = window.Spec.Name,
            UsedPercent = entry.Percentage,
            Status = StatusForRatio(ratio),
            Usage = entry.Usage,
            Used = entry.CurrentValue,
            Remaining = entry.Remaining,
        };

        DateTimeOffset? resetAt = null;
        if (entry.NextResetTime is { } milliseconds && milliseconds > 0)
        {
            resetAt = FromUnixMilliseconds(milliseconds);
            if (resetAt is { } reset)
            {
                row.ResetAt = FormatRfc3339Seconds(reset);
            }
        }

        return (row, resetAt);
    }

    /// <summary>
    /// Port of zhipuResponseWindows (zhipu_checker.go:565-583): CREDIT_LIMIT
    /// entries (GLM coding plan) win; otherwise TOKENS_LIMIT entries are
    /// ordered so the bucket without a reset time leads.
    /// </summary>
    internal static List<ZhipuResponseWindow> ResponseWindows(
        IReadOnlyList<ZhipuLimitEntry>? limits)
    {
        var entries = limits ?? [];
        var creditEntries = new List<ZhipuLimitEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (EqualFoldAscii(entry.Type, "CREDIT_LIMIT"))
            {
                creditEntries.Add(entry);
            }
        }

        if (creditEntries.Count > 0)
        {
            return BuildResponseWindows(creditEntries);
        }

        var tokenEntries = new List<ZhipuLimitEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (EqualFoldAscii(entry.Type, "TOKENS_LIMIT"))
            {
                tokenEntries.Add(entry);
            }
        }

        return BuildResponseWindows(OrderBuckets(tokenEntries));
    }

    /// <summary>
    /// Port of zhipuBuildResponseWindows (zhipu_checker.go:585-602): the
    /// unit/number pair wins, the API position is the fallback.
    /// </summary>
    internal static List<ZhipuResponseWindow> BuildResponseWindows(
        IReadOnlyList<ZhipuLimitEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var windows = new List<ZhipuResponseWindow>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!TryCreditWindowSpec(entry, out var spec)
                && !TryPositionalSpec(index, out spec))
            {
                continue;
            }

            windows.Add(new ZhipuResponseWindow(spec, entry));
        }

        return windows;
    }

    /// <summary>
    /// Port of zhipuCreditWindowSpec (zhipu_checker.go:604-624): only
    /// 5-hour and 1-week windows are recognised; the duration arithmetic wraps
    /// like Go's int64 time.Duration.
    /// </summary>
    internal static bool TryCreditWindowSpec(
        ZhipuLimitEntry entry,
        [NotNullWhen(true)] out ZhipuWindowSpec? spec)
    {
        ArgumentNullException.ThrowIfNull(entry);

        spec = null;
        if (entry.Unit is not { } unit
            || entry.Number is not { } number
            || number <= 0)
        {
            return false;
        }

        long nanoseconds;
        switch (unit)
        {
            case CreditUnitHour:
                nanoseconds = unchecked(number * NanosecondsPerHour);
                break;
            case CreditUnitWeek:
                nanoseconds = unchecked(
                    unchecked(unchecked(number * 7) * 24) * NanosecondsPerHour);
                break;
            default:
                return false;
        }

        return TrySpecForLength(nanoseconds, out spec);
    }

    /// <summary>Port of zhipuWindowSpecForLength (zhipu_checker.go:626-635).</summary>
    internal static bool TrySpecForLength(
        long nanoseconds,
        [NotNullWhen(true)] out ZhipuWindowSpec? spec)
    {
        if (nanoseconds == FiveHourNanoseconds)
        {
            spec = FiveHourSpec;
            return true;
        }

        if (nanoseconds == WeeklyNanoseconds)
        {
            spec = WeeklySpec;
            return true;
        }

        spec = null;
        return false;
    }

    /// <summary>Port of zhipuWindowSpecByName (zhipu_checker.go:637-646).</summary>
    internal static bool TrySpecByName(
        string name,
        [NotNullWhen(true)] out ZhipuWindowSpec? spec)
    {
        switch (name)
        {
            case WindowFiveHour:
                spec = FiveHourSpec;
                return true;
            case WindowWeekly:
                spec = WeeklySpec;
                return true;
            default:
                spec = null;
                return false;
        }
    }

    /// <summary>Port of zhipuPositionalSpec (zhipu_checker.go:648-657).</summary>
    internal static bool TryPositionalSpec(
        int index,
        [NotNullWhen(true)] out ZhipuWindowSpec? spec)
    {
        switch (index)
        {
            case 0:
                spec = FiveHourSpec;
                return true;
            case 1:
                spec = WeeklySpec;
                return true;
            default:
                spec = null;
                return false;
        }
    }

    /// <summary>
    /// Port of zhipuChannelAPIKeys (zhipu_checker.go:659-681): trimmed,
    /// non-empty and de-duplicated, in credential order.
    /// </summary>
    internal static List<string> ChannelApiKeys(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var candidates = ChannelCredentials.GetAllApiKeys(channel.Credentials);
        var keys = new List<string>(candidates.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var key = GoStrings.TrimSpace(candidate);
            if (key.Length == 0)
            {
                continue;
            }

            if (!seen.Add(key))
            {
                continue;
            }

            keys.Add(key);
        }

        return keys;
    }

    /// <summary>Port of zhipuDisabledKeySet (zhipu_checker.go:683-697).</summary>
    internal HashSet<string> DisabledKeySet(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var disabled = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<DisabledApiKey> entries;
        try
        {
            entries = ChannelWriteSerialization.ParseDisabledApiKeys(channel.DisabledApiKeys);
        }
        catch (JsonException)
        {
            // The reference decodes the column with Ent before reaching the
            // checker; malformed JSON is treated as "no settings" (C013).
            return disabled;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var entry in entries)
        {
            var key = GoStrings.TrimSpace(entry.Key);
            if (key.Length == 0 || entry.IsExpired(now))
            {
                continue;
            }

            disabled.Add(key);
        }

        return disabled;
    }

    /// <summary>Port of zhipuKeyRef (zhipu_checker.go:699-704).</summary>
    internal static string KeyRef(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("zhipu:" + key));
        return Convert.ToHexStringLower(digest)[..8];
    }

    /// <summary>Port of zhipuKeySuffix (zhipu_checker.go:706-715).</summary>
    internal static string KeySuffix(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var runes = key.EnumerateRunes().ToArray();
        if (runes.Length <= 4)
        {
            return key;
        }

        var builder = new StringBuilder(4);
        for (var index = runes.Length - 4; index < runes.Length; index++)
        {
            builder.Append(runes[index].ToString());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Port of buildZhipuQuotaURL (zhipu_checker.go:717-727): every ZhiPu
    /// coding plan channel uses open.bigmodel.cn regardless of its base URL.
    /// </summary>
    internal static string BuildQuotaUrl(string? baseUrl)
    {
        _ = baseUrl;
        return DefaultQuotaBaseUrl + QuotaPath;
    }

    /// <summary>Port of zhipuStatusForRatio (zhipu_checker.go:729-739).</summary>
    internal static string StatusForRatio(double ratio)
    {
        if (ratio >= 1.0)
        {
            return ProviderQuotaStatuses.Exhausted;
        }

        if (ratio >= ProviderQuotaData.WarningThresholdRatio)
        {
            return ProviderQuotaStatuses.Warning;
        }

        return ProviderQuotaStatuses.Available;
    }

    /// <summary>Port of worseZhipuStatus (zhipu_checker.go:741-748).</summary>
    internal static string WorseStatus(string current, string candidate)
        => CheckerStatusRank(candidate) > CheckerStatusRank(current) ? candidate : current;

    /// <summary>
    /// The reference's local rank map (zhipu_checker.go:742): available 0,
    /// warning 1, exhausted 2, anything else 0.
    /// </summary>
    internal static int CheckerStatusRank(string? status) => status switch
    {
        ProviderQuotaStatuses.Exhausted => 2,
        ProviderQuotaStatuses.Warning => 1,
        ProviderQuotaStatuses.Available => 0,
        _ => 0,
    };

    /// <summary>
    /// Port of orderZhipuBuckets (zhipu_checker.go:750-768): buckets without a
    /// positive reset time come first, preserving API order within each half.
    /// </summary>
    internal static List<ZhipuLimitEntry> OrderBuckets(
        IReadOnlyList<ZhipuLimitEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var withoutReset = new List<ZhipuLimitEntry>(entries.Count);
        var withReset = new List<ZhipuLimitEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.NextResetTime is not { } reset || reset <= 0)
            {
                withoutReset.Add(entry);
            }
            else
            {
                withReset.Add(entry);
            }
        }

        withoutReset.AddRange(withReset);
        return withoutReset;
    }

    /// <summary>
    /// Port of the <c>json.Unmarshal(body, &amp;response)</c> call at
    /// zhipu_checker.go:498-501, including Go's pointer/struct/slice merge
    /// semantics for duplicate keys and its strict number rules.
    /// </summary>
    /// <exception cref="JsonException">
    /// The payload is not valid JSON or a field has the wrong JSON type; the
    /// message follows Go's UnmarshalTypeError text, while malformed JSON keeps
    /// the System.Text.Json wording (C003).
    /// </exception>
    internal static ZhipuQuotaResponse ParseQuotaResponse(byte[] data)
    {
        using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        var root = document.RootElement;

        // Go: unmarshaling JSON null into a struct is a no-op.
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new ZhipuQuotaResponse();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoKindName(root.ValueKind)
                + " into Go value of type provider_quota.zhipuQuotaResponse");
        }

        var response = new ZhipuQuotaResponse();
        foreach (var property in root.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "success":
                    DecodeBool(
                        property.Value,
                        "zhipuQuotaResponse",
                        "success",
                        value => response.Success = value);
                    break;
                case "code":
                    DecodeInteger(
                        property.Value,
                        "zhipuQuotaResponse",
                        "code",
                        "int",
                        value => response.Code = value);
                    break;
                case "msg":
                    DecodeString(
                        property.Value,
                        "zhipuQuotaResponse",
                        "msg",
                        value => response.Msg = value);
                    break;
                case "data":
                    response.Data = DecodeQuotaData(property.Value, response.Data);
                    break;
            }
        }

        return response;
    }

    /// <summary>
    /// Go decodes a repeated pointer field by reusing the pointed-to struct:
    /// later duplicate keys merge field by field; JSON null resets it to nil.
    /// </summary>
    private static ZhipuQuotaData? DecodeQuotaData(JsonElement value, ZhipuQuotaData? current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoKindName(value.ValueKind)
                + " into Go struct field zhipuQuotaResponse.data"
                + " of type provider_quota.zhipuQuotaData");
        }

        var data = current ?? new ZhipuQuotaData();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "level":
                    DecodeString(
                        property.Value,
                        "zhipuQuotaData",
                        "data.level",
                        value2 => data.Level = value2);
                    break;
                case "limits":
                    data.Limits = DecodeLimits(property.Value, data.Limits);
                    break;
            }
        }

        return data;
    }

    /// <summary>
    /// Go decodes a repeated slice field by reusing the backing array: existing
    /// element structs merge field by field, extra elements are truncated,
    /// missing ones appended; JSON null resets the slice to nil.
    /// </summary>
    private static List<ZhipuLimitEntry>? DecodeLimits(
        JsonElement value,
        List<ZhipuLimitEntry>? current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoKindName(value.ValueKind)
                + " into Go struct field zhipuQuotaData.data.limits"
                + " of type []provider_quota.zhipuLimitEntry");
        }

        var result = current ?? [];
        var index = 0;
        foreach (var element in value.EnumerateArray())
        {
            if (index >= result.Count)
            {
                result.Add(new ZhipuLimitEntry());
            }

            var target = result[index];
            if (element.ValueKind == JsonValueKind.Object)
            {
                DecodeLimitEntry(element, target);
            }
            else if (element.ValueKind != JsonValueKind.Null)
            {
                throw UnmarshalError(
                    "json: cannot unmarshal " + GoKindName(element.ValueKind)
                    + " into Go struct field zhipuQuotaData.data.limits"
                    + " of type provider_quota.zhipuLimitEntry");
            }

            index++;
        }

        if (result.Count > index)
        {
            result.RemoveRange(index, result.Count - index);
        }

        return result;
    }

    private static void DecodeLimitEntry(JsonElement element, ZhipuLimitEntry target)
    {
        foreach (var property in element.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "type":
                    DecodeString(
                        property.Value, "zhipuLimitEntry", "data.limits.type",
                        value => target.Type = value);
                    break;
                case "percentage":
                    DecodeFloat(
                        property.Value, "zhipuLimitEntry", "data.limits.percentage",
                        value => target.Percentage = value);
                    break;
                case "nextresettime":
                    DecodeIntegerPointer(
                        property.Value, "zhipuLimitEntry", "data.limits.nextResetTime",
                        value => target.NextResetTime = value);
                    break;
                case "unit":
                    DecodeIntegerPointer(
                        property.Value, "zhipuLimitEntry", "data.limits.unit",
                        value => target.Unit = value);
                    break;
                case "number":
                    DecodeIntegerPointer(
                        property.Value, "zhipuLimitEntry", "data.limits.number",
                        value => target.Number = value);
                    break;
                case "usage":
                    DecodeIntegerPointer(
                        property.Value, "zhipuLimitEntry", "data.limits.usage",
                        value => target.Usage = value);
                    break;
                case "currentvalue":
                    DecodeIntegerPointer(
                        property.Value, "zhipuLimitEntry", "data.limits.currentValue",
                        value => target.CurrentValue = value);
                    break;
                case "remaining":
                    DecodeIntegerPointer(
                        property.Value, "zhipuLimitEntry", "data.limits.remaining",
                        value => target.Remaining = value);
                    break;
            }
        }
    }

    /// <summary>Go's decoder for a bool field: JSON null is a no-op.</summary>
    private static void DecodeBool(
        JsonElement value,
        string structName,
        string field,
        Action<bool> set)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoKindName(value.ValueKind)
                + " into Go struct field " + structName + "." + field
                + " of type bool");
        }

        set(value.ValueKind == JsonValueKind.True);
    }

    /// <summary>
    /// Go's decoder for an integer field: JSON null is a no-op, and a number
    /// must be an integer literal that fits in int64 (fractions, exponents and
    /// numeric strings are type errors).
    /// </summary>
    private static void DecodeInteger(
        JsonElement value,
        string structName,
        string field,
        string goType,
        Action<long> set)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number || !TryDecodeInt64(value, out var parsed))
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoNumberValueName(value)
                + " into Go struct field " + structName + "." + field
                + " of type " + goType);
        }

        set(parsed);
    }

    /// <summary>
    /// Go's decoder for an <c>*int64</c> field: JSON null resets the pointer to
    /// nil; a wrong type keeps the pointer but is a type error.
    /// </summary>
    private static void DecodeIntegerPointer(
        JsonElement value,
        string structName,
        string field,
        Action<long?> set)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            set(null);
            return;
        }

        if (value.ValueKind != JsonValueKind.Number || !TryDecodeInt64(value, out var parsed))
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoNumberValueName(value)
                + " into Go struct field " + structName + "." + field
                + " of type int64");
        }

        set(parsed);
    }

    /// <summary>Go's decoder for a float64 field: JSON null is a no-op.</summary>
    private static void DecodeFloat(
        JsonElement value,
        string structName,
        string field,
        Action<double> set)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoKindName(value.ValueKind)
                + " into Go struct field " + structName + "." + field
                + " of type float64");
        }

        var raw = value.GetRawText();
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsInfinity(parsed))
        {
            // strconv.ParseFloat reports ErrRange on overflow, which Go's
            // decoder turns into a type error with the literal.
            throw UnmarshalError(
                "json: cannot unmarshal number " + raw
                + " into Go struct field " + structName + "." + field
                + " of type float64");
        }

        set(parsed);
    }

    /// <summary>Go's decoder for a string field: JSON null is a no-op.</summary>
    private static void DecodeString(
        JsonElement value,
        string structName,
        string field,
        Action<string> set)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoKindName(value.ValueKind)
                + " into Go struct field " + structName + "." + field
                + " of type string");
        }

        set(value.GetString() ?? string.Empty);
    }

    /// <summary>
    /// Go's integer-literal rule: no fraction, exponent or out-of-range value.
    /// </summary>
    private static bool TryDecodeInt64(JsonElement value, out long parsed)
        => long.TryParse(
            value.GetRawText(),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out parsed);

    /// <summary>
    /// Go's value description for scalar fields: numbers keep their literal
    /// (for int/float type errors), every other kind is named only.
    /// </summary>
    private static string GoNumberValueName(JsonElement value)
        => value.ValueKind == JsonValueKind.Number
            ? "number " + value.GetRawText()
            : GoKindName(value.ValueKind);

    private static JsonException UnmarshalError(string message) => new(message);

    private static string GoKindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        _ => "null",
    };

    /// <summary>Go's encoding/json field lookup folds ASCII letters only.</summary>
    private static string FoldAscii(string name)
    {
        var chars = name.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] is >= 'A' and <= 'Z')
            {
                chars[index] = (char)(chars[index] + ('a' - 'A'));
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// The reference uses strings.EqualFold; the two literals are ASCII, so
    /// ASCII folding is equivalent for any input.
    /// </summary>
    private static bool EqualFoldAscii(string value, string literal)
        => string.Equals(FoldAscii(value), FoldAscii(literal), StringComparison.Ordinal);

    /// <summary>
    /// Port of <c>time.UnixMilli(...).Format(time.RFC3339)</c>: seconds
    /// precision, with the port's UTC instant written as <c>Z</c> (C217).
    /// </summary>
    internal static string FormatRfc3339Seconds(DateTimeOffset value)
        => value.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss",
                CultureInfo.InvariantCulture)
            + "Z";

    private static DateTimeOffset? FromUnixMilliseconds(long milliseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Go's time.Time spans years far outside DateTimeOffset's range;
            // the port has no representable value (C123 / C217).
            return null;
        }
    }

    private static string Invariant(long value)
        => value.ToString(CultureInfo.InvariantCulture);

    private IExecutor ResolveExecutor(ChannelRecord channel)
        => ParseSettingsLenient(channel.Settings)?.Proxy is { } proxy
            ? _proxyExecutors.Create(ToProxyConfig(proxy))
            : _executor;

    private static ChannelSettingsDraft? ParseSettingsLenient(string? raw)
    {
        try
        {
            return ChannelWriteSerialization.ParseSettings(raw);
        }
        catch (JsonException)
        {
            // The reference decodes settings with Ent before reaching the
            // checker; malformed JSON is treated as "no settings" (C013).
            return null;
        }
    }

    private static ProxyConfig ToProxyConfig(ProxyConfigDraft proxy) => new()
    {
        Type = proxy.Type,
        Url = proxy.Url,
        Username = proxy.Username,
        Password = proxy.Password,
        DisableConnectionReuse = proxy.DisableConnectionReuse ?? false,
    };
}

/// <summary>Port of zhipuQuotaResponse (zhipu_checker.go:49-54).</summary>
internal sealed class ZhipuQuotaResponse
{
    public bool Success { get; set; }

    /// <summary>The reference's <c>int</c>; 64-bit on amd64, reported as int.</summary>
    public long Code { get; set; }

    public string Msg { get; set; } = string.Empty;

    public ZhipuQuotaData? Data { get; set; }
}

/// <summary>Port of zhipuQuotaData (zhipu_checker.go:56-59).</summary>
internal sealed class ZhipuQuotaData
{
    public string Level { get; set; } = string.Empty;

    public List<ZhipuLimitEntry>? Limits { get; set; }
}

/// <summary>Port of zhipuLimitEntry (zhipu_checker.go:65-74).</summary>
internal sealed class ZhipuLimitEntry
{
    public string Type { get; set; } = string.Empty;

    public double Percentage { get; set; }

    public long? NextResetTime { get; set; }

    public long? Unit { get; set; }

    public long? Number { get; set; }

    public long? Usage { get; set; }

    public long? CurrentValue { get; set; }

    public long? Remaining { get; set; }
}

/// <summary>Port of zhipuWindowRow (zhipu_checker.go:77-85).</summary>
internal sealed class ZhipuWindowRow
{
    [JsonPropertyName("window")]
    [JsonPropertyOrder(0)]
    public string Window { get; set; } = string.Empty;

    [JsonPropertyName("usedPercent")]
    [JsonPropertyOrder(1)]
    [JsonConverter(typeof(GoDoubleConverter))]
    public double UsedPercent { get; set; }

    [JsonPropertyName("status")]
    [JsonPropertyOrder(2)]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("resetAt")]
    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResetAt { get; set; }

    [JsonPropertyName("usage")]
    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Usage { get; set; }

    [JsonPropertyName("used")]
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Used { get; set; }

    [JsonPropertyName("remaining")]
    [JsonPropertyOrder(6)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Remaining { get; set; }
}

/// <summary>
/// Port of zhipuAccountQuota (zhipu_checker.go:90-99): only a short key digest
/// and the trailing four characters ever reach the payload.
/// </summary>
internal sealed class ZhipuAccountQuota
{
    [JsonPropertyName("ref")]
    [JsonPropertyOrder(0)]
    public string Ref { get; set; } = string.Empty;

    [JsonPropertyName("suffix")]
    [JsonPropertyOrder(1)]
    public string Suffix { get; set; } = string.Empty;

    [JsonPropertyName("disabled")]
    [JsonPropertyOrder(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; set; }

    [JsonPropertyName("status")]
    [JsonPropertyOrder(3)]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("ready")]
    [JsonPropertyOrder(4)]
    public bool Ready { get; set; }

    [JsonPropertyName("level")]
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Level { get; set; }

    [JsonPropertyName("error")]
    [JsonPropertyOrder(6)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    [JsonPropertyName("rows")]
    [JsonPropertyOrder(7)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ZhipuWindowRow>? Rows { get; set; }
}

/// <summary>Port of zhipuAccountSnapshot (zhipu_checker.go:102-108).</summary>
internal sealed class ZhipuAccountSnapshot
{
    public string Level { get; set; } = string.Empty;

    public List<ZhipuWindowRow> Rows { get; } = [];

    public string Status { get; set; } = string.Empty;

    public bool Ready { get; set; }

    /// <summary>
    /// Kept for structural parity; like the reference field it is computed
    /// during parsing and never read by the aggregation.
    /// </summary>
    public DateTimeOffset? NextResetAt { get; set; }
}

/// <summary>Port of zhipuWindowSpec (zhipu_checker.go:111-115).</summary>
internal sealed record ZhipuWindowSpec(string Name, string Label, TimeSpan Length);

/// <summary>Port of zhipuResponseWindow (zhipu_checker.go:539-542).</summary>
internal sealed record ZhipuResponseWindow(ZhipuWindowSpec Spec, ZhipuLimitEntry Entry);
