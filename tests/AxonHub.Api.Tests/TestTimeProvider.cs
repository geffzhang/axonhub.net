namespace AxonHub.Api.Tests;

/// <summary>
/// A clock the test moves by hand, so natural-minute windows and 429 cooldowns
/// never depend on wall-clock timing.
/// </summary>
internal sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}
