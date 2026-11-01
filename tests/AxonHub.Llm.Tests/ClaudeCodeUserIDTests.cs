using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;

namespace AxonHub.Llm.Tests;

/// <summary>Port of llm/transformer/anthropic/claudecode/userid_test.go (parsing half).</summary>
public sealed class ClaudeCodeUserIDTests
{
    [Fact]
    public void TestParseUserIdLegacy()
    {
        const string raw =
            "user_aabbccddaabbccddaabbccddaabbccddaabbccddaabbccddaabbccddaabbccdd"
            + "_account__session_7581b58b-1234-5678-9abc-def012345678";

        var uid = ClaudeCodeUserID.ParseUserId(raw);

        Assert.NotNull(uid);
        Assert.Equal(
            "aabbccddaabbccddaabbccddaabbccddaabbccddaabbccddaabbccddaabbccdd",
            uid.DeviceId);
        Assert.Equal(string.Empty, uid.AccountUuid);
        Assert.Equal("7581b58b-1234-5678-9abc-def012345678", uid.SessionId);
    }

    [Fact]
    public void TestParseUserIdV2Json()
    {
        const string raw =
            """{"device_id":"67bad5aabbccdd1122334455667788990011223344556677889900aabbccddee","account_uuid":"acc-uuid-123","session_id":"7581b58b-1234-5678-9abc-def012345678"}""";

        var uid = ClaudeCodeUserID.ParseUserId(raw);

        Assert.NotNull(uid);
        Assert.Equal(
            "67bad5aabbccdd1122334455667788990011223344556677889900aabbccddee",
            uid.DeviceId);
        Assert.Equal("acc-uuid-123", uid.AccountUuid);
        Assert.Equal("7581b58b-1234-5678-9abc-def012345678", uid.SessionId);
    }

    [Fact]
    public void TestParseUserIdV2EmptySessionId()
    {
        Assert.Null(ClaudeCodeUserID.ParseUserId(
            """{"device_id":"abc","account_uuid":"","session_id":""}"""));
    }

    [Fact]
    public void TestParseUserIdInvalidInputs()
    {
        Assert.Null(ClaudeCodeUserID.ParseUserId(string.Empty));
        Assert.Null(ClaudeCodeUserID.ParseUserId("   "));
        Assert.Null(ClaudeCodeUserID.ParseUserId("random-string"));
        Assert.Null(ClaudeCodeUserID.ParseUserId("{invalid json"));
        Assert.Null(ClaudeCodeUserID.ParseUserId(
            "user_tooshort_account__session_bad-uuid"));
    }

    [Fact]
    public void TestParseUserIdTrimsSurroundingWhitespace()
    {
        const string raw =
            "  user_aabbccddaabbccddaabbccddaabbccddaabbccddaabbccddaabbccddaabbccdd"
            + "_account__session_7581b58b-1234-5678-9abc-def012345678  ";

        Assert.Equal(
            "7581b58b-1234-5678-9abc-def012345678",
            ClaudeCodeUserID.ParseUserId(raw)?.SessionId);
    }
}
