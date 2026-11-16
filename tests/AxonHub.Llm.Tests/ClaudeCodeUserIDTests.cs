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

    // ------------------------------------------------------------------
    // Generation (reference TestBuildUserID / TestGenerateUserID_*)
    // ------------------------------------------------------------------

    [Fact]
    public void TestBuildUserId()
    {
        var uid = new ClaudeCodeUserID
        {
            DeviceId = "deadbeef",
            AccountUuid = "acc-123",
            SessionId = "sess-456",
        };

        var result = ClaudeCodeUserID.BuildUserId(uid);

        // Go's json.Marshal emits every field (no omitempty) in declaration order.
        Assert.Equal(
            """{"device_id":"deadbeef","account_uuid":"acc-123","session_id":"sess-456"}""",
            result);

        var parsed = ClaudeCodeUserID.ParseUserId(result);
        Assert.NotNull(parsed);
        Assert.Equal(uid.DeviceId, parsed!.DeviceId);
        Assert.Equal(uid.AccountUuid, parsed.AccountUuid);
        Assert.Equal(uid.SessionId, parsed.SessionId);
    }

    [Fact]
    public void TestGenerateUserIdRandom()
    {
        var uid = ClaudeCodeUserID.ParseUserId(
            ClaudeCodeUserID.GenerateUserId(string.Empty, null));

        Assert.NotNull(uid);
        Assert.Matches("^[0-9a-f]{64}$", uid!.DeviceId);
        Assert.Equal(string.Empty, uid.AccountUuid);
        Assert.True(Guid.TryParse(uid.SessionId, out _));
    }

    [Fact]
    public void TestGenerateUserIdStable()
    {
        // The channel identity makes both derived values deterministic: the
        // device id is SHA-256 hex, the account uuid is a UUID v5 in the URL
        // namespace.
        var raw1 = ClaudeCodeUserID.GenerateUserId("42", null);
        var raw2 = ClaudeCodeUserID.GenerateUserId("42", null);

        var uid1 = ClaudeCodeUserID.ParseUserId(raw1);
        var uid2 = ClaudeCodeUserID.ParseUserId(raw2);

        Assert.NotNull(uid1);
        Assert.NotNull(uid2);
        Assert.Equal(
            "73475cb40a568e8da8a045ced110137e159f890ac4da883b6b17dc651b3a8049",
            uid1!.DeviceId);
        Assert.Equal("5c2b23de-4bad-58ee-a4b3-f22f3b9cfd7d", uid1.AccountUuid);
        Assert.Equal(uid1.DeviceId, uid2!.DeviceId);
        Assert.Equal(uid1.AccountUuid, uid2.AccountUuid);
    }

    [Fact]
    public void TestGenerateUserIdDifferentIdentities()
    {
        var uid1 = ClaudeCodeUserID.ParseUserId(ClaudeCodeUserID.GenerateUserId("1", null));
        var uid2 = ClaudeCodeUserID.ParseUserId(ClaudeCodeUserID.GenerateUserId("2", null));

        Assert.NotNull(uid1);
        Assert.NotNull(uid2);
        Assert.NotEqual(uid1!.DeviceId, uid2!.DeviceId);
        Assert.NotEqual(uid1.AccountUuid, uid2.AccountUuid);
    }

    [Fact]
    public void TestGenerateUserIdUsesSharedSessionId()
    {
        // The reference reads shared.GetSessionID(ctx); the port threads the
        // same value on the raw request (C062) and passes it here explicitly.
        var uid = ClaudeCodeUserID.ParseUserId(
            ClaudeCodeUserID.GenerateUserId(string.Empty, "shared-session-id"));

        Assert.NotNull(uid);
        Assert.Equal("shared-session-id", uid!.SessionId);
    }

    [Fact]
    public void TestGenerateUserIdReplacesBlankSessionId()
    {
        var uid = ClaudeCodeUserID.ParseUserId(ClaudeCodeUserID.GenerateUserId("42", "   "));

        Assert.NotNull(uid);
        Assert.True(Guid.TryParse(uid!.SessionId, out _));
    }

    [Fact]
    public void TestGenerateUserIdKeepsSessionWhitespace()
    {
        // Go only trims for the emptiness check; the original value is kept.
        var uid = ClaudeCodeUserID.ParseUserId(
            ClaudeCodeUserID.GenerateUserId("42", " shared "));

        Assert.NotNull(uid);
        Assert.Equal(" shared ", uid!.SessionId);
    }
}
