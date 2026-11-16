using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Settings;

/// <summary>
/// Port of biz.CodexCompatibilitySettings (internal/server/biz/system_codex.go:17-20):
/// whether the Codex compatibility surface is enabled and which channel serves
/// its model catalog. Go keeps <c>ChannelID *int</c>; the port keeps a nullable
/// 64-bit value so a stored row written on a 64-bit Go host round-trips, while
/// the GraphQL boundary remains <c>Int</c>.
/// </summary>
public sealed class CodexCompatibilitySettings
{
    public bool Enabled { get; init; }

    public long? ChannelID { get; init; }
}

/// <summary>
/// Reads and writes the <c>codex_compatibility</c> system setting
/// (internal/server/biz/system_codex.go). The management surface requires
/// read_settings / write_settings through <c>authz.RequireScope</c>; the
/// enabled-state accessor is the reference's <c>authz.RunWithSystemBypass</c>
/// path and carries no scope check.
/// </summary>
public interface ICodexCompatibilitySettingsService
{
    /// <summary>Port of SystemService.CodexCompatibilitySettings.</summary>
    Task<CodexCompatibilitySettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>SystemService.CodexCompatibilitySettings</c> called inside
    /// <c>authz.RunWithSystemBypass</c> (<c>CodexCompatibility.Enabled</c> and
    /// <c>ListModels</c>): the full settings without the <c>read_settings</c>
    /// scope check.
    /// </summary>
    Task<CodexCompatibilitySettings> GetBypassAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.CodexCompatibilityEnabled.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SetCodexCompatibilitySettings.</summary>
    Task UpdateAsync(
        CodexCompatibilitySettings settings,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store-backed implementation. The read path mirrors <c>getSystemValue</c>:
/// a storage failure becomes <c>failed to get system value: %w</c> and the
/// caller adds its operation prefix; a missing row is the zero value.
/// </summary>
public sealed class CodexCompatibilitySettingsService(
    ISystemStore systems,
    IChannelStore channels,
    AuthorizationScope scope)
    : ICodexCompatibilitySettingsService
{
    /// <summary>biz.SystemKeyCodexCompatibility.</summary>
    public const string SystemKey = "codex_compatibility";

    /// <summary>channel.TypeCodex.</summary>
    public const string CodexChannelType = "codex";

    public async Task<CodexCompatibilitySettings> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await scope.RequireScopeAsync(AuthorizationScope.ReadSettings, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<CodexCompatibilitySettings> GetBypassAsync(
        CancellationToken cancellationToken = default)
        // authz.RunWithSystemBypass("codex-compatibility-switch"): the wrapper
        // only skips the scope check; the read itself is the same.
        => ReadAsync(cancellationToken);

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        // authz.RunWithSystemBypass("codex-compatibility-enabled"): the state
        // is server-owned, so no principal scope is consulted.
        var settings = await ReadAsync(
                readPrefix: "read Codex compatibility enabled state: ",
                cancellationToken)
            .ConfigureAwait(false);
        return settings.Enabled;
    }

    public async Task UpdateAsync(
        CodexCompatibilitySettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await scope.RequireScopeAsync(AuthorizationScope.WriteSettings, cancellationToken)
            .ConfigureAwait(false);

        if (settings.Enabled && settings.ChannelID is null)
        {
            throw new CodedException(
                CodedException.ValidationFailed,
                "a Codex channel is required when enabled");
        }

        if (settings.ChannelID is { } channelId)
        {
            await scope.RequireScopeAsync(AuthorizationScope.ReadChannels, cancellationToken)
                .ConfigureAwait(false);

            ChannelRecord? entity;
            try
            {
                entity = await channels.GetByIdAsync(channelId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ent's Channel.Get error, including not-found, maps to the same
                // validation error.
                entity = null;
            }

            if (entity is null)
            {
                throw new CodedException(
                    CodedException.ValidationFailed,
                    "Codex catalog channel is unavailable or inaccessible");
            }

            if (!string.Equals(entity.Type, CodexChannelType, StringComparison.Ordinal))
            {
                throw new CodedException(
                    CodedException.ValidationFailed,
                    "catalog channel must be Codex");
            }
        }

        // json.Marshal cannot fail for this struct, so the reference's
        // "encode Codex compatibility settings" wrapper has no analogue.
        var value = GoJson.MarshalToString(new CodexCompatibilitySettingsStored
        {
            Enabled = settings.Enabled,
            ChannelID = settings.ChannelID,
        });

        try
        {
            await systems.SetAsync(SystemKey, value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // setSystemValue's own prefix is the only wrapper here.
            throw new InvalidOperationException(
                "failed to create system setting: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of SystemService.CodexCompatibilitySettings' read half.</summary>
    private async Task<CodexCompatibilitySettings> ReadAsync(
        CancellationToken cancellationToken)
        => await ReadAsync("read Codex compatibility settings: ", cancellationToken)
            .ConfigureAwait(false);

    private async Task<CodexCompatibilitySettings> ReadAsync(
        string readPrefix,
        CancellationToken cancellationToken)
    {
        SystemRecord? row;
        try
        {
            row = await systems.GetByKeyAsync(SystemKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // getSystemValue: fmt.Errorf("failed to get system value: %w", err);
            // the caller prefixes the operation.
            throw new InvalidOperationException(
                readPrefix + "failed to get system value: " + ex.Message,
                ex);
        }

        if (row is null)
        {
            return new CodexCompatibilitySettings();
        }

        try
        {
            return CodexCompatibilitySettingsCodec.Decode(row.Value);
        }
        catch (JsonException ex)
        {
            // fmt.Errorf("decode Codex compatibility settings: %w", err)
            throw new InvalidOperationException(
                "decode Codex compatibility settings: " + ex.Message,
                ex);
        }
    }
}

/// <summary>
/// JSON shape of the stored <c>codex_compatibility</c> value
/// (biz.CodexCompatibilitySettings). Property order is the Go struct order and
/// matters because the value is written with <see cref="GoJson"/>; <c>channelID</c>
/// has no omitempty, so the null is always emitted.
/// </summary>
public sealed class CodexCompatibilitySettingsStored
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("channelID")]
    public long? ChannelID { get; set; }
}

/// <summary>
/// Port of <c>json.Unmarshal(value, &amp;CodexCompatibilitySettings)</c>: Go's
/// decoder accepts a top-level <c>null</c> as the zero struct, treats a
/// <c>null</c> field as a no-op for the bool and as "clear the pointer" for
/// <c>channelID</c>, matches keys case-insensitively, and lets duplicate keys
/// last-win. <see cref="JsonSerializer"/> cannot reproduce those tolerances,
/// hence the manual walk.
/// </summary>
public static class CodexCompatibilitySettingsCodec
{
    public static CodexCompatibilitySettings Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        using var document = JsonDocument.Parse(value);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new CodexCompatibilitySettings();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal " + KindName(root)
                + " into Go value of type biz.CodexCompatibilitySettings");
        }

        var enabled = false;
        long? channelId = null;

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "enabled", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    // null into a non-pointer field is a no-op.
                    continue;
                }

                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new JsonException(
                        "json: cannot unmarshal " + KindName(property.Value)
                        + " into Go struct field CodexCompatibilitySettings.enabled of type bool");
                }

                enabled = property.Value.GetBoolean();
                continue;
            }

            if (string.Equals(property.Name, "channelID", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    // null into a pointer clears it.
                    channelId = null;
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Number
                    || !long.TryParse(
                        property.Value.GetRawText(),
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    throw new JsonException(
                        "json: cannot unmarshal " + ScalarName(property.Value)
                        + " into Go struct field CodexCompatibilitySettings.channelID of type int");
                }

                channelId = parsed;
            }
        }

        return new CodexCompatibilitySettings
        {
            Enabled = enabled,
            ChannelID = channelId,
        };
    }

    private static string KindName(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "bool",
        _ => element.ValueKind.ToString().ToLowerInvariant(),
    };

    /// <summary>Go quotes the literal for numbers, the kind name otherwise.</summary>
    private static string ScalarName(JsonElement element)
        => element.ValueKind == JsonValueKind.Number
            ? "number " + element.GetRawText()
            : KindName(element);
}
