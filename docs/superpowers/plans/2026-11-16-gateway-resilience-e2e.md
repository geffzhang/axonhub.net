# Gateway Resilience E2E Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the existing local-provider E2E suite with 22 deterministic resilience cases covering ten failure families, final-channel accounting, retry bounds, streaming termination, and trace stickiness.

**Architecture:** Add a focused `LlmTestServerResilienceE2ETests` class that uses the existing in-process Orleans fixture and real `HttpExecutor`. Share only the low-level LlmTestServer control API client, query its request evidence after each fault, and record AxonHub request/usage data with deterministic prices.

**Tech Stack:** .NET, C#, xUnit, Orleans.TestingHost, ASP.NET Core `DefaultHttpContext`, local LlmTestServer HTTP admin/provider APIs.

## Global Constraints

- Start with the existing 18 LlmTestServer E2E cases; the target after this work is 38–58 total.
- Use only local LlmTestServer fixtures and the fake `test-key`; do not call a real provider or use a real provider key.
- Run the E2E cases only when `AXONHUB_E2E_LLM_TEST_SERVER_URL` is set; otherwise follow the existing skip behavior.
- Keep each of the ten failure families to 2–4 behavior-distinguishing variants and disable hidden client retries.
- Preserve the current native-tool fallback semantics when no compatible candidate exists.
- Test the AxonHub gateway boundary only; do not claim that external tool side effects are exactly-once.

---

### Task 1: Share the LlmTestServer control API client

**Files:**
- Create: `tests/AxonHub.Api.Tests/LlmTestServerAdminClient.cs`
- Modify: `tests/AxonHub.Api.Tests/LlmTestServerE2ETests.cs`
- Modify: `tests/AxonHub.Api.Tests/LlmTestServerProviderMatrixTests.cs`

**Interfaces:**
- Consumes: `LlmTestServerE2ETests.ServerUrl` and `LlmTestServerE2ETests.ApiKey`.
- Produces: `LlmTestServerAdminClient.ConfigureAsync(object, CancellationToken)`, `ResetAsync(CancellationToken)`, `GetAssertionsAsync(CancellationToken)`, `GetRequestsAsync(string query, CancellationToken)`, and `CountRequestsAsync(string provider, int? status, CancellationToken)`.

- [ ] **Step 1: Establish the baseline**

Run the existing E2E filter with the local LlmTestServer running and `AXONHUB_E2E_LLM_TEST_SERVER_URL` set.

Run: `dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~LlmTestServerE2E"`
Expected: all 18 existing cases pass; no real provider calls occur.

- [ ] **Step 2: Add the typed admin API helper**

Create one helper for configuration and evidence reads. Every non-success HTTP response must fail with status and response body rather than returning an empty JSON object.

```csharp
internal interface ILlmTestServerAdminClient
{
    Task ConfigureAsync(object configuration, CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
    Task<JsonDocument> GetAssertionsAsync(CancellationToken cancellationToken = default);
    Task<JsonDocument> GetRequestsAsync(
        string query,
        CancellationToken cancellationToken = default);
    Task<int> CountRequestsAsync(
        string provider,
        int? status = null,
        CancellationToken cancellationToken = default);
}
```

Implement `LlmTestServerAdminClient` with `POST /admin-api/configure`, `POST /admin-api/reset`, `GET /admin-api/assertions`, and `GET /admin-api/requests`. `CountRequestsAsync` builds the provider/status query and counts the returned records. Send `Authorization: Bearer {apiKey}` on every control request. Read each response body before checking the status; throw `HttpRequestException` containing the status and body on failure.

- [ ] **Step 3: Route existing configuration calls through the helper**

Replace the duplicated low-level configure HTTP code in the two existing test classes with `LlmTestServerAdminClient.ConfigureAsync`. Keep their JSON fixture shapes, models, scenarios, and assertions unchanged.

- [ ] **Step 4: Re-run the baseline**

Run: `dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~LlmTestServerE2E"`
Expected: the same 18 cases pass with the shared control client.

- [ ] **Step 5: Commit the helper refactor**

```powershell
git add tests\AxonHub.Api.Tests\LlmTestServerAdminClient.cs tests\AxonHub.Api.Tests\LlmTestServerE2ETests.cs tests\AxonHub.Api.Tests\LlmTestServerProviderMatrixTests.cs
git commit -m "test: share LlmTestServer E2E control client"
```

### Task 2: Add bounded retry and terminal error cases

**Files:**
- Create: `tests/AxonHub.Api.Tests/LlmTestServerResilienceE2ETests.cs`
- Use: `tests/AxonHub.Api.Tests/TestDoubles.cs`
- Use: `tests/AxonHub.Api.Tests/UsageCostInjectionTests.cs`

