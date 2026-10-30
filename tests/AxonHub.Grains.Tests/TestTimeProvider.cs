namespace AxonHub.Grains.Tests;

/// <summary>
/// A clock the test moves by hand, so natural-minute windows and cooldown
/// expiry can be exercised without sleeping or touching internal maps.
/// </summary>
internal sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;

    public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
}
