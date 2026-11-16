using AxonHub.Llm.Json;

namespace AxonHub.Llm.Tests;

/// <summary>
/// time.Time renderings used by the models API: <c>MarshalJSON</c> (RFC 3339
/// with nanosecond precision) and <c>Unix()</c>.
/// </summary>
public sealed class GoTimeTests
{
    [Fact]
    public void Rfc3339Nano_WholeSecond_HasNoFraction()
    {
        // Go: time.Date(2024, 4, 5, 19, 34, 58, 0, time.UTC).MarshalJSON()
        // -> "2024-04-05T19:34:58Z" (the Anthropic models list example).
        var value = new DateTime(2024, 4, 5, 19, 34, 58, DateTimeKind.Utc);

        Assert.Equal("2024-04-05T19:34:58Z", GoTime.Rfc3339Nano(value));
    }

    [Fact]
    public void Rfc3339Nano_DropsTrailingZerosOfTheFraction()
    {
        Assert.Equal(
            "2024-04-05T19:34:58.5Z",
            GoTime.Rfc3339Nano(new DateTime(2024, 4, 5, 19, 34, 58, 500, DateTimeKind.Utc)));

        Assert.Equal(
            "2024-04-05T19:34:58.123Z",
            GoTime.Rfc3339Nano(new DateTime(2024, 4, 5, 19, 34, 58, 123, DateTimeKind.Utc)));
    }

    [Fact]
    public void Rfc3339Nano_KeepsSubMillisecondDigits()
    {
        // Go's RFC3339Nano trims trailing zeros only, so a microsecond value
        // keeps its significant leading zeros.
        var value = new DateTime(2024, 4, 5, 19, 34, 58, DateTimeKind.Utc)
            .AddTicks(10); // 1 tick = 100ns -> .000001

        Assert.Equal("2024-04-05T19:34:58.000001Z", GoTime.Rfc3339Nano(value));
    }

    [Fact]
    public void Rfc3339Nano_ZeroValueMatchesGo()
    {
        Assert.Equal("0001-01-01T00:00:00Z", GoTime.Rfc3339Nano(default));
    }

    [Fact]
    public void Rfc3339Nano_UnspecifiedKindIsTreatedAsUtc()
    {
        var value = new DateTime(2024, 4, 5, 19, 34, 58, DateTimeKind.Unspecified);

        Assert.Equal("2024-04-05T19:34:58Z", GoTime.Rfc3339Nano(value));
    }

    [Fact]
    public void UnixSeconds_MatchesGoUnix()
    {
        Assert.Equal(
            1712345698L,
            GoTime.UnixSeconds(new DateTime(2024, 4, 5, 19, 34, 58, DateTimeKind.Utc)));

        // Go's zero time.Time.Unix() is -62135596800.
        Assert.Equal(-62135596800L, GoTime.UnixSeconds(default));
    }

    [Fact]
    public void UnixSeconds_TruncatesTheFraction()
    {
        var value = new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc).AddTicks(9_999_999);

        Assert.Equal(1L, GoTime.UnixSeconds(value));
    }
}
