# Dashboard Token Overflow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve exact token counts above `int.MaxValue` across dashboard and API-key GraphQL statistics.

**Architecture:** Keep persistence aggregates as `long` and widen only the affected management records and GraphQL response fields. Verify the public GraphQL responses with endpoint tests; leave frontend numeric consumers and aggregation behavior unchanged.

**Tech Stack:** .NET, C#, HotChocolate GraphQL, xUnit, `System.Text.Json`.

## Global Constraints

- Use `long` end-to-end for token counts in affected service records and GraphQL response types.
- Keep frontend values numeric; no query or UI changes are needed.
- Leave aggregate queries, ordering, authorization, filtering, and token calculations unchanged.

---

### Task 1: Add 64-bit GraphQL regression tests

**Files:**
- Modify: `tests\AxonHub.Api.Tests\AdminDashboardEndpointTests.cs`

**Interfaces:**
- Consumes: Existing `QueryDataAsync`, `_dashboard.Aggregates`, `_dashboard.ApiKeyUsage`, `_dashboard.ApiKeyModelUsage`, and `_gate.ApiKeys` test fixture APIs.
- Produces: Three endpoint regression tests covering channel/model totals, API-key dashboard totals, and selected-key usage with Top Models.

- [ ] **Step 1: Add a channel/model test with an aggregate above `int.MaxValue`**

Add a test named `TokenStatsByChannelAndModelSupports64BitCounts`. Populate one row per dimension and query both fields:

```csharp
const long largeInput = (long)int.MaxValue + 1;
_dashboard.Aggregates[DashboardUsageDimension.Channel] =
[
    new DashboardUsageAggregateRow { Id = 1, Name = "channel", InputTokens = largeInput },
];
_dashboard.Aggregates[DashboardUsageDimension.Model] =
[
    new DashboardUsageAggregateRow { Name = "model", InputTokens = largeInput },
];

var data = await QueryDataAsync(
    """
    query {
      tokenStatsByChannel { inputTokens totalTokens }
      tokenStatsByModel { inputTokens totalTokens }
    }
    """);

var channel = Assert.Single(data.GetProperty("tokenStatsByChannel").EnumerateArray());
var model = Assert.Single(data.GetProperty("tokenStatsByModel").EnumerateArray());
Assert.Equal(largeInput, channel.GetProperty("inputTokens").GetInt64());
Assert.Equal(largeInput, channel.GetProperty("totalTokens").GetInt64());
Assert.Equal(largeInput, model.GetProperty("inputTokens").GetInt64());
Assert.Equal(largeInput, model.GetProperty("totalTokens").GetInt64());
```

- [ ] **Step 2: Add an API-key dashboard aggregate test**

Add `TokenStatsByApiKeySupports64BitCounts`; populate the existing API-key aggregate dimension and query the summary:

```csharp
const long largeInput = (long)int.MaxValue + 1;
_dashboard.Aggregates[DashboardUsageDimension.ApiKey] =
[
    new DashboardUsageAggregateRow
    {
        Id = 7,
        Name = "key-7",
        InputTokens = largeInput,
    },
];

var data = await QueryDataAsync(
    """
    query {
      tokenStatsByAPIKey { inputTokens totalTokens }
    }
    """);

var stats = Assert.Single(data.GetProperty("tokenStatsByAPIKey").EnumerateArray());
Assert.Equal(largeInput, stats.GetProperty("inputTokens").GetInt64());
Assert.Equal(largeInput, stats.GetProperty("totalTokens").GetInt64());
```

- [ ] **Step 3: Add a selected API-key detail and Top Models test**

Add `ApiKeyTokenUsageStatsSupports64BitCounts`; create an accessible key and seed both store rows above the 32-bit boundary:

```csharp
const long largeInput = (long)int.MaxValue + 1;
_gate.ApiKeys.Add(key =>
{
    key.Id = 1;
    key.Name = "key-1";
    key.ProjectId = 1;
});
_dashboard.ApiKeyUsage.Add(new DashboardApiKeyUsageRow
{
    ApiKeyId = 1,
    InputTokens = largeInput,
});
_dashboard.ApiKeyModelUsage.Add(new DashboardApiKeyModelUsageRow
{
    ApiKeyId = 1,
    ModelId = "model-1",
    InputTokens = largeInput,
});

var data = await QueryDataAsync(
    """
    query Usage($input: APIKeyTokenUsageStatsInput) {
      apiKeyTokenUsageStats(input: $input) {
        inputTokens
        topModels { modelId inputTokens }
      }
    }
    """,
    new { input = new { apiKeyIds = new[] { "gid://axonhub/APIKey/1" } } });

var stats = Assert.Single(data.GetProperty("apiKeyTokenUsageStats").EnumerateArray());
Assert.Equal(largeInput, stats.GetProperty("inputTokens").GetInt64());
var model = Assert.Single(stats.GetProperty("topModels").EnumerateArray());
Assert.Equal("model-1", model.GetProperty("modelId").GetString());
Assert.Equal(largeInput, model.GetProperty("inputTokens").GetInt64());
```

