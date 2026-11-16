using System.Text.Json;
using AxonHub.Grains.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of objects.ParseOverrideOperations / objects.HeaderEntriesToOverrideOperations
/// and the settings precedence rules in biz.Channel.GetBodyOverrideOperations and
/// GetHeaderOverrideOperations (internal/server/biz/channel_override.go:24-85).
/// </summary>
public sealed class ChannelOverrideSettingsTests
{
    /// <summary>Stores a raw legacy <c>overrideParameters</c> value.</summary>
    private static string LegacyParameters(string raw)
        => "{\"overrideParameters\":" + JsonSerializer.Serialize(raw) + "}";

    /// <summary>Stores a raw legacy <c>overrideHeaders</c> array.</summary>
    private static string LegacyHeaders(string rawEntries)
        => "{\"overrideHeaders\":" + rawEntries + "}";

    [Fact]
    public void NullSettingsYieldNoOperations()
    {
        var settings = ChannelOverrideSettings.Parse(null);

        Assert.Empty(settings.BodyOperations);
        Assert.Empty(settings.HeaderOperations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json")]
    public void NonSettingsJsonYieldsNoOperations(string json)
    {
        var settings = ChannelOverrideSettings.Parse(json);

        Assert.Empty(settings.BodyOperations);
        Assert.Empty(settings.HeaderOperations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData(" { } ")]
    public void EmptyLegacyParametersYieldNoOperations(string raw)
    {
        var settings = ChannelOverrideSettings.Parse(LegacyParameters(raw));

        Assert.Empty(settings.BodyOperations);
    }

    /// <summary>Port of TestParseOverrideOperations / "new format array".</summary>
    [Fact]
    public void NewFormatArrayParsesEveryField()
    {
        var settings = ChannelOverrideSettings.Parse(LegacyParameters(
            """
            [
                {"op": "set", "path": "temp", "value": "0.7"},
                {"op": "delete", "path": "top_p"},
                {"op": "rename", "from": "a", "to": "b", "condition": "{{if .Model}}true{{end}}"}
            ]
            """));

        Assert.Collection(
            settings.BodyOperations,
            op =>
            {
                Assert.Equal(ChannelOverrideOps.Set, op.Op);
                Assert.Equal("temp", op.Path);
                Assert.Equal("0.7", op.Value);
            },
            op =>
            {
                Assert.Equal(ChannelOverrideOps.Delete, op.Op);
                Assert.Equal("top_p", op.Path);
            },
            op =>
            {
                Assert.Equal(ChannelOverrideOps.Rename, op.Op);
                Assert.Equal("a", op.From);
                Assert.Equal("b", op.To);
                Assert.NotEmpty(op.Condition);
            });
    }

    /// <summary>Port of TestParseOverrideOperations / "legacy map format".</summary>
    [Fact]
    public void LegacyMapFormatBecomesSetAndDeleteOperations()
    {
        var settings = ChannelOverrideSettings.Parse(
            LegacyParameters("""{"temperature": 0.7, "remove": "__AXONHUB_CLEAR__"}"""));

        Assert.Equal(2, settings.BodyOperations.Length);
        Assert.Contains(settings.BodyOperations, static op =>
            op.Op == ChannelOverrideOps.Set && op.Path == "temperature");
        Assert.Contains(settings.BodyOperations, static op =>
            op.Op == ChannelOverrideOps.Delete && op.Path == "remove");
    }

    /// <summary>
    /// The legacy map stringifies values with Go's <c>fmt.Sprintf("%v", value)</c>
    /// before storing them in <see cref="ChannelOverrideOperation.Value"/>.
    /// </summary>
    [Theory]
    [InlineData("0.7", "0.7")]
    [InlineData("1", "1")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("null", "<nil>")]
    [InlineData("\"hello\"", "hello")]
    [InlineData("[1,2]", "[1 2]")]
    [InlineData("\"a\"", "a")]
    [InlineData("[\"a\"]", "[a]")]
    [InlineData("{\"a\":1}", "map[a:1]")]
    [InlineData("{\"b\":2,\"a\":1}", "map[a:1 b:2]")]
    public void LegacyMapValuesUseGoPrintfSemantics(string valueJson, string expected)
    {
        var settings = ChannelOverrideSettings.Parse(LegacyParameters("{\"x\":" + valueJson + "}"));

        var op = Assert.Single(settings.BodyOperations);
        Assert.Equal(ChannelOverrideOps.Set, op.Op);
        Assert.Equal("x", op.Path);
        Assert.Equal(expected, op.Value);
    }

    /// <summary>
    /// Port of the "new schema field takes precedence, even when empty" rule
    /// (channel_override.go:30-35).
    /// </summary>
    [Fact]
    public void EmptyBodyOperationArraySuppressesLegacyParameters()
    {
        var settings = ChannelOverrideSettings.Parse(
            "{\"bodyOverrideOperations\":[],\"overrideParameters\":"
            + JsonSerializer.Serialize("""{"temperature": 0.7}""")
            + "}");

        Assert.Empty(settings.BodyOperations);
    }

    [Fact]
    public void BodyOperationArrayWinsOverLegacyParameters()
    {
        var settings = ChannelOverrideSettings.Parse(
            "{\"bodyOverrideOperations\":[{\"op\":\"delete\",\"path\":\"top_p\"}],\"overrideParameters\":"
            + JsonSerializer.Serialize("""{"temperature": 0.7}""")
            + "}");

        var op = Assert.Single(settings.BodyOperations);
        Assert.Equal(ChannelOverrideOps.Delete, op.Op);
    }

    [Fact]
    public void NullBodyOperationArrayFallsBackToLegacyParameters()
    {
        var settings = ChannelOverrideSettings.Parse(
            "{\"bodyOverrideOperations\":null,\"overrideParameters\":"
            + JsonSerializer.Serialize("""{"temperature": 0.7}""")
            + "}");

        var op = Assert.Single(settings.BodyOperations);
        Assert.Equal(ChannelOverrideOps.Set, op.Op);
        Assert.Equal("temperature", op.Path);
    }

    [Fact]
    public void EmptyHeaderOperationArraySuppressesLegacyHeaderEntries()
    {
        var settings = ChannelOverrideSettings.Parse(
            "{\"headerOverrideOperations\":[],\"overrideHeaders\":[{\"key\":\"X-Test\",\"value\":\"1\"}]}");

        Assert.Empty(settings.HeaderOperations);
    }

    [Fact]
    public void HeaderOperationArrayWinsOverLegacyHeaderEntries()
    {
        var settings = ChannelOverrideSettings.Parse(
            "{\"headerOverrideOperations\":[{\"op\":\"delete\",\"path\":\"X-Test\"}],"
            + "\"overrideHeaders\":[{\"key\":\"X-Test\",\"value\":\"1\"}]}");

        var op = Assert.Single(settings.HeaderOperations);
        Assert.Equal(ChannelOverrideOps.Delete, op.Op);
    }

    [Fact]
    public void LegacyHeaderEntriesBecomeSetOperations()
    {
        var settings = ChannelOverrideSettings.Parse(
            LegacyHeaders("""[{"key":"User-Agent","value":"AxonHub/1.0"},{"key":"X-Test","value":"{{.Model}}"}]"""));

        Assert.Collection(
            settings.HeaderOperations,
            op =>
            {
                Assert.Equal(ChannelOverrideOps.Set, op.Op);
                Assert.Equal("User-Agent", op.Path);
                Assert.Equal("AxonHub/1.0", op.Value);
            },
            op =>
            {
                Assert.Equal(ChannelOverrideOps.Set, op.Op);
                Assert.Equal("X-Test", op.Path);
                Assert.Equal("{{.Model}}", op.Value);
            });
    }

    [Fact]
    public void LegacyHeaderClearDirectiveBecomesDeleteOperation()
    {
        var settings = ChannelOverrideSettings.Parse(
            LegacyHeaders("""[{"key":"X-Old","value":"__AXONHUB_CLEAR__"}]"""));

        var op = Assert.Single(settings.HeaderOperations);
        Assert.Equal(ChannelOverrideOps.Delete, op.Op);
        Assert.Equal("X-Old", op.Path);
    }

    /// <summary>
    /// The reference's GetBodyOverrideOperations logs and degrades to no
    /// overrides when the legacy value is not valid JSON; Parse itself reports
    /// the error to its caller, which this build does at load time.
    /// </summary>
    [Fact]
    public void InvalidLegacyJsonDegradesToNoOperations()
    {
        var settings = ChannelOverrideSettings.Parse(LegacyParameters("{invalid}"));

        Assert.Empty(settings.BodyOperations);
    }

    [Fact]
    public void NonStringLegacyParametersAreIgnored()
    {
        var settings = ChannelOverrideSettings.Parse("{\"overrideParameters\":42}");

        Assert.Empty(settings.BodyOperations);
    }

    [Fact]
    public void NonArrayLegacyHeaderEntriesAreIgnored()
    {
        var settings = ChannelOverrideSettings.Parse("{\"overrideHeaders\":\"nope\"}");

        Assert.Empty(settings.HeaderOperations);
    }

    [Fact]
    public void NewOperationsReadIndexMatchAndSplat()
    {
        var settings = ChannelOverrideSettings.Parse(
            """
            {"bodyOverrideOperations":[
                {"op":"array_insert","path":"tools","value":"X","index":-1},
                {"op":"array_remove","path":"tools","match":{"path":"function.name","eq":" web_search "}},
                {"op":"array_append","path":"system","value":"[1,2]","splat":false}
            ]}
            """);

        Assert.Collection(
            settings.BodyOperations,
            op =>
            {
                Assert.Equal(ChannelOverrideOps.ArrayInsert, op.Op);
                Assert.Equal(-1L, op.Index);
                Assert.Null(op.Splat);
                Assert.Null(op.Match);
            },
            op =>
            {
                Assert.Equal(ChannelOverrideOps.ArrayRemove, op.Op);
                Assert.NotNull(op.Match);
                Assert.Equal("function.name", op.Match.Path);
                Assert.Equal(" web_search ", op.Match.Eq);
            },
            op =>
            {
                Assert.Equal(ChannelOverrideOps.ArrayAppend, op.Op);
                Assert.False(op.Splat);
            });
    }

    /// <summary>
    /// encoding/json rejects a non-string into a string field, a non-integer
    /// into *int and a non-bool into *bool; the whole operations array then
    /// degrades to "no overrides" instead of loading half of it.
    /// </summary>
    [Theory]
    [InlineData("""{"op":123,"path":"x"}""")]
    [InlineData("""{"op":"set","path":1}""")]
    [InlineData("""{"op":"array_insert","path":"x","index":1.5}""")]
    [InlineData("""{"op":"array_insert","path":"x","index":"1"}""")]
    [InlineData("""{"op":"array_insert","path":"x","index":1e2}""")]
    [InlineData("""{"op":"array_append","path":"x","splat":"false"}""")]
    [InlineData("""{"op":"array_remove","path":"x","match":{"path":1}}""")]
    [InlineData("""{"op":"array_remove","path":"x","match":"nope"}""")]
    public void DecodingFailureInvalidatesTheWholeOperationArray(string operation)
    {
        var settings = ChannelOverrideSettings.Parse(
            "{\"bodyOverrideOperations\":[" + operation + "]}");

        Assert.Empty(settings.BodyOperations);
    }

    [Fact]
    public void ValidOperationAfterAnInvalidOneIsAlsoDropped()
    {
        var settings = ChannelOverrideSettings.Parse(
            """
            {"bodyOverrideOperations":[
                {"op":"set","path":"a","value":"1"},
                {"op":123,"path":"b"}
            ]}
            """);

        Assert.Empty(settings.BodyOperations);
    }

    [Fact]
    public void OperationFieldsMatchCaseInsensitivelyLikeGoEncodingJson()
    {
        var settings = ChannelOverrideSettings.Parse(
            """
            {"BodyOverrideOperations":[{"Op":"set","Path":"temp","Value":"0.7"}]}
            """);

        var op = Assert.Single(settings.BodyOperations);
        Assert.Equal(ChannelOverrideOps.Set, op.Op);
        Assert.Equal("temp", op.Path);
        Assert.Equal("0.7", op.Value);
    }

    [Fact]
    public void NonObjectOperationItemsInvalidateTheArray()
    {
        var settings = ChannelOverrideSettings.Parse(
            "{\"bodyOverrideOperations\":[\"not-an-object\"]}");

        Assert.Empty(settings.BodyOperations);
    }

    [Fact]
    public void NullMatchIsTreatedAsAbsent()
    {
        var settings = ChannelOverrideSettings.Parse(
            """{"bodyOverrideOperations":[{"op":"set","path":"x","match":null,"index":null,"splat":null}]}""");

        var op = Assert.Single(settings.BodyOperations);
        Assert.Null(op.Match);
        Assert.Null(op.Index);
        Assert.Null(op.Splat);
    }
}
