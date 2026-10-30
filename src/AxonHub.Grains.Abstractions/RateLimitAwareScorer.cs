using System.Globalization;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Port of <c>orchestrator.RateLimitAwareStrategy</c>
/// (internal/server/orchestrator/lb_strategy_rate_limit.go): adjusts a channel's
/// load-balancing score from its RPM/TPM budgets and its per-channel concurrency
/// limiter. The two sub-scores are combined with <c>min</c>; a channel at any
/// hard limit scores <see cref="ExhaustedScore"/> so the balancer ranks it last
/// while still keeping it available as a fallback.
/// </summary>
public sealed class RateLimitAwareScorer(ChannelRequestTracker requestTracker, ChannelLimiterManager? limiterManager = null)
{
    /// <summary>Full score when no limit applies.</summary>
    public const double MaxScore = 100.0;

    /// <summary>Penalty for a hard limit; dominates every positive strategy sum.</summary>
    public const double ExhaustedScore = -10000.0;

    /// <summary>Cap on the score of a channel that has spilled into its queue.</summary>
    public const double QueueingScoreCeiling = 0.3;

    public string Name => "RateLimitAware";

    /// <summary>Production-path score with no diagnostic bookkeeping.</summary>
    public double Score(long channelId, ChannelRateLimitSettings? rateLimit)
        => ScoreCore(channelId, rateLimit, null).Score;