- [ ] **Step 4: Run the new endpoint tests and confirm they fail on checked narrowing**

Run:

```powershell
dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj -c Release --filter "FullyQualifiedName~TokenStatsByChannelAndModelSupports64BitCounts|FullyQualifiedName~TokenStatsByApiKeySupports64BitCounts|FullyQualifiedName~ApiKeyTokenUsageStatsSupports64BitCounts"
```

Expected: all three tests fail because a checked conversion from the aggregate `long` to `int` throws an overflow exception.

---

### Task 2: Widen affected service and GraphQL token fields

**Files:**
- Modify: `src\AxonHub.Api\Management\DashboardService.cs`
- Modify: `src\AxonHub.Api\GraphQL\DashboardTypes.cs`
- Test: `tests\AxonHub.Api.Tests\AdminDashboardEndpointTests.cs`

**Interfaces:**
- Consumes: The failing endpoint tests from Task 1 and existing `long` persistence row properties.
- Produces: 64-bit token values in `TokenStatsByChannel`, `TokenStatsByModel`, `TokenStatsByAPIKey`, `APIKeyTokenUsageStats`, and nested `ModelTokenUsageStats`.

- [ ] **Step 1: Widen affected management record token fields**

In `DashboardService.cs`, change only the token fields in these records from `int` to `long`: `TokenStatsByApiKeyModel`, `ApiKeyTokenUsageStatsModel`, `ModelTokenUsageStatsModel`, `TokenStatsByChannelModel`, and `TokenStatsByModelModel`. For example, the channel record becomes:

```csharp
public sealed record TokenStatsByChannelModel(
    string ChannelId,
    string ChannelName,
    long InputTokens,
    long OutputTokens,
    long CachedTokens,
    long ReasoningTokens,
    long TotalTokens);
```

Apply the same `long` token types to the other four named records. Do not widen unrelated request-count or daily-stat fields.

- [ ] **Step 2: Remove narrowing conversions from affected resolver projections**

In `GetTokenStatsByApiKeyAsync`, `GetApiKeyTokenUsageStatsAsync`, `GetTopModelsForApiKeysAsync`, `GetTokenStatsByChannelAsync`, and `GetTokenStatsByModelAsync`, pass the existing `long` row properties directly instead of casting them to `int`. For the channel projection, use:

```csharp
.Select(static row => new TokenStatsByChannelModel(
    new GidValue("Channel", row.Id).ToString(),
    row.Name,
    row.InputTokens,
    row.OutputTokens,
    row.CachedTokens,
    row.ReasoningTokens,
    TotalTokens(row)))
```

Apply the corresponding direct assignments for API-key, model, selected-key, and Top Models projections.

- [ ] **Step 3: Widen matching GraphQL response fields**

In `DashboardTypes.cs`, change the token properties to `long` in `TokenStatsByApiKeyType`, `TokenStatsByChannelType`, `TokenStatsByModelType`, `ApiKeyTokenUsageStatsType`, and `ModelTokenUsageStatsType`. Keep ID, name, and request-count fields unchanged.

- [ ] **Step 4: Run the targeted endpoint tests**

Run:

```powershell
dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj -c Release --filter "FullyQualifiedName~TokenStatsByChannelAndModelSupports64BitCounts|FullyQualifiedName~TokenStatsByApiKeySupports64BitCounts|FullyQualifiedName~ApiKeyTokenUsageStatsSupports64BitCounts"
```

Expected: all three regression tests pass, and each response exposes exact counts above `int.MaxValue`.

- [ ] **Step 5: Run the complete dashboard endpoint test class**

Run:

```powershell
dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj -c Release --filter "FullyQualifiedName~AdminDashboardEndpointTests"
```

Expected: all dashboard endpoint tests pass, including the existing user-usage 64-bit regression test.

- [ ] **Step 6: Commit the implementation**

```powershell
git add -- src\AxonHub.Api\Management\DashboardService.cs src\AxonHub.Api\GraphQL\DashboardTypes.cs tests\AxonHub.Api.Tests\AdminDashboardEndpointTests.cs
git commit -m "fix: preserve large dashboard token counts" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```
