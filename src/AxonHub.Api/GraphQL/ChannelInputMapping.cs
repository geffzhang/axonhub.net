using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Maps the GraphQL <c>CreateChannelInput</c> to the write command
/// (ent.graphql:1616-1644). Every nullable-vs-empty choice mirrors the gqlgen
/// unmarshaler: nullable strings/bools become Go zero values, lists keep nil
/// distinct from an empty slice, while <c>omitempty</c> list fields collapse an
/// empty slice to nil because both leave it out of the stored JSON.
/// </summary>
public static class ChannelInputMapping
{
    public static CreateChannelCommand ToCommand(CreateChannelInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new CreateChannelCommand
        {
            Type = ChannelMapping.ToWire(input.Type),
            BaseUrl = input.BaseUrl,
            Name = input.Name,
            Credentials = ToCredentials(input.Credentials),
            SupportedModels = input.SupportedModels.ToList(),
            ManualModels = input.ManualModels?.ToList(),
            AutoSyncSupportedModels = input.AutoSyncSupportedModels,
            AutoSyncModelPattern = input.AutoSyncModelPattern,
            Tags = input.Tags?.ToList(),
            DefaultTestModel = input.DefaultTestModel,
            Policies = ToPolicies(input.Policies),
            Settings = ToSettings(input.Settings),
            Remark = input.Remark,
            Endpoints = input.Endpoints?.Select(ToEndpoint).ToList(),
        };
    }

    /// <summary>
    /// Maps the GraphQL <c>UpdateChannelInput</c> to the write command
    /// (ent.graphql:6185-6237). Unlike create, every scalar stays nullable so
    /// null keeps meaning "leave untouched"; an explicit empty list remains an
    /// empty list, matching gqlgen's nil-vs-empty slice handling.
    /// </summary>
    public static UpdateChannelCommand ToCommand(UpdateChannelInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new UpdateChannelCommand
        {
            Type = input.Type is null ? null : ChannelMapping.ToWire(input.Type.Value),
            BaseUrl = input.BaseUrl,
            Name = input.Name,
            Credentials = input.Credentials is null ? null : ToCredentials(input.Credentials),
            SupportedModels = input.SupportedModels?.ToList(),
            ManualModels = input.ManualModels?.ToList(),
            AutoSyncSupportedModels = input.AutoSyncSupportedModels,
            AutoSyncModelPattern = input.AutoSyncModelPattern,
            ClearAutoSyncModelPattern = input.ClearAutoSyncModelPattern is true,
            Tags = input.Tags?.ToList(),
            DefaultTestModel = input.DefaultTestModel,
            Policies = ToPolicies(input.Policies),
            Settings = ToSettings(input.Settings),
            OrderingWeight = input.OrderingWeight,
            Remark = input.Remark,
            ClearRemark = input.ClearRemark is true,
            Endpoints = input.Endpoints?.Select(ToEndpoint).ToList(),
            ClearErrorMessage = input.ClearErrorMessage is true,
        };
    }

    /// <summary>
    /// Maps the GraphQL bulk ordering input to the store items
    /// (channel_bulk.go:18-21). gqlgen decodes the IDs before the resolver runs,
    /// so this is the reference's nil-vs-empty boundary for <c>channels</c>.
    /// </summary>
    public static IReadOnlyList<ChannelOrderingItem> ToCommand(BulkUpdateChannelOrderingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return input.Channels
            .Select(static item => new ChannelOrderingItem(
                GidValue.Parse(item.Id).Id,
                item.OrderingWeight))
            .ToList();
    }

    /// <summary>
    /// Maps the GraphQL bulk auto-disable input to the service command
    /// (channel_bulk.go:235-240). The action keeps its wire spelling so the
    /// service switch matches the reference's enum values.
    /// </summary>
    public static BulkUpdateAutoDisableCommand ToCommand(
        BulkUpdateChannelAutoDisableInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new BulkUpdateAutoDisableCommand
        {
            ChannelIds = input.ChannelIDs
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            Action = input.Action switch
            {
                BulkAutoDisableActionValue.WriteRules => "write_rules",
                BulkAutoDisableActionValue.Inherit => "inherit",
                _ => "off",
            },
            Rules = input.Rules?.Select(ToAutoDisableRule).ToList(),
        };
    }

    private static ChannelCredentialsDraft ToCredentials(ChannelCredentialsInput input)
        => new()
        {
            ApiKey = OmitEmpty(input.ApiKey),
            OAuth = ToOAuth(input.OAuth),
            ApiKeys = NonEmpty(input.ApiKeys),
            VertexApiKeys = NonEmpty(input.VertexApiKeys?.Select(static key => new VertexApiKeyDraft
            {
                ProjectId = key.ProjectID,
                ApiKey = key.ApiKey,
                Region = key.Region,
            }).ToList()),
            ManagementApiKey = OmitEmpty(input.ManagementApiKey),
            Gcp = input.Gcp is null
                ? null
                : new GcpCredentialDraft
                {
                    Region = input.Gcp.Region,
                    ProjectId = input.Gcp.ProjectID,
                    JsonData = input.Gcp.JsonData,
                },
        };

