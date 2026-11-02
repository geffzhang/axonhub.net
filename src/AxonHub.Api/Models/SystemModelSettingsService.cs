using System.Text.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Models;

/// <summary>Read side of the reference's SystemService.ModelSettingsOrDefault.</summary>
public interface IModelSettingsProvider
{
    /// <summary>
    /// Port of SystemService.ModelSettingsOrDefault: a missing row, a lookup
    /// failure or malformed JSON falls back to <see cref="ModelSettings.Default"/>.
    /// A stored JSON <c>null</c> is a valid document and yields the zero
    /// settings after normalization, exactly like Go's encoding/json.
    /// </summary>
    Task<ModelSettings> GetModelSettingsOrDefaultAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads and normalizes the <c>system_model_settings</c> system setting
/// (internal/server/biz/system.go:1237-1291 and
/// model_settings_inheritance.go:12-32). The JSON codec lives in
/// <see cref="ModelSettingsCodec"/> so the management GraphQL surface shares it.
/// </summary>
public sealed class SystemModelSettingsService(ISystemStore systems) : IModelSettingsProvider
{
    /// <summary>biz.SystemKeyModelSettings.</summary>
    public const string ModelSettingsKey = ModelSettingsCodec.ModelSettingsKey;

    public async Task<ModelSettings> GetModelSettingsOrDefaultAsync(
        CancellationToken cancellationToken = default)
    {
        SystemRecord? row;
        try
        {
            row = await systems.GetByKeyAsync(ModelSettingsKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ModelSettingsOrDefault logs and falls back on every lookup error.
            return ModelSettings.Default;
        }

        if (row is null)
        {
            // ent.IsNotFound: the default document is returned without
            // normalization (the two are identical for the default value).
            return ModelSettings.Default;
        }

        try
        {
            return ModelSettingsCodec.Deserialize(row.Value);
        }
        catch (JsonException)
        {
            // ModelSettings returns the unmarshal error; the OrDefault caller
            // then falls back to the defaults.
            return ModelSettings.Default;
        }
    }
}
