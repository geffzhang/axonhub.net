using System.Text.Json;
using System.Text.Json.Nodes;
using AxonHub.Api.Models;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the system-model-settings codec: typed association decoding, Go
/// pointer/slice semantics (null elements survive), developer normalization,
/// validation messages and the Go JSON field order.
/// </summary>
public sealed class ModelSettingsCodecTests
{
    [Fact]
    public void DefaultSettingsSerializeInGoFieldOrder()
    {
        var json = ModelSettingsCodec.Serialize(ModelSettings.Default);

        Assert.Equal(
            """
            {"fallback_to_channels_on_model_not_found":true,"query_all_channel_models":true,"default_model_api_include_all":false,"auto_reasoning_effort":false,"model_blacklist_regex":"","hide_unroutable_models_in_list":false,"developer_settings":[]}
            """,
            json);
    }

    [Fact]
    public void AssociationsDecodeIntoTypedMembers()
    {
        var settings = ModelSettingsCodec.Deserialize(
            """
            {
              "developer_settings": [{
                "developer": "  openai  ",
                "associations": [{
                  "type": "channel_model",
                  "channelModel": {"channelId": 7, "modelId": "ignored"},
                  "when": {
                    "enabled": true,
                    "condition": {
                      "type": "group",
                      "logic": "and",
                      "conditions": [
                        {"type":"condition","field":"prompt_tokens","operator":"gte","value":100},
                        {"type":"condition","field":"stream","operator":"eq","value":false}
                      ]
                    }
                  }
                }]
              }]
            }
            """);

        var developer = Assert.Single(settings.DeveloperSettings);
        Assert.NotNull(developer);
        Assert.Equal("openai", developer.Developer);

        var association = Assert.Single(developer.Associations);
        Assert.NotNull(association);
        Assert.Equal(ModelAssociation.TypeChannelModel, association.Type);

        // normalizeDeveloperAssociations clears the model id owned by the
        // developer rule; the model entity supplies it later.
        Assert.Equal(7, association.ChannelModel!.ChannelId);
        Assert.Equal(string.Empty, association.ChannelModel.ModelId);

        Assert.NotNull(association.When);
        Assert.True(association.When.Enabled);
        var condition = association.When.Condition;
        Assert.NotNull(condition);
        Assert.Equal(FilterCondition.TypeGroup, condition.Type);
        Assert.Equal("and", condition.Logic);
        Assert.Collection(
            condition.Conditions!,
            promptTokens =>
            {
                Assert.Equal("prompt_tokens", promptTokens.Field);
                Assert.Equal("gte", promptTokens.Operator);
                // normalizeJSONNumberValue turns integers into int64.
                Assert.Equal(100L, promptTokens.Value!.GetValue<long>());
            },
            stream =>
            {
                Assert.Equal("stream", stream.Field);
                Assert.False(stream.Value!.GetValue<bool>());
            });
    }

    [Fact]
    public void NullDeveloperAndAssociationElementsArePreserved()
    {
        var settings = ModelSettingsCodec.Deserialize(
            """
            {"developer_settings":[
              null,
              {"developer":"openai","associations":[null,{"type":"channel_model","channelId":1}]}
            ]}
            """);

        Assert.Equal(2, settings.DeveloperSettings.Count);
        Assert.Null(settings.DeveloperSettings[0]);
        var developer = settings.DeveloperSettings[1];
        Assert.NotNull(developer);
        Assert.Null(developer.Associations[0]);
        Assert.NotNull(developer.Associations[1]);
    }

    [Fact]
    public void ChannelTagsModelIdsAreClearedAndNullableTagListsSurvive()
    {
        var settings = ModelSettingsCodec.Deserialize(
            """
            {"developer_settings":[{
              "developer":"openai",
              "associations":[
                {"type":"channel_tags_model","channelTagsModel":{"channelTags":["a",null],"modelId":"ignored"}},
                {"type":"regex","regex":{"pattern":"^gpt","exclude":[
                  {"channelNamePattern":"x-.*","channelIds":[1,null],"channelTags":null}
                ]}}
              ]
            }]}
            """);

        var associations = settings.DeveloperSettings[0]!.Associations;
        var tags = associations[0]!.ChannelTagsModel!;
        Assert.Equal(["a", ""], tags.ChannelTags);
        Assert.Equal(string.Empty, tags.ModelId);

        var exclude = Assert.Single(associations[1]!.Regex!.Exclude!);
        Assert.NotNull(exclude);
        Assert.Equal("x-.*", exclude.ChannelNamePattern);
        Assert.Equal([1, 0], exclude.ChannelIds);
        Assert.Null(exclude.ChannelTags);
    }