**Interfaces:**
- Consumes: `LlmTestServerAdminClient` from Task 1 and existing `RecordingRequestChainRecorder`.
- Produces: private `SendAsync(string body, RetryPolicy policy, params ChannelDescriptor[] channels)` and channel builders that route primary and fallback through distinguishable local provider namespaces.

- [ ] **Step 1: Add failing cases for transient 429, exhausted 429, auth, and filtering**

Add these cases before implementation:

1. `Transient429FallsBackToBAndBillsOnlyFinalUsage`
2. `Transient429NeverExceedsConfiguredAttemptBudget`
3. `InsufficientQuota429StopsWithoutFallback`
4. `InsufficientBalance429StopsWithoutFallback`
5. `UnauthorizedAndForbiddenAreNotRetriedOrWrapped` as a theory with 401 and 403 rows
6. `NonStreamContentFilterIsNotRetried`
7. `StreamContentFilterIsNotRetried`

Use explicit queued error fixtures such as:

```csharp
new
{
    error = new
    {
        statusCode = 429,
        code = "rate_limit_exceeded",
        message = "temporary rate limit",
    },
}
```

Use separate LlmTestServer provider namespaces for A and B (for example `/openai/v1` and `/groq/openai/v1`) so the admin request journal can distinguish their counts. Use an `insufficient_quota`/balance error code for permanent 429 cases, `invalid_api_key` for 401, `permission_denied` for 403, and `content_filter` for filtering.

- [ ] **Step 2: Run the focused cases and confirm the missing-test failure**

Run: `dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~LlmTestServerResilienceE2ETests"`
Expected: every case executes against the real local HTTP boundary; any failure is an assertion exposing a retry/classification/accounting mismatch or a fixture setup error, not a missing external provider.

- [ ] **Step 3: Add the E2E send and evidence assertions**

Build the orchestrator with a fixed retry policy and injected `RecordingRequestChainRecorder` plus deterministic `IUsageCostService`. For successful fallback, assert HTTP 200 and B’s scripted answer, then assert A has one 429 request, B has one successful match, the request chain identifies B as the final channel, and the usage log contains only the configured successful usage/cost. For non-retryable errors, assert the public error status/type, exactly one A request, and zero B requests.

Use one explicit error response assertion rather than silently accepting any non-200 result:

```csharp
Assert.Equal(expectedStatus, context.Response.StatusCode);
Assert.Contains(expectedCode, ResponseBody(context), StringComparison.Ordinal);
Assert.Equal(1, await CountRequestsAsync(provider: "openai", status: expectedStatus));
Assert.Equal(0, await CountRequestsAsync(provider: "groq"));
```

For content-filter streams, assert the provider error is delivered as an error and does not produce a successful terminal stream or fallback answer.

- [ ] **Step 4: Run the focused cases and correct the harness**

Run: `dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~LlmTestServerResilienceE2ETests"`
Expected: all 8 logical cases pass, counting the 401/403 theory rows separately.

- [ ] **Step 5: Commit the error classification cases**

```powershell
git add tests\AxonHub.Api.Tests\LlmTestServerResilienceE2ETests.cs
git commit -m "test: cover gateway retry error classification"
```

### Task 3: Cover auto-disable and circuit-breaker transitions

**Files:**
- Modify: `tests/AxonHub.Api.Tests/LlmTestServerResilienceE2ETests.cs`
- Use: `tests/AxonHub.Api.Tests/ChannelAutoDisableTestDoubles.cs`
- Use: `tests/AxonHub.Api.Tests/ChannelAutoDisableTests.cs`
- Use: `tests/AxonHub.Api.Tests/PersistentOutboundRetryTests.cs`

**Interfaces:**
- Consumes: the error fixtures and E2E send helper from Task 2.
- Produces: a test-only auto-disable queue that captures completed `ChannelPerformanceRecord` values, a local `ModelCircuitBreaker`, and a manual `TimeProvider` for probe tests.

- [ ] **Step 1: Add failing threshold and breaker cases**

Add:

1. `AutoDisableThresholdMinusOneKeepsChannelEnabled`
2. `AutoDisableAtThresholdSkipsAOnNextRequest`
3. `ThreeFailuresEnterHalfOpen`
4. `FifthFailureOpensCircuit`
5. `SuccessfulHalfOpenProbeClosesCircuit`
6. `FailedHalfOpenProbeUsesBackoff`

Configure the auto-disable rule’s `StatusCodes` to `[503]` and its `Times` to the test threshold. Keep the auto-disable queue deterministic: capture the record from `Enqueue`, evaluate it through `ChannelAutoDisableService`, and update the fake enabled-channel source from the fake credential mutation used by the existing auto-disable harness. Await that evaluation signal before issuing the next request.

