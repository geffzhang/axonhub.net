using AxonHub.Api.Orchestration;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of the ChannelSetting read in internal/server/biz/system.go:1317-1352.
/// The reference falls back to the default prompts on a missing row, a lookup
/// error or malformed JSON, and normalizes whitespace-only prompts back to the
/// defaults while preserving any other surrounding whitespace.
/// </summary>
public sealed class SystemChannelSettingsServiceTests
{
    private readonly FakeSystemStore _store = new();

    private SystemChannelSettingsService Service => new(_store);

    [Fact]
    public async Task MissingRowUsesTheReferenceDefaults()
    {
        var prompts = await Service.GetChannelTestPromptsAsync();

        Assert.Equal(ChannelTestPrompts.Default, prompts);
        Assert.Equal("You are a helpful assistant.", prompts.SystemPrompt);
        Assert.Equal("Hello world, I'm AxonHub.\nPlease tell me who you are?", prompts.UserPrompt);
    }

    [Fact]
    public async Task ConfiguredPromptsAreReturned()
    {
        _store.Add(
            SystemChannelSettingsService.ChannelSettingsKey,
            """{"test_system_prompt":"be terse","test_user_prompt":"ping"}""");

        var prompts = await Service.GetChannelTestPromptsAsync();

        Assert.Equal("be terse", prompts.SystemPrompt);
        Assert.Equal("ping", prompts.UserPrompt);
    }

    [Fact]
    public async Task KeysAreMatchedCaseInsensitively()
    {
        _store.Add(
            SystemChannelSettingsService.ChannelSettingsKey,
            """{"TEST_SYSTEM_PROMPT":"upper","Test_User_Prompt":"mixed"}""");

        var prompts = await Service.GetChannelTestPromptsAsync();

        Assert.Equal("upper", prompts.SystemPrompt);
        Assert.Equal("mixed", prompts.UserPrompt);
    }

    [Fact]
    public async Task TheLastDuplicateKeyWins()
    {
        _store.Add(
            SystemChannelSettingsService.ChannelSettingsKey,
            """{"test_system_prompt":"first","test_system_prompt":"last"}""");

        var prompts = await Service.GetChannelTestPromptsAsync();

        Assert.Equal("last", prompts.SystemPrompt);
        Assert.Equal(ChannelTestPrompts.DefaultUserPrompt, prompts.UserPrompt);
    }

    [Fact]
    public async Task APartialSettingKeepsTheDefaultForTheMissingPrompt()
    {
        _store.Add(
            SystemChannelSettingsService.ChannelSettingsKey,
            """{"test_system_prompt":"only system"}""");

        var prompts = await Service.GetChannelTestPromptsAsync();

        Assert.Equal("only system", prompts.SystemPrompt);
        Assert.Equal(ChannelTestPrompts.DefaultUserPrompt, prompts.UserPrompt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task WhitespaceOnlyPromptsFallBackToTheDefaults(string value)
    {
        _store.Add(
            SystemChannelSettingsService.ChannelSettingsKey,
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["test_system_prompt"] = value,
                ["test_user_prompt"] = value,
            }));

        var prompts = await Service.GetChannelTestPromptsAsync();

        Assert.Equal(ChannelTestPrompts.DefaultSystemPrompt, prompts.SystemPrompt);
        Assert.Equal(ChannelTestPrompts.DefaultUserPrompt, prompts.UserPrompt);
    }

    [Fact]
    public async Task SurroundingWhitespaceIsPreserved()
    {
        _store.Add(
            SystemChannelSettingsService.ChannelSettingsKey,
            """{"test_system_prompt":"  padded  ","test_user_prompt":"\nuser\n"}""");

        var prompts = await Service.GetChannelTestPromptsAsync();

        Assert.Equal("  padded  ", prompts.SystemPrompt);
        Assert.Equal("\nuser\n", prompts.UserPrompt);
    }

    [Fact]
    public async Task AJsonNullDocumentUsesTheDefaults()
    {
        _store.Add(SystemChannelSettingsService.ChannelSettingsKey, "null");

        var prompts = await Service.GetChannelTestPromptsAsync();

        Assert.Equal(ChannelTestPrompts.Default, prompts);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[1,2]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    public async Task MalformedOrNonObjectDocumentsUseTheDefaults(string value)
    {
        _store.Add(SystemChannelSettingsService.ChannelSettingsKey, value);

        var prompts = await Service.GetChannelTestPromptsAsync();

        Assert.Equal(ChannelTestPrompts.Default, prompts);
    }

    [Fact]
    public async Task ANonStringPromptFailsTheWholeDecode()
    {
        // encoding/json rejects the type mismatch and the caller keeps the
        // default setting, so the valid sibling value is discarded too.
        _store.Add(
            SystemChannelSettingsService.ChannelSettingsKey,
            """{"test_system_prompt":123,"test_user_prompt":"kept?"}""");

        var prompts = await Service.GetChannelTestPromptsAsync();

        Assert.Equal(ChannelTestPrompts.Default, prompts);
    }

    [Fact]
    public async Task ALookupFailureUsesTheDefaults()
    {
        _store.GetFailure = new InvalidOperationException("database is down");

        var prompts = await Service.GetChannelTestPromptsAsync();

        Assert.Equal(ChannelTestPrompts.Default, prompts);
    }

    [Fact]
    public async Task ASoftDeletedRowIsTreatedAsMissing()
    {
        _store.Add(
            SystemChannelSettingsService.ChannelSettingsKey,
            """{"test_system_prompt":"stale"}""",
            row => row.DeletedAt = 1);

        var prompts = await Service.GetChannelTestPromptsAsync();

        Assert.Equal(ChannelTestPrompts.Default, prompts);
    }
}