    [Fact]
    public void NullExcludeElementsAreSkippedByValidation()
    {
        var settings = ModelSettingsCodec.Deserialize(
            """
            {"developer_settings":[{"developer":"openai","associations":[{
              "type":"channel_model",
              "channelModel":{"channelId":1},
              "regex":{"exclude":[null,{"channelNamePattern":"x-.*"}]}
            }]}]}
            """);

        var association = settings.DeveloperSettings[0]!.Associations[0]!;
        Assert.Null(association.Regex!.Exclude![0]);

        // C130: the reference panics on a nil exclude element; the port skips it.
        ModelSettingsCodec.Validate(settings);

        var json = ModelSettingsCodec.Serialize(settings);
        Assert.Contains("\"exclude\":[null,", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ConditionValueNormalizationSortsObjectKeysAndKeepsGoNumbers()
    {
        var normalized = ModelSettingsCodec.NormalizeValue(
            JsonDocument.Parse("""{"b":2,"a":1.5,"c":{"z":1,"y":true}}""").RootElement);

        Assert.NotNull(normalized);
        Assert.Equal(
            """{"a":1.5,"b":2,"c":{"y":true,"z":1}}""",
            normalized.ToJsonString());
        Assert.Equal(2L, normalized["b"]!.GetValue<long>());
        Assert.Equal(1.5, normalized["a"]!.GetValue<double>());
    }

    [Fact]
    public void AssociationSerializationEmitsEveryPointerMember()
    {
        var settings = new ModelSettings
        {
            DeveloperSettings =
            [
                new DeveloperModelSettings(
                    "openai",
                    [
                        new ModelAssociation
                        {
                            Type = ModelAssociation.TypeChannelModel,
                            ChannelModel = new ChannelModelAssociation(7, string.Empty),
                        },
                    ]),
            ],
        };

        var json = ModelSettingsCodec.Serialize(settings);

        Assert.Contains(
            """
            "associations":[{"type":"channel_model","priority":0,"disabled":false,"channelModel":{"channelId":7,"modelId":""},"channelRegex":null,"regex":null,"modelId":null,"channelTagsModel":null,"channelTagsRegex":null}]
            """,
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"when\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateTrimsDevelopersAndRejectsDuplicates()
    {
        var settings = new ModelSettings
        {
            DeveloperSettings =
            [
                new DeveloperModelSettings(" openai ", []),
                new DeveloperModelSettings("openai", []),
            ],
        };

        var error = Assert.Throws<ModelSettingsValidationException>(
            () => ModelSettingsCodec.Validate(settings));

        Assert.Equal("duplicate model developer \"openai\"", error.Message);
    }

    [Fact]
    public void ValidateRequiresAnAssociationTypeSpecificPayload()
    {
        var settings = new ModelSettings
        {
            DeveloperSettings =
            [
                new DeveloperModelSettings(
                    "openai",
                    [new ModelAssociation { Type = ModelAssociation.TypeChannelModel }]),
            ],
        };

        var error = Assert.Throws<ModelSettingsValidationException>(
            () => ModelSettingsCodec.Validate(settings));

        Assert.Equal(
            "invalid developer settings for \"openai\": "
            + "developer channel association requires channel",
            error.Message);
    }

    [Fact]
    public void ValidateWrapsWhenConditionErrors()
    {
        var settings = new ModelSettings
        {
            DeveloperSettings =
            [
                new DeveloperModelSettings(
                    "openai",
                    [
                        new ModelAssociation
                        {
                            Type = ModelAssociation.TypeChannelModel,
                            ChannelModel = new ChannelModelAssociation(1, string.Empty),
                            When = new ModelAssociationWhen(
                                true,
                                new FilterCondition
                                {
                                    Type = FilterCondition.TypeCondition,
                                    Field = "stream",
                                    Operator = "eq",
                                    Value = JsonValue.Create(true),
                                }),
                        },
                    ]),
            ],
        };

        var error = Assert.Throws<ModelSettingsValidationException>(
            () => ModelSettingsCodec.Validate(settings));

        Assert.Equal(
            "invalid developer settings for \"openai\": invalid when condition: "
            + "root when condition must be a group",
            error.Message);
    }

    [Theory]
    [InlineData("unknown", "developer association type \"unknown\" is not supported")]
    public void ValidateRejectsUnsupportedDeveloperAssociationTypes(
        string type,
        string expected)
    {
        var settings = new ModelSettings
        {
            DeveloperSettings =
            [
                new DeveloperModelSettings(
                    "openai",
                    [new ModelAssociation { Type = type }]),
            ],
        };

        var error = Assert.Throws<ModelSettingsValidationException>(
            () => ModelSettingsCodec.Validate(settings));

        Assert.Equal(
            "invalid developer settings for \"openai\": " + expected,
            error.Message);
    }
}