- [ ] **Step 2: Run the new cases to observe the missing behavior**

Run: `dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~AutoDisable|FullyQualifiedName~CircuitBreaker"`
Expected: each new method executes; failing assertions identify missing auto-disable/clock wiring or a mismatch in the intended threshold transition.

- [ ] **Step 3: Wire auto-disable evaluation and a manual breaker clock**

Pass `autoDisableQueue` and `modelCircuitBreaker` to `ChatCompletionOrchestrator`. Keep the test clock private to the new resilience fixture so the shared Orleans fixture and unrelated tests retain wall-clock behavior.

```csharp
private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
```

Use `ModelCircuitBreaker.HalfOpenThreshold`, `OpenThreshold`, and `ProbeInterval` rather than duplicating their numeric values in the implementation. Verify a probe is allowed only after advancing beyond `NextProbeAt`; a successful probe resets state and a failed probe schedules a later probe window.

- [ ] **Step 4: Re-run threshold and breaker cases**

Run: `dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~AutoDisable|FullyQualifiedName~CircuitBreaker"`
Expected: all 6 cases pass without wall-clock sleeps.

- [ ] **Step 5: Commit auto-disable and circuit coverage**

```powershell
git add tests\AxonHub.Api.Tests\LlmTestServerResilienceE2ETests.cs
git commit -m "test: cover gateway disable and circuit recovery"
```

### Task 4: Cover capability filtering, timeout budgets, and trace stickiness

**Files:**
- Modify: `tests/AxonHub.Api.Tests/LlmTestServerResilienceE2ETests.cs`
- Use: `tests/AxonHub.Api.Tests/NativeToolSelectionTests.cs`
- Use: `tests/AxonHub.Api.Tests/StickyChannelSelectionTests.cs`
- Use: `src/AxonHub.Api/Http/RequestTracingMiddleware.cs`
- Use: `src/AxonHub.Api/Http/RequestTimeoutMiddleware.cs`

**Interfaces:**
- Consumes: Task 1’s admin client and Task 2’s deterministic send helper.
- Produces: fixed candidate-selector helper implementing `IModelCandidateSelector.SelectAsync(LlmRequest, RequestIdentity, CancellationToken)`, a traced request helper that wraps `ChatCompletionEndpoint.HandleAsync` in `RequestTracingMiddleware`, and a deadline helper that wraps it in `RequestTimeoutMiddleware`.

- [ ] **Step 1: Add failing capability, timeout, and trace cases**

Add:

1. `NativeToolRequestKeepsCompatibleFallbackOnly`
2. `NoCompatibleNativeToolChannelPreservesExistingCandidateFallback`
3. `TimedOutPrimaryFallsBackWithinDeadline`
4. `ExpiredOverallDeadlinePreventsNextAttempt`
5. `RepeatedTraceIdSticksToPreviouslySelectedChannel`
6. `DifferentTraceIdUsesNormalCandidateOrder`

For native-tool cases, supply a Gemini or Anthropic native tool and order incompatible and compatible candidates explicitly. Assert that an incompatible provider receives zero calls whenever a compatible candidate exists. When no compatible candidate exists, assert the current fallback-to-original-candidate-list behavior rather than introducing a new fail-closed policy.

- [ ] **Step 2: Run the new cases and confirm failure**

Run: `dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~NativeToolRequest|FullyQualifiedName~CompatibleNative|FullyQualifiedName~TimedOutPrimary|FullyQualifiedName~ExpiredOverallDeadline|FullyQualifiedName~TraceId"`
Expected: each case executes; assertion failures point to candidate filtering, timeout propagation, or trace selection rather than a missing test dependency.

- [ ] **Step 3: Add deterministic timeout and trace wiring**

Configure primary delay above the test’s `LlmRequestTimeout` and keep the total request deadline larger than the fallback budget in the success case. In the deadline-exhaustion case, set the parent request deadline shorter than A’s delay and assert B is never called.

Wrap the endpoint with the existing middleware so `AH-Trace-Id` populates `TraceContext` before the request reaches the orchestrator:

```csharp
var middleware = new RequestTracingMiddleware(
    context => ChatCompletionEndpoint.HandleAsync(context, orchestrator),
    TracingOptions.Default);
context.Request.Headers[TracingOptions.DefaultTraceHeader] = traceId;
await middleware.InvokeAsync(context);
```

Use `RequestTimeoutMiddleware` with `TimeProvider.System` and a short local deadline for the timeout tests. Use a `PreviousChannelCache` for the two-request same-trace case. Seed the prior-channel entry under one trace and issue a request under another trace to prove it does not inherit that entry. Assert exact provider request counts after each response; do not infer stickiness merely from equal response text.

