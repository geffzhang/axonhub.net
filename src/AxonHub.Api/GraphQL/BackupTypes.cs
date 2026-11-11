using System.Text.Json;
using AxonHub.Api.Management;
using HotChocolate;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the gqlgen <c>BackupConflictStrategy</c> enum (backup.graphql:3-7).</summary>
[GraphQLName("BackupConflictStrategy")]
public enum BackupConflictStrategyValue
{
    [GraphQLName("skip")]
    Skip,

    [GraphQLName("overwrite")]
    Overwrite,

    [GraphQLName("error")]
    Error,
}

/// <summary>Port of the gqlgen <c>BackupFrequency</c> enum (backup.graphql:44-48).</summary>
[GraphQLName("BackupFrequency")]
public enum BackupFrequencyValue
{
    [GraphQLName("daily")]
    Daily,

    [GraphQLName("weekly")]
    Weekly,

    [GraphQLName("monthly")]
    Monthly,
}

/// <summary>Port of <c>BackupOptionsInput</c> (backup.graphql:9-17).</summary>
public sealed class BackupOptionsInput
{
    [DefaultValue(false)]
    public bool IncludeSystemConfigs { get; set; }

    public bool IncludeChannels { get; set; }

    [DefaultValue(true)]
    public bool IncludeModelPrices { get; set; } = true;

    public bool IncludeModels { get; set; }

    [GraphQLName("includeAPIKeys")]
    public bool IncludeApiKeys { get; set; }

    [DefaultValue(true)]
    public bool IncludeUsageStats { get; set; } = true;

    [DefaultValue(false)]
    public bool IncludeRequestLogs { get; set; }
}

/// <summary>Port of <c>RestoreOptionsInput</c> (backup.graphql:19-30).</summary>
public sealed class RestoreOptionsInput
{
    [DefaultValue(false)]
    public bool IncludeSystemConfigs { get; set; }

    public bool IncludeChannels { get; set; }

    [DefaultValue(true)]
    public bool IncludeModelPrices { get; set; } = true;

    public bool IncludeModels { get; set; }

    [GraphQLName("includeAPIKeys")]
    public bool IncludeApiKeys { get; set; }

    [DefaultValue(true)]
    public bool IncludeUsageStats { get; set; } = true;

    [DefaultValue(false)]
    public bool IncludeRequestLogs { get; set; }

    public BackupConflictStrategyValue ChannelConflictStrategy { get; set; }

    public BackupConflictStrategyValue ModelConflictStrategy { get; set; }

    public BackupConflictStrategyValue ModelPriceConflictStrategy { get; set; }

    /// <summary>SDL spelling is <c>apiKeyConflictStrategy</c>.</summary>
    public BackupConflictStrategyValue ApiKeyConflictStrategy { get; set; }
}

/// <summary>Port of the <c>BackupPayload</c> type (backup.graphql:32-36).</summary>
[GraphQLName("BackupPayload")]
public sealed class BackupPayloadType
{
    public bool Success { get; init; }

    public string? Data { get; init; }

    public string? Message { get; init; }
}

/// <summary>Port of the <c>RestorePayload</c> type (backup.graphql:38-41).</summary>
[GraphQLName("RestorePayload")]
public sealed class RestorePayloadType
{
    public bool Success { get; init; }

    public string? Message { get; init; }
}

/// <summary>Port of the <c>AutoBackupSettings</c> type (backup.graphql:50-64).</summary>
[GraphQLName("AutoBackupSettings")]
public sealed class AutoBackupSettingsType
{
    public bool IncludeSystemConfigs { get; init; }

    public bool Enabled { get; init; }

    public BackupFrequencyValue Frequency { get; init; }

    [GraphQLName("dataStorageID")]
    public int DataStorageId { get; init; }

    public bool IncludeChannels { get; init; }

    public bool IncludeModels { get; init; }

    [GraphQLName("includeAPIKeys")]
    public bool IncludeApiKeys { get; init; }

    public bool IncludeModelPrices { get; init; }

    public bool IncludeUsageStats { get; init; }

    public bool IncludeRequestLogs { get; init; }

    public int RetentionDays { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? LastBackupAt { get; init; }

    public string? LastBackupError { get; init; }
}

/// <summary>Port of <c>UpdateAutoBackupSettingsInput</c> (backup.graphql:66-78).</summary>
public sealed class UpdateAutoBackupSettingsInput
{
    public bool? IncludeSystemConfigs { get; set; }

    public bool? Enabled { get; set; }

    public BackupFrequencyValue? Frequency { get; set; }

    [GraphQLName("dataStorageID")]
    public int? DataStorageId { get; set; }

    public bool? IncludeChannels { get; set; }

    public bool? IncludeModels { get; set; }

    [GraphQLName("includeAPIKeys")]
    public bool? IncludeApiKeys { get; set; }

    public bool? IncludeModelPrices { get; set; }

    public bool? IncludeUsageStats { get; set; }

    public bool? IncludeRequestLogs { get; set; }