    private static OAuthCredentialsDraft? ToOAuth(OAuthCredentialsInput? input)
        => input is null
            ? null
            : new OAuthCredentialsDraft
            {
                ClientId = OmitEmpty(input.ClientID),
                AccessToken = input.AccessToken ?? string.Empty,
                RefreshToken = input.RefreshToken ?? string.Empty,
                ExpiresAt = input.ExpiresAt is null
                    ? "0001-01-01T00:00:00Z"
                    : GoTimeType.Format(input.ExpiresAt.Value),
                TokenType = OmitEmpty(input.TokenType),
                Scopes = NonEmpty(input.Scopes),
            };

    private static ChannelPoliciesDraft? ToPolicies(ChannelPoliciesInput? input)
        => input is null
            ? null
            : new ChannelPoliciesDraft
            {
                Stream = input.Stream is null
                    ? null
                    : input.Stream switch
                    {
                        CapabilityPolicyValue.Require => "require",
                        CapabilityPolicyValue.Forbid => "forbid",
                        _ => "unlimited",
                    },
                ApiKeyAutoDisableMode = input.ApiKeyAutoDisableMode switch
                {
                    ApiKeyAutoDisableModeValue.Inherit => "inherit",
                    ApiKeyAutoDisableModeValue.Custom => "custom",
                    ApiKeyAutoDisableModeValue.Off => "off",
                    _ => null,
                },
                ApiKeyAutoDisableRules = NonEmpty(
                    input.ApiKeyAutoDisableRules?.Select(ToAutoDisableRule).ToList()),
            };

    private static ApiKeyAutoDisableRuleDraft ToAutoDisableRule(ApiKeyAutoDisableRuleInput input)
        => new()
        {
            StatusCodes = NonEmpty(input.StatusCodes),
            KeywordPatterns = NonEmpty(input.KeywordPatterns),
            Times = input.Times,
            Action = input.Action switch
            {
                ApiKeyAutoDisableActionValue.TemporaryDisable
                    => RetryPolicy.AutoDisableActionTemporary,
                ApiKeyAutoDisableActionValue.DisableUntilCron
                    => RetryPolicy.AutoDisableActionUntilCron,
                ApiKeyAutoDisableActionValue.PermanentDisableDelete
                    => RetryPolicy.AutoDisableActionPermanentDelete,
                _ => RetryPolicy.AutoDisableActionPermanent,
            },
            DisableDurationMinutes = input.DisableDurationMinutes,
            DisableUntilCron = OmitEmpty(input.DisableUntilCron),
            DisableUntilTimezone = OmitEmpty(input.DisableUntilTimezone),
        };

    private static ChannelSettingsDraft? ToSettings(ChannelSettingsInput? input)
    {
        if (input is null)
        {
            return null;
        }

        return new ChannelSettingsDraft
        {
            ExtraModelPrefix = input.ExtraModelPrefix ?? string.Empty,
            AutoTrimedModelPrefixes = input.AutoTrimedModelPrefixes?.ToList(),
            ModelMappings = input.ModelMappings?.Select(static mapping => new ModelMappingDraft
            {
                From = mapping.From,
                To = mapping.To,
            }).ToList(),
            HideOriginalModels = input.HideOriginalModels ?? false,
            HideMappedModels = input.HideMappedModels ?? false,
            LowercaseModelId = input.LowercaseModelId ?? false,

            // The frozen input has no overrideParameters field; gqlgen leaves the
            // deprecated string at its zero value.
            OverrideParameters = string.Empty,
            BodyOverrideOperations = NonEmpty(
                input.BodyOverrideOperations?.Select(ToOperation).ToList()),
            OverrideHeaders = null,
            HeaderOverrideOperations = NonEmpty(
                input.HeaderOverrideOperations?.Select(ToOperation).ToList()),
            Proxy = ToProxy(input.Proxy),
            TransformOptions = ToTransformOptions(input.TransformOptions),
            PassThroughUserAgent = input.PassThroughUserAgent,
            PassThroughBody = input.PassThroughBody,
            RateLimit = ToRateLimit(input.RateLimit),
            RetryableStatusCodes = NonEmpty(input.RetryableStatusCodes),
            RetryableErrorPatterns = NonEmpty(
                input.RetryableErrorPatterns?.Select(static pattern => new RetryableErrorPatternDraft
                {
                    Pattern = pattern.Pattern,
                    Regex = pattern.Regex is true ? true : null,
                }).ToList()),
            MaxSingleChannelRetries = input.MaxSingleChannelRetries,
            ResponseTimeout = ToResponseTimeout(input.ResponseTimeout),
            ModelProtocols = NonEmpty(input.ModelProtocols?.Select(static protocol
                => new ModelProtocolDraft
                {
                    Model = protocol.Model,
                    ApiFormats = protocol.ApiFormats.ToList(),
                    Enabled = protocol.Enabled,
                }).ToList()),
            ApiKeyStrategy = OmitEmpty(input.ApiKeyStrategy),
            ApiKeyRoundRobinSwitchAfter = input.ApiKeyRoundRobinSwitchAfter,
            ProviderQuota = input.ProviderQuota is null
                ? null
                : new ChannelProviderQuotaSettingsDraft
                {
                    CommandCode = input.ProviderQuota.CommandCode is null
                        ? null
                        : new CommandCodeQuotaSettingsDraft
                        {
                            AuthCookie = OmitEmpty(input.ProviderQuota.CommandCode.AuthCookie),
                        },
                    Ollama = input.ProviderQuota.Ollama is null
                        ? null
                        : new OllamaQuotaSettingsDraft
                        {
                            AuthCookie = OmitEmpty(input.ProviderQuota.Ollama.AuthCookie),
                        },
                },
            QuotaRoutingMode = input.QuotaRoutingMode switch
            {
                null or ChannelQuotaRoutingModeValue.Inherit => null,
                ChannelQuotaRoutingModeValue.IgnoreQuota => "ignore_quota",
                ChannelQuotaRoutingModeValue.RemoveOnExhausted => "remove_on_exhausted",
                _ => "backpressure",
            },
        };
    }