- [ ] **Step 4: Re-run capability, timeout, and trace cases**

Run: `dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~NativeToolRequest|FullyQualifiedName~CompatibleNative|FullyQualifiedName~TimedOutPrimary|FullyQualifiedName~ExpiredOverallDeadline|FullyQualifiedName~TraceId"`
Expected: all 6 cases pass; the deadline test completes under its explicit generous upper bound.

- [ ] **Step 5: Commit routing and deadline cases**

```powershell
git add tests\AxonHub.Api.Tests\LlmTestServerResilienceE2ETests.cs
git commit -m "test: cover gateway routing and timeout resilience"
```

### Task 5: Cover interrupted stream delivery

**Files:**
- Modify: `tests/AxonHub.Api.Tests/LlmTestServerResilienceE2ETests.cs`
- Use: `tests/AxonHub.Api.Tests/SseStreamWriterTests.cs`
- Use: `tests/AxonHub.Api.Tests/StreamLifecycleTests.cs`

**Interfaces:**
- Consumes: Task 1’s admin client and Task 2’s HTTP send helper.
- Produces: an SSE assertion helper that extracts text deltas and checks terminal frames without treating clean EOF as successful completion.

- [ ] **Step 1: Add failing stream interruption cases**

Add:

1. `EarlyCleanEndAfterFirstFrameDoesNotSpliceFallback`
2. `EarlyCleanEndAfterAllContentStillFailsTerminalCheck`

Configure an OpenAI chat response with explicit `streamChunks` and:

```csharp
streamDelivery = new
{
    mode = "EarlyCleanEnd",
    afterContentFrames = 1,
}
```

In the first case, configure B with a distinct marker answer and assert that only A’s emitted prefix appears, B has zero requests, and the result does not append B’s text. In the second case, deliver all text frames but end before the protocol completion marker; assert the stream is recorded/recognized as incomplete despite all text being present.

- [ ] **Step 2: Run the stream cases and confirm failure**

Run: `dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~EarlyCleanEnd"`
Expected: the cases fail until fixture configuration, SSE parsing, and terminal assertions are wired.

- [ ] **Step 3: Assert gateway and upstream terminal evidence**

Use the LlmTestServer request journal to inspect the configured delivery fault and terminal observation. Parse AxonHub’s SSE body and assert the emitted prefix and terminal-marker state separately. Never concatenate output across providers once a client-visible frame has been written.

- [ ] **Step 4: Re-run the stream cases**

Run: `dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~EarlyCleanEnd"`
Expected: both cases pass, and no assertion requires an invented complete answer or a client-side tool action.

- [ ] **Step 5: Commit the interrupted-stream cases**

```powershell
git add tests\AxonHub.Api.Tests\LlmTestServerResilienceE2ETests.cs
git commit -m "test: cover interrupted gateway streams"
```

### Task 6: Document and run the complete resilience gate

**Files:**
- Modify: `tests/AxonHub.Api.Tests/LlmTestServerE2ETests.cs`
- Verify: `tests/AxonHub.Api.Tests/LlmTestServerResilienceE2ETests.cs`
- Verify: `tests/AxonHub.Api.Tests/LlmTestServerProviderMatrixTests.cs`

- [ ] **Step 1: Document the suite’s local-only run command**

Update the existing E2E XML remarks with the resilience filter and clarify that `test-key` is a fake local fixture credential. Keep the existing server startup instructions unchanged.

- [ ] **Step 2: Run the resilience suite with LlmTestServer**

Run in PowerShell with the local test server already running:

```powershell
$env:AXONHUB_E2E_LLM_TEST_SERVER_URL = 'http://localhost:8080'
dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~LlmTestServerResilienceE2ETests"
```

Expected: 22 logical cases pass, including the 401/403 theory rows.

- [ ] **Step 3: Run all LlmTestServer E2E cases**

Run: `dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~LlmTestServerE2E"`
Expected: all existing and new E2E cases pass with a nonzero executed-test count.

- [ ] **Step 4: Verify opt-in skip behavior**

Run after removing the URL from the current PowerShell environment:

```powershell
Remove-Item Env:AXONHUB_E2E_LLM_TEST_SERVER_URL -ErrorAction SilentlyContinue
dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~LlmTestServerResilienceE2ETests"
```

Expected: all resilience cases are reported skipped and none fail due to a missing external process.

- [ ] **Step 5: Commit the documentation and final suite**

```powershell
git add tests\AxonHub.Api.Tests\LlmTestServerE2ETests.cs tests\AxonHub.Api.Tests\LlmTestServerResilienceE2ETests.cs
git commit -m "test: document gateway resilience E2E suite"
```
