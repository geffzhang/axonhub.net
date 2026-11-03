using System.Text.Json;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Read side of the reference's SystemService.ChannelTestPrompts
/// (internal/server/biz/system.go:1340-1352).
/// </summary>
public interface IChannelTestPromptsProvider
{
    /// <summary>
    /// The effective global prompts for channel tests. The reference runs the
    /// read under a system bypass; the port's channel tester has direct store
    /// access, so the bypass has no additional effect.
    /// </summary>
    Task<ChannelTestPrompts> GetChannelTestPromptsAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads and normalizes the <c>system_channel_settings</c> system setting
/// (internal/server/biz/system.go:1317-1338). A missing row, a lookup failure
/// or malformed JSON falls back to the defaults, mirroring
/// ChannelSettingOrDefault; whitespace-only prompts are replaced by the
/// defaults exactly like normalizeSystemChannelSettings.
/// </summary>
public sealed class SystemChannelSettingsService(ISystemStore systems)
    : IChannelTestPromptsProvider
{
    /// <summary>biz.SystemKeyChannelSettings.</summary>
    public const string ChannelSettingsKey = "system_channel_settings";

    public async Task<ChannelTestPrompts> GetChannelTestPromptsAsync(
        CancellationToken cancellationToken = default)
    {
        SystemRecord? row;
        try
        {
            row = await systems.GetByKeyAsync(ChannelSettingsKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ChannelSettingOrDefault logs every lookup error and falls back.
            return ChannelTestPrompts.Default;
        }

        if (row is null)
        {
            // ent.IsNotFound returns the default setting.
            return ChannelTestPrompts.Default;
        }

        return ChannelTestPromptsCodec.Deserialize(row.Value);
    }
}

/// <summary>
/// Port of the ChannelSetting JSON read in internal/server/biz/system.go:
/// Go's encoding/json matches struct tags case-insensitively, treats a JSON
/// <c>null</c> as the zero struct and rejects every other non-object document.
/// </summary>
public static class ChannelTestPromptsCodec
{
    public static ChannelTestPrompts Deserialize(string json)
    {
        string systemPrompt;
        string userPrompt;

        try
        {
            using var document = JsonDocument.Parse(json);
            switch (document.RootElement.ValueKind)
            {
                case JsonValueKind.Null:
                    systemPrompt = string.Empty;
                    userPrompt = string.Empty;
                    break;
                case JsonValueKind.Object:
                    systemPrompt = ReadString(document.RootElement, "test_system_prompt");
                    userPrompt = ReadString(document.RootElement, "test_user_prompt");
                    break;
                default:
                    // encoding/json rejects arrays and scalars for a struct.
                    return ChannelTestPrompts.Default;
            }
        }
        catch (JsonException)
        {
            return ChannelTestPrompts.Default;
        }

        // normalizeSystemChannelSettings replaces whitespace-only prompts.
        if (GoStrings.TrimSpace(systemPrompt).Length == 0)
        {
            systemPrompt = ChannelTestPrompts.DefaultSystemPrompt;
        }

        if (GoStrings.TrimSpace(userPrompt).Length == 0)
        {
            userPrompt = ChannelTestPrompts.DefaultUserPrompt;
        }

        return new ChannelTestPrompts(systemPrompt, userPrompt);
    }

    /// <summary>
    /// Last matching member wins, like encoding/json; a JSON null leaves the
    /// Go string field zeroed, and any other non-string value fails the whole
    /// unmarshal.
    /// </summary>
    private static string ReadString(JsonElement root, string name)
    {
        var value = string.Empty;

        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString()!,
                JsonValueKind.Null => string.Empty,
                _ => throw new JsonException(
                    $"json: cannot unmarshal {property.Value.ValueKind} into Go struct field "
                    + $"SystemChannelSettings.{name} of type string"),
            };
        }

        return value;
    }
}

/// <summary>
/// The effective (system, user) prompt pair for channel connectivity tests.
/// </summary>
public sealed record ChannelTestPrompts(string SystemPrompt, string UserPrompt)
{
    /// <summary>biz.defaultChannelTestSystemPrompt.</summary>
    public const string DefaultSystemPrompt = "You are a helpful assistant.";

    /// <summary>biz.defaultChannelTestUserPrompt.</summary>
    public const string DefaultUserPrompt =
        "Hello world, I'm AxonHub.\nPlease tell me who you are?";

    /// <summary>The reference's defaultChannelSetting prompts.</summary>
    public static ChannelTestPrompts Default { get; } =
        new(DefaultSystemPrompt, DefaultUserPrompt);
}
