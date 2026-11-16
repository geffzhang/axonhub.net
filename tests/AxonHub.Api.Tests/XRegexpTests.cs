using AxonHub.Api.Models;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/pkg/xregexp/matcher_test.go: a wildcard pattern, a plain
/// string comparison, or an anchored regex, with compile failures matching
/// nothing.
/// </summary>
public sealed class XRegexpTests
{
    [Fact]
    public void StarMatchesEverything()
    {
        Assert.True(XRegexp.MatchString("*", "gpt-4o"));
        Assert.True(XRegexp.MatchString("*", string.Empty));
    }

    [Fact]
    public void PlainStringIsAnExactMatch()
    {
        Assert.True(XRegexp.MatchString("gpt-4", "gpt-4"));
        Assert.False(XRegexp.MatchString("gpt-4", "gpt-4o"));
        Assert.False(XRegexp.MatchString("gpt-4", "xgpt-4"));
    }

    [Fact]
    public void PlainStringIsCaseSensitive()
    {
        Assert.False(XRegexp.MatchString("gpt-4", "GPT-4"));
    }

    [Fact]
    public void RegexIsAnchoredAtBothEnds()
    {
        Assert.True(XRegexp.MatchString("gpt-.*", "gpt-4o"));
        Assert.False(XRegexp.MatchString("gpt-.*", "xgpt-4"));

        // The body's own anchors are stripped once, then re-applied.
        Assert.True(XRegexp.MatchString("^gpt-4$", "gpt-4"));
        Assert.False(XRegexp.MatchString("^gpt-4$", "gpt-4o"));
    }

    [Fact]
    public void InlineModifierIsKeptOutsideTheAnchors()
    {
        // (?i) stays a leading modifier instead of becoming the body.
        Assert.True(XRegexp.MatchString("(?i)gpt-4", "GPT-4"));
        Assert.False(XRegexp.MatchString("(?i)gpt-4", "GPT-4o"));
    }

    [Fact]
    public void NonCapturingGroupIsPartOfTheBody()
    {
        Assert.True(XRegexp.MatchString("(?:gpt|claude)-4", "claude-4"));
        Assert.False(XRegexp.MatchString("(?:gpt|claude)-4", "gemini-4"));
    }

    [Fact]
    public void InvalidRegexMatchesNothing()
    {
        // "[": a metacharacter forcing the regex branch, which fails to compile.
        Assert.False(XRegexp.MatchString("[", "anything"));
        Assert.False(XRegexp.MatchString("[", "["));

        // The failure is cached; matching again must stay false.
        Assert.False(XRegexp.MatchString("[", "anything"));
    }

    [Fact]
    public void EmptyPatternMatchesOnlyTheEmptyString()
    {
        Assert.True(XRegexp.MatchString(string.Empty, string.Empty));
        Assert.False(XRegexp.MatchString(string.Empty, "gpt-4"));
    }
}
