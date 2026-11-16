namespace AxonHub.Llm.Transformer.Antigravity;

/// <summary>Port of antigravity.endpointKey (llm/transformer/antigravity/health_tracker.go:17-20).</summary>
internal sealed record EndpointKey(string Model, string Endpoint);

/// <summary>
/// Port of antigravity.AntigravityHealthTracker
/// (llm/transformer/antigravity/health_tracker.go:22-30): per-model endpoint
/// health with a simple cooldown to avoid repeatedly hitting failed endpoints.
/// </summary>
public sealed class AntigravityHealthTracker
{
    /// <summary>Port of DefaultCooldownDuration: the fixed cooldown period after an endpoint failure.</summary>
    public static readonly TimeSpan DefaultCooldownDuration = TimeSpan.FromSeconds(60);

    /// <summary>Port of DefaultTTL: how long failure records are kept before considered stale.</summary>
    public static readonly TimeSpan DefaultTTL = TimeSpan.FromMinutes(10);

    private readonly object _sync = new();
    private readonly TimeSpan _cooldownDuration;
    private readonly TimeSpan _ttl;

    private Dictionary<EndpointKey, AntigravityEndpointFailure> _failures = [];

    /// <summary>Port of NewAntigravityHealthTracker: default cooldown and TTL.</summary>
    public AntigravityHealthTracker()
        : this(DefaultCooldownDuration, DefaultTTL)
    {
    }

    /// <summary>Port of NewAntigravityHealthTrackerWithConfig.</summary>
    public AntigravityHealthTracker(TimeSpan cooldown, TimeSpan ttl)
    {
        _cooldownDuration = cooldown;
        _ttl = ttl;
    }

    /// <summary>Port of ShouldSkip: true when the endpoint is in cooldown for this model.</summary>
    public bool ShouldSkip(string model, string endpoint)
    {
        lock (_sync)
        {
            var key = new EndpointKey(model, endpoint);

            if (!_failures.TryGetValue(key, out var failure))
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;

            // Lazy cleanup: remove the entry once it is past TTL.
            if (now - failure.LastFailedAt > _ttl)
            {
                _failures.Remove(key);
                return false;
            }

            // Still in cooldown?
            return now < failure.CooldownUntil;
        }
    }

    /// <summary>Port of RecordFailure: puts the endpoint into cooldown for the configured duration.</summary>
    public void RecordFailure(string model, string endpoint, int statusCode)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            _failures[new EndpointKey(model, endpoint)] = new AntigravityEndpointFailure
            {
                Model = model,
                Endpoint = endpoint,
                LastFailedAt = now,
                StatusCode = statusCode,
                CooldownUntil = now.Add(_cooldownDuration),
            };
        }
    }

    /// <summary>Port of RecordSuccess: clears any existing failure state for the combination.</summary>
    public void RecordSuccess(string model, string endpoint)
    {
        lock (_sync)
        {
            _failures.Remove(new EndpointKey(model, endpoint));
        }
    }

    /// <summary>
    /// Port of GetFailure: returns a copy, or null when no failure is recorded
    /// or the entry has expired.
    /// </summary>
    public AntigravityEndpointFailure? GetFailure(string model, string endpoint)
    {
        lock (_sync)
        {
            var key = new EndpointKey(model, endpoint);

            if (!_failures.TryGetValue(key, out var failure))
            {
                return null;
            }

            if (DateTimeOffset.UtcNow - failure.LastFailedAt > _ttl)
            {
                return null;
            }

            // Return a copy to avoid races with later mutations.
            return new AntigravityEndpointFailure
            {
                Model = failure.Model,
                Endpoint = failure.Endpoint,
                LastFailedAt = failure.LastFailedAt,
                StatusCode = failure.StatusCode,
                CooldownUntil = failure.CooldownUntil,
            };
        }
    }

    /// <summary>Port of Stats: current health tracker state.</summary>
    public AntigravityHealthTrackerStats Stats()
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var stats = new AntigravityHealthTrackerStats
            {
                TotalEntries = _failures.Count,
            };

            foreach (var failure in _failures.Values)
            {
                if (now - failure.LastFailedAt > _ttl)
                {
                    stats.Expired++;
                    continue;
                }

                if (now < failure.CooldownUntil)
                {
                    stats.InCooldown++;
                    stats.CooldownEntries[failure.Model + "|" + failure.Endpoint] = failure.CooldownUntil;
                }
            }

            return stats;
        }
    }

    /// <summary>Port of Clear: removes all entries (primarily for testing).</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _failures = [];
        }
    }
}

/// <summary>Port of antigravity.AntigravityEndpointFailure (health_tracker.go:33-39).</summary>
public sealed class AntigravityEndpointFailure
{
    public string Model { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    public DateTimeOffset LastFailedAt { get; set; }

    public int StatusCode { get; set; }

    public DateTimeOffset CooldownUntil { get; set; }
}

/// <summary>Port of antigravity.AntigravityHealthTrackerStats (health_tracker.go:171-176).</summary>
public sealed class AntigravityHealthTrackerStats
{
    public int TotalEntries { get; set; }

    public int InCooldown { get; set; }

    public int Expired { get; set; }

    /// <summary>"model|endpoint" -> cooldown expiry time.</summary>
    public Dictionary<string, DateTimeOffset> CooldownEntries { get; } = new(StringComparer.Ordinal);
}