    public int? RetentionDays { get; set; }
}

/// <summary>Port of the <c>TriggerBackupPayload</c> type (backup.graphql:80-83).</summary>
[GraphQLName("TriggerBackupPayload")]
public sealed class TriggerBackupPayloadType
{
    public bool Success { get; init; }

    public string? Message { get; init; }
}

/// <summary>Domain ↔ GraphQL mapping for the backup slice.</summary>
public static class BackupMapping
{
    public static BackupOptions ToDomain(BackupOptionsInput input) => new()
    {
        IncludeSystemConfigs = input.IncludeSystemConfigs,
        IncludeChannels = input.IncludeChannels,
        IncludeModelPrices = input.IncludeModelPrices,
        IncludeModels = input.IncludeModels,
        IncludeApiKeys = input.IncludeApiKeys,
        IncludeUsageStats = input.IncludeUsageStats,
        IncludeRequestLogs = input.IncludeRequestLogs,
    };

    public static RestoreOptions ToDomain(RestoreOptionsInput input) => new()
    {
        IncludeSystemConfigs = input.IncludeSystemConfigs,
        IncludeChannels = input.IncludeChannels,
        IncludeModelPrices = input.IncludeModelPrices,
        IncludeModels = input.IncludeModels,
        IncludeApiKeys = input.IncludeApiKeys,
        IncludeUsageStats = input.IncludeUsageStats,
        IncludeRequestLogs = input.IncludeRequestLogs,
        ChannelConflictStrategy = ToDomain(input.ChannelConflictStrategy),
        ModelConflictStrategy = ToDomain(input.ModelConflictStrategy),
        ModelPriceConflictStrategy = ToDomain(input.ModelPriceConflictStrategy),
        ApiKeyConflictStrategy = ToDomain(input.ApiKeyConflictStrategy),
    };

    public static AutoBackupSettings Apply(
        AutoBackupSettings settings,
        UpdateAutoBackupSettingsInput input)
        => settings with
        {
            IncludeSystemConfigs =
                input.IncludeSystemConfigs ?? settings.IncludeSystemConfigs,
            Enabled = input.Enabled ?? settings.Enabled,
            Frequency = input.Frequency is { } frequency
                ? ToDomain(frequency)
                : settings.Frequency,
            DataStorageId = input.DataStorageId ?? settings.DataStorageId,
            IncludeChannels = input.IncludeChannels ?? settings.IncludeChannels,
            IncludeModels = input.IncludeModels ?? settings.IncludeModels,
            IncludeApiKeys = input.IncludeApiKeys ?? settings.IncludeApiKeys,
            IncludeModelPrices = input.IncludeModelPrices ?? settings.IncludeModelPrices,
            IncludeUsageStats = input.IncludeUsageStats ?? settings.IncludeUsageStats,
            IncludeRequestLogs = input.IncludeRequestLogs ?? settings.IncludeRequestLogs,
            RetentionDays = input.RetentionDays ?? settings.RetentionDays,
        };

    public static AutoBackupSettingsType ToGraphQL(AutoBackupSettings settings) => new()
    {
        IncludeSystemConfigs = settings.IncludeSystemConfigs,
        Enabled = settings.Enabled,
        Frequency = ToGraphQL(settings.Frequency),
        DataStorageId = settings.DataStorageId,
        IncludeChannels = settings.IncludeChannels,
        IncludeModels = settings.IncludeModels,
        IncludeApiKeys = settings.IncludeApiKeys,
        IncludeModelPrices = settings.IncludeModelPrices,
        IncludeUsageStats = settings.IncludeUsageStats,
        IncludeRequestLogs = settings.IncludeRequestLogs,
        RetentionDays = settings.RetentionDays,
        LastBackupAt = settings.LastBackupAt,
        LastBackupError = settings.LastBackupError,
    };

    private static string ToDomain(BackupConflictStrategyValue strategy) => strategy switch
    {
        BackupConflictStrategyValue.Skip => BackupConflictStrategy.Skip,
        BackupConflictStrategyValue.Overwrite => BackupConflictStrategy.Overwrite,
        _ => BackupConflictStrategy.Error,
    };

    private static string ToDomain(BackupFrequencyValue frequency) => frequency switch
    {
        BackupFrequencyValue.Daily => BackupFrequencies.Daily,
        BackupFrequencyValue.Weekly => BackupFrequencies.Weekly,
        _ => BackupFrequencies.Monthly,
    };

    /// <remarks>
    /// gqlgen marshals the Go string as-is without checking the enum set
    /// (difference C198); HotChocolate requires a declared value, so an
    /// unknown stored frequency fails this field instead of emitting the raw
    /// string.
    /// </remarks>
    private static BackupFrequencyValue ToGraphQL(string frequency) => frequency switch
    {
        BackupFrequencies.Daily => BackupFrequencyValue.Daily,
        BackupFrequencies.Weekly => BackupFrequencyValue.Weekly,
        BackupFrequencies.Monthly => BackupFrequencyValue.Monthly,
        _ => throw new GraphQLException(
            "Enum \"BackupFrequency\" cannot represent value: "
            + JsonSerializer.Serialize(frequency)),
    };
}