    /// <summary>
    /// Score plus the reference's <c>StrategyScore.Details</c> map (diagnostic
    /// path; same arithmetic as <see cref="Score"/>).
    /// </summary>
    public RateLimitScore ScoreWithDebug(long channelId, ChannelRateLimitSettings? rateLimit)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["channel_id"] = channelId,
        };

        var (score, exhaustedBy) = ScoreCore(channelId, rateLimit, details);

        if (exhaustedBy is not null)
        {
            details["exhausted"] = true;
            details["exhausted_by"] = exhaustedBy;
        }

        details["score"] = score;

        return new RateLimitScore(Name, score, details);
    }

    /// <summary>
    /// Unified scorer. <paramref name="details"/> null means no diagnostics.
    /// Returns the final score and, when exhausted, the dimension that exhausted
    /// (<c>cooldown</c>, <c>rpm_or_tpm</c>, <c>concurrency</c>).
    /// </summary>
    private (double Score, string? ExhaustedBy) ScoreCore(
        long channelId,
        ChannelRateLimitSettings? rateLimit,
        Dictionary<string, object?>? details)
    {
        if (requestTracker.TryGetCooldownUntil(channelId, out var until))
        {
            if (details is not null)
            {
                details["reason"] = "channel_in_cooldown";
                details["cooldown_until"] = FormatRfc3339(until);
            }

            return (ExhaustedScore, "cooldown");
        }

        var (rpmTpmScore, rpmTpmExhausted) = ScoreRpmTpm(channelId, rateLimit, details);
        if (rpmTpmExhausted)
        {
            return (ExhaustedScore, "rpm_or_tpm");
        }

        var (concurrencyScore, concurrencyExhausted) = ScoreConcurrency(channelId, rateLimit, details);
        if (concurrencyExhausted)
        {
            return (ExhaustedScore, "concurrency");
        }

        if (details is not null)
        {
            details["score_rpm_tpm"] = rpmTpmScore;
            details["score_concurrency"] = concurrencyScore;
        }

        return (Math.Min(rpmTpmScore, concurrencyScore), null);
    }

    /// <summary>RPM/TPM sub-score; exhausted when either budget is spent.</summary>
    private (double Score, bool Exhausted) ScoreRpmTpm(
        long channelId,
        ChannelRateLimitSettings? rateLimit,
        Dictionary<string, object?>? details)
    {
        if (rateLimit is null)
        {
            if (details is not null)
            {
                details["rpm_tpm_reason"] = "no_rpm_tpm_configured";
            }

            return (MaxScore, false);
        }

        var maxRatio = 0.0;

        if (rateLimit.Rpm is { } rpmLimit && rpmLimit > 0)
        {
            var rpm = requestTracker.GetRequestCount(channelId);

            if (details is not null)
            {
                details["rpm_limit"] = rpmLimit;
                details["rpm_current"] = rpm;
            }

            if (rpm >= rpmLimit)
            {
                if (details is not null)
                {
                    details["rpm_exhausted"] = true;
                }

                return (0, true);
            }

            maxRatio = Math.Max(maxRatio, (double)rpm / rpmLimit);
        }

        if (rateLimit.Tpm is { } tpmLimit && tpmLimit > 0)
        {
            var tpm = requestTracker.GetTokenCount(channelId);

            if (details is not null)
            {
                details["tpm_limit"] = tpmLimit;
                details["tpm_current"] = tpm;
            }

            if (tpm >= tpmLimit)
            {
                if (details is not null)
                {
                    details["tpm_exhausted"] = true;
                }

                return (0, true);
            }

            maxRatio = Math.Max(maxRatio, (double)tpm / tpmLimit);
        }

        if (details is not null)
        {
            details["rpm_tpm_max_ratio"] = maxRatio;
        }

        return (ScaleScore(MaxScore, 1 - maxRatio), false);
    }

    /// <summary>
    /// Concurrency sub-score from the limiter's live in-flight/queued counts.
    /// Below capacity the score spans <c>[30, 100]</c>; at capacity with a bounded
    /// queue it is capped at 30 and decays with queue depth, and is exhausted
    /// only when the queue is full too; with an unbounded queue it decays toward
    /// zero but is never exhausted.
    /// </summary>
    private (double Score, bool Exhausted) ScoreConcurrency(
        long channelId,
        ChannelRateLimitSettings? rateLimit,
        Dictionary<string, object?>? details)
    {
        if (limiterManager is null)
        {
            if (details is not null)
            {
                details["concurrency_reason"] = "no_limiter_manager";
            }

            return (MaxScore, false);
        }

        if (!limiterManager.TryGetStats(channelId, out var inFlight, out var waiting))
        {
            if (details is not null)
            {
                details["concurrency_reason"] = "channel_has_no_limiter";
            }

            return (MaxScore, false);
        }

        // A stale entry can briefly outlive a "limit disabled" config change;
        // treat it as unlimited rather than reading a now-absent config.
        if (rateLimit?.MaxConcurrent is not { } maxConcurrent || maxConcurrent <= 0)
        {
            if (details is not null)
            {
                details["concurrency_reason"] = "channel_limiter_disabled";
            }

            return (MaxScore, false);
        }

        var capacity = maxConcurrent > int.MaxValue ? int.MaxValue : (int)maxConcurrent;
        var queueSize = rateLimit.QueueSize is { } configuredQueue && configuredQueue > 0
            ? (configuredQueue > int.MaxValue ? int.MaxValue : (int)configuredQueue)
            : 0;

        if (details is not null)
        {
            details["concurrent_capacity"] = capacity;
            details["concurrent_in_flight"] = inFlight;
            details["concurrent_queue_size"] = queueSize;
            details["concurrent_waiting"] = waiting;
        }

        // The two ranges must stay monotonic so a saturated channel never ranks
        // above one with free capacity:
        //   below capacity: [0.3*max, max]
        //   queueing:       (0, 0.3*max]
        var queueCeiling = MaxScore * QueueingScoreCeiling;

        if (inFlight < capacity)
        {
            var ratio = (double)inFlight / capacity;

            if (details is not null)
            {
                details["concurrency_mode"] = "below_capacity";
                details["concurrency_inflight_ratio"] = ratio;
            }

            return (queueCeiling + ScaleScore(MaxScore - queueCeiling, 1 - ratio), false);
        }

        if (queueSize > 0)
        {
            // Bounded queue: exhausted once capacity + queue are both full.
            if (inFlight + waiting >= capacity + queueSize)
            {
                if (details is not null)
                {
                    details["concurrent_exhausted"] = "queue_full";
                }

                return (0, true);
            }

            var waitingRatio = (double)waiting / queueSize;

            if (details is not null)
            {
                details["concurrency_mode"] = "hard_queueing";
                details["concurrency_waiting_ratio"] = waitingRatio;
            }

            return (ScaleScore(queueCeiling, 1 - waitingRatio), false);
        }

        // Unbounded queue: never exhausted, but down-ranked steeply with depth.
        if (details is not null)
        {
            details["concurrency_mode"] = "unbounded_queueing";
        }

        return (ScaleScore(queueCeiling, 1.0 / (1 + waiting)), false);
    }

    /// <summary>Clamps <c>base * factor</c> to [0, +inf); a negative factor yields 0.</summary>
    private static double ScaleScore(double @base, double factor)
    {
        var score = @base * factor;

        return score < 0 ? 0 : score;
    }

    /// <summary>Go's <c>time.Format(time.RFC3339)</c> for diagnostics.</summary>
    private static string FormatRfc3339(DateTimeOffset value)
    {
        var text = value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (value.Offset == TimeSpan.Zero)
        {
            return text + "Z";
        }

        return text + value.ToString("zzz", CultureInfo.InvariantCulture);
    }
}

/// <summary>Port of the reference's <c>StrategyScore</c> debug payload.</summary>
public sealed record RateLimitScore(string StrategyName, double Score, IReadOnlyDictionary<string, object?> Details);