    private static OverrideOperationDraft ToOperation(OverrideOperationInput input)
        => new()
        {
            Op = input.Op,
            Path = OmitEmpty(input.Path),
            From = OmitEmpty(input.From),
            To = OmitEmpty(input.To),
            Value = OmitEmpty(input.Value),
            Condition = OmitEmpty(input.Condition),
            Match = input.Match is null
                ? null
                : new OverrideMatchDraft { Path = input.Match.Path, Eq = input.Match.Eq },
            Index = input.Index,
            Splat = input.Splat,
        };

    private static ProxyConfigDraft? ToProxy(ProxyConfigInput? input)
        => input is null
            ? null
            : new ProxyConfigDraft
            {
                Type = input.Type switch
                {
                    ProxyTypeValue.Environment => "environment",
                    ProxyTypeValue.Url => "url",
                    _ => "disabled",
                },
                Url = OmitEmpty(input.Url),
                Username = OmitEmpty(input.Username),
                Password = OmitEmpty(input.Password),
                DisableConnectionReuse = input.DisableConnectionReuse is true ? true : null,
            };

    private static TransformOptionsDraft ToTransformOptions(TransformOptionsInput? input)
        => input is null
            ? new TransformOptionsDraft()
            : new TransformOptionsDraft
            {
                ForceArrayInstructions = input.ForceArrayInstructions ?? false,
                ForceArrayInputs = input.ForceArrayInputs ?? false,
                ReplaceDeveloperRoleWithSystem = input.ReplaceDeveloperRoleWithSystem ?? false,
                ReasoningEffortMapping = NonEmpty(
                    input.ReasoningEffortMapping?.Select(static mapping
                        => new ReasoningEffortMappingDraft
                        {
                            From = mapping.From,
                            To = mapping.To,
                        }).ToList()),
            };

    private static ChannelRateLimitDraft? ToRateLimit(ChannelRateLimitInput? input)
        => input is null
            ? null
            : new ChannelRateLimitDraft
            {
                Rpm = input.Rpm,
                Tpm = input.Tpm,
                MaxConcurrent = input.MaxConcurrent,
                QueueSize = input.QueueSize,
                QueueTimeoutMs = input.QueueTimeoutMs,
            };

    private static ChannelResponseTimeoutDraft? ToResponseTimeout(
        ChannelResponseTimeoutInput? input)
        => input is null
            ? null
            : new ChannelResponseTimeoutDraft
            {
                Mode = input.Mode == ChannelResponseTimeoutModeValue.Custom ? "custom" : null,
                StreamFirstEventTimeoutSeconds = input.StreamFirstEventTimeoutSeconds,
                NonStreamResponseTimeoutSeconds = input.NonStreamResponseTimeoutSeconds,
            };

    private static ChannelEndpointDraft ToEndpoint(ChannelEndpointInput input)
        => new()
        {
            ApiFormat = input.ApiFormat,
            Path = OmitEmpty(input.Path),
            BaseUrl = OmitEmpty(input.BaseUrl),
            Transport = OmitEmpty(input.Transport),
        };

    /// <summary>Go <c>omitempty</c> string: an empty value is left out.</summary>
    private static string? OmitEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Go <c>omitempty</c> slice: an empty slice marshals like nil.</summary>
    private static List<T>? NonEmpty<T>(IReadOnlyList<T>? values)
        => values is { Count: > 0 } ? values.ToList() : null;
}
