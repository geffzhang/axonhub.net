using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Builds the ent channel row produced by createChannel's builder
/// (channel.go:617-631). Every column follows ent's presence rules:
/// <c>SetSettings(nil)</c>/<c>SetManualModels(nil)</c> store JSON <c>null</c>
/// (not the schema default), the module defaults write
/// <c>{"stream":"unlimited"}</c> for policies and <c>[]</c> for tags/endpoints,
/// and <c>orderingWeight</c> is never set.
/// </summary>
public static class ChannelWriteSerialization
{
    /// <summary>objects.ChannelPolicies zero value's JSON form (ent module default).</summary>
    public const string DefaultPoliciesJson = """{"stream":"unlimited"}""";

    public static ChannelRecord ToRecord(CreateChannelCommand command, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(command);

        return new ChannelRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = 0,
            Type = command.Type,
            BaseUrl = command.BaseUrl,
            Name = command.Name,
            Status = "disabled",
            Credentials = GoJson.MarshalToString(command.Credentials),
            DisabledApiKeys = "[]",
            SupportedModels = GoJson.MarshalToString(command.SupportedModels),

            // SetManualModels(nil) marks the column present with a nil value:
            // the stored JSON is null, suppressing the default [].
            ManualModels = command.ManualModels is null
                ? "null"
                : GoJson.MarshalToString(command.ManualModels),
            AutoSyncSupportedModels = command.AutoSyncSupportedModels ?? false,
            AutoSyncModelPattern = command.AutoSyncModelPattern ?? string.Empty,
            Tags = command.Tags is null ? "[]" : GoJson.MarshalToString(command.Tags),
            DefaultTestModel = command.DefaultTestModel,

            // SetPolicies only runs for a non-nil pointer; otherwise the ent
            // default applies.
            Policies = command.Policies is null
                ? DefaultPoliciesJson
                : GoJson.MarshalToString(command.Policies),

            // SetSettings(nil) stores JSON null, suppressing the schema
            // default {"modelMappings":[]}.
            Settings = command.Settings is null ? "null" : GoJson.MarshalToString(command.Settings),
            OrderingWeight = 0,
            Remark = command.Remark,
            Endpoints = command.Endpoints is null
                ? "[]"
                : GoJson.MarshalToString(command.Endpoints),
        };
    }
}
