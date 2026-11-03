using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Billing;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the saveChannelModelPrices mutation to the reference resolver and
/// biz.ChannelService.SaveChannelModelPrices (price.resolvers.go:17-24;
/// channel_price.go:374-508): validate duplicates then each price, query the
/// live prices, plan create/update/skip in input order followed by deletes for
/// omitted models, and write the plan in one transaction that archives the
/// active version and issues a new reference id on every update/create.
/// </summary>
public sealed class AdminChannelModelPriceEndpointTests : IAsyncLifetime
{
    private const string SaveMutation =
        """
        mutation Save($channelId: ID!, $input: [SaveChannelModelPriceInput!]!) {
          saveChannelModelPrices(channelId: $channelId, input: $input) {
            id
            channelID
            modelID
            referenceID
            price {
              items {
                itemCode
                pricing {
                  mode
                  flatFee
                  usagePerUnit
                  usageTiered {
                    tiers {
                      upTo
                      pricePerUnit
                    }
                  }
                }
                promptWriteCacheVariants {
                  variantCode
                  pricing {
                    mode
                    flatFee
                    usagePerUnit
                  }
                }
              }
              schedule {
                timezone
                overrides {
                  name
                  priority
                  when {
                    dailyTime {
                      start
                      end
                    }
                    weekdays
                    dateRange {
                      start
                      end
                    }
                  }
                  items {
                    itemCode
                    pricing {
                      mode
                      usagePerUnit
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string FlatFeePriceJson =
        """{"items":[{"itemCode":"prompt_tokens","pricing":{"mode":"flat_fee","flatFee":"1.5"}}]}""";

    private static readonly string[] SavedModelIds = ["model-a", "model-b", "model-d"];

    private static readonly string[] PlannedModelIds =
        ["model-a", "model-b", "model-d", "model-c"];

    private static readonly int[] Weekdays = [1, 7];

    private static readonly string[] SaveArgs = ["channelId", "input"];

    private static readonly string[] SaveInputFields = ["modelId", "price"];

    private static readonly string[] PriceInputFields = ["items", "schedule"];

    private static readonly string[] PricingInputFields =
        ["mode", "flatFee", "usagePerUnit", "usageTiered"];

    private static readonly string[] PricingModeValues =
        ["flat_fee", "usage_per_unit", "usage_tiered", "usage_volume"];

    private static readonly string[] VariantCodeValues = ["five_min", "one_hour"];

    private static readonly string[] ChannelPriceFields =
        ["id", "createdAt", "updatedAt", "channelID", "modelID", "price", "referenceID"];

    private readonly FakePriceStore _prices = new();

    private readonly AdminGraphqlGateServices _gate = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IChannelModelPriceStore>(_prices);
        builder.Services.AddScoped<ChannelModelPriceService>();

        _app = builder.Build();
        _app.MapAdminGraphql();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _gate.Authorize(_client);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task SaveChannelModelPricesCreatesUpdatesSkipsAndDeletesInReferenceOrder()
    {
        var unchanged = _prices.Seed(1, "model-a", FlatFeePriceJson, "ref-a");
        var changed = _prices.Seed(1, "model-b", FlatFeePriceJson, "ref-b");
        var removed = _prices.Seed(1, "model-c", FlatFeePriceJson, "ref-c");

        var data = await QueryDataAsync(
            SaveMutation,
            Save(
                1,
                Price("model-a", 1.5m),
                Price("model-b", 2m),
                Price("model-d", 3m)));

        var saved = Saved(data);

        // Input order first (skip, update, create); the delete is not returned.
        Assert.Equal(
            SavedModelIds,
            saved.Select(row => row.GetProperty("modelID").GetString()).ToArray());

        Assert.Equal(PriceGid(unchanged.Id), saved[0].GetProperty("id").GetString());
        Assert.Equal("ref-a", saved[0].GetProperty("referenceID").GetString());

        Assert.Equal(PriceGid(changed.Id), saved[1].GetProperty("id").GetString());
        AssertReferenceId(saved[1]);

        Assert.True(saved[2].GetProperty("id").GetString() != PriceGid(removed.Id));
        AssertReferenceId(saved[2]);

        // The plan is input order followed by one delete per omitted live row.
        Assert.Equal(
            new[]
            {
                ChannelModelPriceChangeType.Skip,
                ChannelModelPriceChangeType.Update,
                ChannelModelPriceChangeType.Create,
                ChannelModelPriceChangeType.Delete,
            },
            _prices.Changes.Select(change => change.Type).ToArray());
        Assert.Equal(
            PlannedModelIds,
            _prices.Changes.Select(change => change.ModelId).ToArray());
        Assert.Equal(1, _prices.TouchedChannelId);
        Assert.NotNull(_prices.AppliedAt);

        // The delete soft-deletes the current row; listByChannel no longer sees it.
        Assert.Equal(
            SavedModelIds,
            (await _prices.ListByChannelAsync(1)).Select(row => row.ModelId).ToArray());
    }

    [Fact]
    public async Task SaveChannelModelPricesDeletesEveryOmittedPriceForAnEmptyInput()
    {
        _prices.Seed(1, "model-a", FlatFeePriceJson, "ref-a");
        _prices.Seed(1, "model-b", FlatFeePriceJson, "ref-b");

        var data = await QueryDataAsync(SaveMutation, Save(1));

        Assert.Empty(Saved(data));
        Assert.Equal(
            new[] { ChannelModelPriceChangeType.Delete, ChannelModelPriceChangeType.Delete },
            _prices.Changes.Select(change => change.Type).ToArray());
        Assert.Empty(await _prices.ListByChannelAsync(1));
    }

    [Fact]
    public async Task SaveChannelModelPricesRejectsDuplicateModelIds()
    {
        var errors = await QueryErrorsAsync(
            SaveMutation,
            Save(1, Price("model-a", 1m), Price("model-a", 2m)));

        Assert.Equal(
            "duplicate model price input: model_id=model-a",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_prices.Changes);
    }

    [Fact]
    public async Task SaveChannelModelPricesWrapsValidationWithTheModelId()
    {
        var errors = await QueryErrorsAsync(
            SaveMutation,
            Save(
                1,
                new
                {
                    modelId = "model-a",
                    price = new
                    {
                        items = new[]
                        {
                            new
                            {
                                itemCode = "prompt_tokens",
                                pricing = new { mode = "flat_fee" },
                            },
                        },
                    },
                }));

        Assert.Equal(
            "invalid model price: model_id=model-a: items[0]: pricing: flatFee is required",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_prices.Changes);
    }

    [Fact]
    public async Task SaveChannelModelPricesValidatesTheScheduleTree()
    {
        var errors = await QueryErrorsAsync(
            SaveMutation,
            Save(
                1,
                new
                {
                    modelId = "model-a",
                    price = new
                    {
                        items = new[]
                        {
                            new
                            {
                                itemCode = "prompt_tokens",
                                pricing = new { mode = "usage_per_unit", usagePerUnit = 0.1m },
                            },
                        },
                        schedule = new
                        {
                            timezone = "Asia/Shanghai",
                            overrides = new[]
                            {
                                new
                                {
                                    name = "night",
                                    priority = 1,
                                    when = new
                                    {
                                        dailyTime = new { start = "25:00", end = "08:00" },
                                    },
                                    items = new[]
                                    {
                                        new
                                        {
                                            itemCode = "prompt_tokens",
                                            pricing = new
                                            {
                                                mode = "usage_per_unit",
                                                usagePerUnit = 0.05m,
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                }));

        Assert.Equal(
            "invalid model price: model_id=model-a: schedule: overrides[0]: when: dailyTime: start: hour must be between 0 and 23, got 25",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task SaveChannelModelPricesReportsStoreQueryFailures()
    {
        _prices.ListFailure = new StoreFailureException(
            "database is down",
            new InvalidOperationException("database is down"));

        var errors = await QueryErrorsAsync(SaveMutation, Save(1, Price("model-a", 1m)));

        Assert.Equal(
            "failed to query existing channel model prices: database is down",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task SaveChannelModelPricesReportsAnUndecodableStoredPriceAsAQueryFailure()
    {
        _prices.Seed(1, "model-a", "{not json", "ref-a");

        var errors = await QueryErrorsAsync(SaveMutation, Save(1, Price("model-a", 1m)));

        var message = errors[0].GetProperty("message").GetString()!;
        Assert.StartsWith(
            "failed to query existing channel model prices: ",
            message,
            StringComparison.Ordinal);
        Assert.Empty(_prices.Changes);
    }

    [Fact]
    public async Task SaveChannelModelPricesForwardsTheApplyFailure()
    {
        _prices.ApplyFailure = new StoreFailureException(
            "failed to create channel model price: duplicate key",
            new InvalidOperationException("duplicate key"));

        var errors = await QueryErrorsAsync(SaveMutation, Save(1, Price("model-a", 1m)));

        Assert.Equal(
            "failed to create channel model price: duplicate key",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task SaveChannelModelPricesReportsAMissingChannel()
    {
        _prices.ChannelMissing = true;

        var errors = await QueryErrorsAsync(SaveMutation, Save(7, Price("model-a", 1m)));

        Assert.Equal("ent: channel not found", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task SaveChannelModelPricesTreatsScaledAndUnscaledDecimalsAsEqual()
    {
        _prices.Seed(1, "model-a", FlatFeePriceJson, "ref-a");

        var data = await QueryDataAsync(
            SaveMutation,
            Save(1, Price("model-a", 1.50m)));

        var saved = Saved(data);
        Assert.Single(saved);
        Assert.Equal("ref-a", saved[0].GetProperty("referenceID").GetString());
        Assert.Equal(
            ChannelModelPriceChangeType.Skip,
            Assert.Single(_prices.Changes).Type);
    }

    [Fact]
    public async Task SaveChannelModelPricesComparesTheWholePriceStructure()
    {
        _prices.Seed(1, "model-a", FlatFeePriceJson, "ref-a");

        // Same item and amount, but an extra cache-write variant changes the
        // structural equality, so the row is updated and gets a new reference.
        var data = await QueryDataAsync(
            SaveMutation,
            Save(
                1,
                new
                {
                    modelId = "model-a",
                    price = new
                    {
                        items = new[]
                        {
                            new
                            {
                                itemCode = "prompt_tokens",
                                pricing = new { mode = "flat_fee", flatFee = 1.5m },
                                promptWriteCacheVariants = new[]
                                {
                                    new
                                    {
                                        variantCode = "five_min",
                                        pricing = new { mode = "flat_fee", flatFee = 3m },
                                    },
                                },
                            },
                        },
                    },
                }));

        var saved = Saved(data);
        Assert.Single(saved);
        AssertReferenceId(saved[0]);
        Assert.NotEqual("ref-a", saved[0].GetProperty("referenceID").GetString());
        Assert.Equal(
            ChannelModelPriceChangeType.Update,
            Assert.Single(_prices.Changes).Type);
    }

    [Fact]
    public async Task SaveChannelModelPricesStoresGoShapedJsonForDecimalsAndVariants()
    {
        await QueryDataAsync(
            SaveMutation,
            Save(
                1,
                new
                {
                    modelId = "model-a",
                    price = new
                    {
                        items = new[]
                        {
                            new
                            {
                                itemCode = "prompt_write_cached_tokens",
                                pricing = new { mode = "usage_per_unit", usagePerUnit = 0.000003m },
                                promptWriteCacheVariants = new[]
                                {
                                    new
                                    {
                                        variantCode = "one_hour",
                                        pricing = new { mode = "flat_fee", flatFee = 2m },
                                    },
                                },
                            },
                        },
                        schedule = new
                        {
                            timezone = "UTC",
                            overrides = new[]
                            {
                                new
                                {
                                    name = "night",
                                    priority = 2,
                                    when = new
                                    {
                                        weekdays = Weekdays,
                                        dateRange = new { start = "2026-01-01", end = "2026-12-31" },
                                    },
                                    items = new[]
                                    {
                                        new
                                        {
                                            itemCode = "completion_tokens",
                                            pricing = new
                                            {
                                                mode = "usage_per_unit",
                                                usagePerUnit = 0.5m,
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                }));

        var row = Assert.Single(_prices.Rows);
        using var stored = JsonDocument.Parse(row.Price);

        // shopspring decimal JSON keeps the value as a string, trailing zeros
        // trimmed; the field order follows the Go structs.
        var item = stored.RootElement.GetProperty("items")[0];
        Assert.Equal("prompt_write_cached_tokens", item.GetProperty("itemCode").GetString());
        Assert.Equal("0.000003", item.GetProperty("pricing").GetProperty("usagePerUnit").GetString());
        Assert.Equal(
            "2",
            item.GetProperty("promptWriteCacheVariants")[0]
                .GetProperty("pricing")
                .GetProperty("flatFee")
                .GetString());

        var priceOverride = stored.RootElement
            .GetProperty("schedule")
            .GetProperty("overrides")[0];
        Assert.Equal("night", priceOverride.GetProperty("name").GetString());
        Assert.Equal(2, priceOverride.GetProperty("priority").GetInt32());
        Assert.Equal(
            Weekdays,
            priceOverride.GetProperty("when").GetProperty("weekdays")
                .EnumerateArray().Select(day => day.GetInt32()).ToArray());
        Assert.Equal(
            "2026-12-31",
            priceOverride.GetProperty("when").GetProperty("dateRange")
                .GetProperty("end").GetString());
    }

    [Fact]
    public async Task SaveChannelModelPricesReturnsTheStoredPriceTree()
    {
        var data = await QueryDataAsync(
            SaveMutation,
            new
            {
                channelId = Gid(3),
                input = new[]
                {
                    new
                    {
                        modelId = "model-a",
                        price = new
                        {
                            items = new[]
                            {
                                new
                                {
                                    itemCode = "completion_tokens",
                                    pricing = new
                                    {
                                        mode = "usage_tiered",
                                        usageTiered = new
                                        {
                                            tiers = new[]
                                            {
                                                new { upTo = (int?)1000, pricePerUnit = 0.5m },
                                                new { upTo = (int?)null, pricePerUnit = 0.25m },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            });

        var saved = Saved(data);
        var row = Assert.Single(saved);
        Assert.Equal(PriceGid(1), row.GetProperty("id").GetString());
        Assert.Equal(Gid(3), row.GetProperty("channelID").GetString());

        var item = row.GetProperty("price").GetProperty("items")[0];
        Assert.Equal("completion_tokens", item.GetProperty("itemCode").GetString());
        var pricing = item.GetProperty("pricing");
        Assert.Equal("usage_tiered", pricing.GetProperty("mode").GetString());

        var tiers = pricing.GetProperty("usageTiered").GetProperty("tiers");
        Assert.Equal(2, tiers.GetArrayLength());
        Assert.Equal(1000, tiers[0].GetProperty("upTo").GetInt32());
        Assert.Equal(0.5m, tiers[0].GetProperty("pricePerUnit").GetDecimal());
        Assert.Equal(JsonValueKind.Null, tiers[1].GetProperty("upTo").ValueKind);
        Assert.Equal(0.25m, tiers[1].GetProperty("pricePerUnit").GetDecimal());
    }

    [Fact]
    public async Task SaveChannelModelPricesRejectsAMalformedChannelGid()
    {
        var errors = await QueryErrorsAsync(
            SaveMutation,
            new { channelId = "3", input = Array.Empty<object>() });

        Assert.Equal(
            "guid must start with gid://axonhub/",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task IntrospectionExposesTheReferencePriceSurface()
    {
        var data = await QueryDataAsync(
            """
            query {
              schema: __schema {
                mutationType {
                  fields { name args { name type { kind name ofType { kind name } } } }
                }
              }
              saveInput: __type(name: "SaveChannelModelPriceInput") {
                inputFields { name type { kind name ofType { kind name } } }
              }
              priceInput: __type(name: "ModelPriceInput") {
                inputFields { name type { kind name ofType { kind name } } }
              }
              pricingInput: __type(name: "PricingInput") {
                inputFields { name type { kind name ofType { kind name } } }
              }
              pricingMode: __type(name: "PricingMode") { enumValues { name } }
              variantCode: __type(name: "PromptWriteCacheVariantCode") { enumValues { name } }
              channelPrice: __type(name: "ChannelModelPrice") {
                fields { name type { kind name ofType { kind name } } }
              }
            }
            """,
            null);

        var save = data.GetProperty("schema").GetProperty("mutationType").GetProperty("fields")
            .EnumerateArray()
            .Single(field => field.GetProperty("name").GetString() == "saveChannelModelPrices");
        var args = save.GetProperty("args").EnumerateArray().ToArray();
        Assert.Equal(
            SaveArgs,
            args.Select(arg => arg.GetProperty("name").GetString()).ToArray());
        Assert.Equal("NON_NULL", args[0].GetProperty("type").GetProperty("kind").GetString());
        Assert.Equal("ID", args[0].GetProperty("type").GetProperty("ofType").GetProperty("name").GetString());
        Assert.Equal("NON_NULL", args[1].GetProperty("type").GetProperty("kind").GetString());

        var inputFields = data.GetProperty("saveInput").GetProperty("inputFields")
            .EnumerateArray().ToArray();
        Assert.Equal(
            SaveInputFields,
            inputFields.Select(field => field.GetProperty("name").GetString()).ToArray());
        Assert.Equal("String", inputFields[0].GetProperty("type").GetProperty("ofType").GetProperty("name").GetString());
        Assert.Equal("ModelPriceInput", inputFields[1].GetProperty("type").GetProperty("ofType").GetProperty("name").GetString());

        var priceFields = data.GetProperty("priceInput").GetProperty("inputFields")
            .EnumerateArray().ToArray();
        Assert.Equal(
            PriceInputFields,
            priceFields.Select(field => field.GetProperty("name").GetString()).ToArray());
        Assert.Equal("NON_NULL", priceFields[0].GetProperty("type").GetProperty("kind").GetString());

        var pricingFields = data.GetProperty("pricingInput").GetProperty("inputFields")
            .EnumerateArray().ToArray();
        Assert.Equal(
            PricingInputFields,
            pricingFields.Select(field => field.GetProperty("name").GetString()).ToArray());
        Assert.Equal("PricingMode", pricingFields[0].GetProperty("type").GetProperty("ofType").GetProperty("name").GetString());

        Assert.Equal(
            PricingModeValues,
            data.GetProperty("pricingMode").GetProperty("enumValues")
                .EnumerateArray().Select(value => value.GetProperty("name").GetString()).ToArray());
        Assert.Equal(
            VariantCodeValues,
            data.GetProperty("variantCode").GetProperty("enumValues")
                .EnumerateArray().Select(value => value.GetProperty("name").GetString()).ToArray());

        var channelPriceFields = data.GetProperty("channelPrice").GetProperty("fields")
            .EnumerateArray().Select(field => field.GetProperty("name").GetString()).ToArray();
        Assert.Equal(ChannelPriceFields, channelPriceFields);
    }

    [Fact]
    public async Task SaveChannelModelPricesRejectsAnUnknownPricingModeAtTheServiceSeam()
    {
        // The GraphQL enum cannot produce an unknown mode; the service seam is
        // reachable from bulk/auto-pricing callers and keeps the reference
        // message (price.go:103-104).
        var service = new ChannelModelPriceService(_prices);
        var command = new SaveChannelModelPricesCommand(
            1,
            [
                new Management.SaveChannelModelPriceInput(
                    "model-a",
                    new ModelPrice
                    {
                        Items =
                        [
                            new ModelPriceItem
                            {
                                ItemCode = PriceItemCodes.PromptTokens,
                                Pricing = new Pricing { Mode = "mystery" },
                            },
                        ],
                    }),
            ]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveAsync(command));

        Assert.Equal(
            "invalid model price: model_id=model-a: items[0]: pricing: unknown pricing mode: mystery",
            error.Message);
        Assert.Empty(_prices.Changes);
    }

    private static object Save(long channelId, params object[] input)
        => new { channelId = Gid(channelId), input };

    private static object Price(string modelId, decimal flatFee)
        => new
        {
            modelId,
            price = new
            {
                items = new[]
                {
                    new
                    {
                        itemCode = "prompt_tokens",
                        pricing = new { mode = "flat_fee", flatFee },
                    },
                },
            },
        };

    private static JsonElement[] Saved(JsonElement data)
        => data.GetProperty("saveChannelModelPrices")
            .EnumerateArray()
            .Select(static row => row.Clone())
            .ToArray();

    private static string Gid(long id) => "gid://axonhub/Channel/" + id;

    private static string PriceGid(long id) => "gid://axonhub/ChannelModelPrice/" + id;

    private static void AssertReferenceId(JsonElement row)
    {
        var referenceId = row.GetProperty("referenceID").GetString()!;
        Assert.Equal(8, referenceId.Length);
        Assert.All(
            referenceId,
            character => Assert.True(
                char.IsAsciiLetter(character),
                "reference id contains a non-letter: " + referenceId));
    }

    private async Task<JsonElement> QueryDataAsync(string query, object? variables)
    {
        var root = await ExecuteAsync(query, variables);
        Assert.False(root.TryGetProperty("errors", out var errors), errors.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(string query, object? variables)
    {
        var root = await ExecuteAsync(query, variables);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> ExecuteAsync(string query, object? variables)
    {
        var payload = JsonSerializer.Serialize(new { query, variables });
        using var response = await _client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// In-memory stand-in for the price store. ApplyAsync follows the port's
    /// documented transaction effects so the resolver-visible results and the
    /// planned action sequence can both be asserted without PostgreSQL.
    /// </summary>
    private sealed class FakePriceStore : IChannelModelPriceStore
    {
        private long _nextId = 1;

        public List<ChannelModelPriceRecord> Rows { get; } = [];

        public List<ChannelModelPriceChange> Changes { get; private set; } = [];

        public DateTime? AppliedAt { get; private set; }

        public long TouchedChannelId { get; private set; }

        public StoreFailureException? ListFailure { get; set; }

        public StoreFailureException? ApplyFailure { get; set; }

        public bool ChannelMissing { get; set; }

        public ChannelModelPriceRecord Seed(
            long channelId,
            string modelId,
            string priceJson,
            string referenceId)
        {
            var row = new ChannelModelPriceRecord
            {
                Id = _nextId++,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ModelId = modelId,
                Price = priceJson,
                ReferenceId = referenceId,
                ChannelId = channelId,
            };

            Rows.Add(row);
            return row;
        }

        public Task<long> CreateAsync(
            ChannelModelPriceRecord price,
            CancellationToken cancellationToken = default)
        {
            price.Id = _nextId++;
            Rows.Add(price);
            return Task.FromResult(price.Id);
        }

        public Task<IReadOnlyList<ChannelModelPriceRecord>> ListByChannelAsync(
            long channelId,
            CancellationToken cancellationToken = default)
        {
            if (ListFailure is not null)
            {
                throw ListFailure;
            }

            IReadOnlyList<ChannelModelPriceRecord> rows = Rows
                .Where(row => row.ChannelId == channelId && row.DeletedAt == 0)
                .OrderBy(row => row.Id)
                .ToList();
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyList<ChannelModelPriceRecord>> ListEnabledChannelPricesAsync(
            long channelId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("the price write slice never reads enabled prices");

        public Task<IReadOnlyList<ChannelModelPriceRecord?>> ApplyAsync(
            long channelId,
            IReadOnlyList<ChannelModelPriceChange> changes,
            DateTime now,
            CancellationToken cancellationToken = default)
        {
            if (ApplyFailure is not null)
            {
                throw ApplyFailure;
            }

            Changes = changes.ToList();
            AppliedAt = now;
            TouchedChannelId = channelId;

            if (ChannelMissing)
            {
                throw new StoreFailureException(
                    "ent: channel not found",
                    new InvalidOperationException("ent: channel not found"));
            }

            var results = new ChannelModelPriceRecord?[changes.Count];
            for (var index = 0; index < changes.Count; index++)
            {
                var change = changes[index];
                switch (change.Type)
                {
                    case ChannelModelPriceChangeType.Skip:
                        results[index] = change.Existing;
                        break;
                    case ChannelModelPriceChangeType.Delete:
                        Rows.Single(row => row.Id == change.ExistingId).DeletedAt =
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        results[index] = null;
                        break;
                    case ChannelModelPriceChangeType.Create:
                        results[index] = Seed(
                            channelId,
                            change.ModelId,
                            change.PriceJson!,
                            change.ReferenceId!);
                        break;
                    case ChannelModelPriceChangeType.Update:
                        var updated = Rows.Single(row => row.Id == change.ExistingId);
                        updated.Price = change.PriceJson!;
                        updated.ReferenceId = change.ReferenceId!;
                        updated.UpdatedAt = now;
                        results[index] = updated;
                        break;
                }
            }

            return Task.FromResult<IReadOnlyList<ChannelModelPriceRecord?>>(results);
        }

        public Task<IReadOnlyList<ChannelModelPriceVersionRecord>> ListVersionsAsync(
            long channelModelPriceId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("versions are asserted in the persistence tests");

        public Task<bool> EnsureAutoPricesAsync(
            long channelId,
            IReadOnlyList<string> modelIds,
            ModelCardPriceJsonFactory modelCardToPriceJson,
            ChannelPriceReferenceIdFactory referenceIdFactory,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("auto-pricing is covered by the channel lifecycle tests");

        public Task<IReadOnlyList<ChannelModelPriceTemplate>> ResolveAutoPriceTemplatesAsync(
            IReadOnlyList<string> modelIds,
            ModelCardPriceJsonFactory modelCardToPriceJson,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("auto-pricing is covered by the channel lifecycle tests");

        public Task<bool> ApplyAutoPriceTemplatesAsync(
            long channelId,
            IReadOnlyList<ChannelModelPriceTemplate> templates,
            ChannelPriceReferenceIdFactory referenceIdFactory,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("auto-pricing is covered by the channel lifecycle tests");

        public Task CopyChannelPricesAsync(
            long sourceChannelId,
            long targetChannelId,
            ChannelPriceReferenceIdFactory referenceIdFactory,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("price copying is covered by the channel lifecycle tests");
    }
}
