# Stream Delivery and Fallback Terminal Tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add E2E coverage for delayed stream continuation, aborted stream transport classification, and the terminal error from an exhausted three-channel fallback chain.

**Architecture:** Extend the existing `LlmTestServerResilienceE2ETests` fixture-driven integration suite. Configure deterministic provider responses and candidate order through the local LlmTestServer admin API, then assert client-visible SSE/JSON and exact upstream request evidence. Do not change production code.

**Tech Stack:** .NET, xUnit, ASP.NET Core `DefaultHttpContext`, local LlmTestServer admin API.

## Global Constraints

- Keep the change test-only and use the existing local LlmTestServer harness.
- Do not change production behavior or introduce a full Kestrel/reverse-proxy keepalive test in this scope.
- Keep provider calls local and use the existing fake `test-key`.

---

## File map

- Modify `tests/AxonHub.Api.Tests/LlmTestServerResilienceE2ETests.cs`: add delivery-mode constants and four focused E2E cases; extend only the stream-evidence parser and fixture configuration needed by those cases.

## Task 1: Cover delayed stream continuation and deadline

**Files:**
- Modify: `tests/AxonHub.Api.Tests/LlmTestServerResilienceE2ETests.cs`

**Interfaces:**
- Consumes: `ConfigureChannelsAsync`, `SendSelectedAsync`, `RequestBody`, `InspectOpenAiStream`, and `FixedCandidateSelector`.
- Produces: `DelayThenContinueCompletesWithinRequestDeadline` and `DelayThenContinueCrossingRequestDeadlineEndsAsTimeout`.

- [ ] **Step 1: Add the delay-mode constant and the two E2E cases**

Add `private const int LlmTestServerDelayThenContinueMode = 2;`. The success case configures two chunks and a 500 ms pause after the first frame, sends through the primary and fallback candidates with a generous 10-second deadline, and asserts complete text, finish reason, `[DONE]`, zero fallback requests, and upstream mode/applied/frame evidence. The deadline case uses the same two-chunk shape, a 5,000 ms delivery pause, and a 2-second request deadline; assert the first chunk only, an SSE error event, no finish reason or `[DONE]`, zero fallback requests, and the applied delay mode in the request journal.

The configured primary response in both cases must have this shape:

```csharp
new
{
    content = prefix + remainder,
    streamChunks = new[] { prefix, remainder },
    streamDelivery = new
    {
        mode = LlmTestServerDelayThenContinueMode,
        afterContentFrames = 1,
        delayMilliseconds = delayMilliseconds,
    },
}
```

For each case use `ConfigureChannelsAsync(model, primaryModel, fallbackModel, primaryResponse, new { content = "B fallback marker." })`, then call `SendSelectedAsync` with `RequestBody(model, stream: true)`, `CreateRetryPolicy(maxChannelRetries: 1)`, `[primary, fallback]`, and a `FixedCandidateSelector` ordered at priorities 0 and 1. Pass `requestDeadline` as 10 seconds for the continuation case and 2 seconds for the deadline case.

- [ ] **Step 2: Run only the delay cases**

Run:

```powershell
$env:AXONHUB_E2E_LLM_TEST_SERVER_URL = 'http://localhost:8080'
dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~DelayThenContinue"
```

Expected: both cases execute against the local server. These are coverage additions, so they may pass on the first run; retain their assertions as the regression boundary even if the current gateway already satisfies them.

- [ ] **Step 3: Commit the delayed-stream cases**

```powershell
git add tests\AxonHub.Api.Tests\LlmTestServerResilienceE2ETests.cs
git commit -m "test: cover delayed stream continuation"
```

## Task 2: Cover aborted-stream transport classification

**Files:**
- Modify: `tests/AxonHub.Api.Tests/LlmTestServerResilienceE2ETests.cs`

**Interfaces:**
- Consumes: the task's candidate/configuration pattern and `InspectOpenAiStream`.
- Produces: `AbortConnectionAfterFirstFrameClassifiesTransportFailure` and an `ErrorCode` field on `OpenAiStreamEvidence`.

- [ ] **Step 1: Add the abort case and parse the SSE error code**

Add `private const int LlmTestServerAbortConnectionMode = 3;`. Configure the primary response with `content = prefix + remainder`, `streamChunks = new[] { prefix, remainder }`, and `streamDelivery = new { mode = LlmTestServerAbortConnectionMode, afterContentFrames = 1 }`; configure fallback with distinct marker text. Send a streaming request with one channel retry available.

Assert HTTP 200 after stream headers have started, the emitted text equals only `prefix`, `HasErrorEvent` is true, `ErrorCode` equals `UpstreamTransportErrors.StreamInterruptedCode`, and both `HasDoneMarker` and `HasFinishReason` are false. Assert one primary request, zero fallback requests, and journal evidence that the abort mode was applied after one emitted content frame with no terminal marker.

Extend `InspectOpenAiStream` to capture `error.code` when an SSE JSON payload contains an `error` object. Add `string? ErrorCode` as the final positional field of `OpenAiStreamEvidence`, and pass the captured value in its return statement. Existing clean-end cases should continue to report a null `ErrorCode`.

Use this extraction inside the existing nonempty JSON-payload branch, after parsing `chunk`:

```csharp
if (chunk.RootElement.TryGetProperty("error", out var error)
    && error.TryGetProperty("code", out var code)
    && code.ValueKind == JsonValueKind.String)
{
    errorCode = code.GetString();
}
```

Initialize `string? errorCode = null;` next to the other evidence flags, then return `new OpenAiStreamEvidence(text.ToString(), hasDoneMarker, hasFinishReason, hasErrorEvent, errorCode)`.

- [ ] **Step 2: Run the abort case**

Run:

```powershell
$env:AXONHUB_E2E_LLM_TEST_SERVER_URL = 'http://localhost:8080'
dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~AbortConnectionAfterFirstFrame"
```

Expected: the case observes `upstream_stream_interrupted`, not a clean EOF or a successful terminal marker, and the fallback provider receives no request.

- [ ] **Step 3: Commit the abort-classification case**

```powershell
git add tests\AxonHub.Api.Tests\LlmTestServerResilienceE2ETests.cs
git commit -m "test: classify aborted upstream streams"
```

## Task 3: Cover three-channel exhaustion terminal semantics

**Files:**
- Modify: `tests/AxonHub.Api.Tests/LlmTestServerResilienceE2ETests.cs`

**Interfaces:**
- Consumes: `NewChannel`, `Candidate`, `SendSelectedAsync`, `ProviderError`, and `AssertPublicError`.
- Produces: `ThreeChannelFailureReturnsLastUpstreamError` and configuration for three independently identifiable local channel model IDs.

- [ ] **Step 1: Add the third channel and a three-model fixture configurator**

Add `private const long TertiaryChannelId = 9983;`. Add this helper signature:

```csharp
private static async Task ConfigureThreeChannelsAsync(
    string requestedModel,
    string primaryModel,
    string fallbackModel,
    string tertiaryModel,
    object primaryResponse,
    object fallbackResponse,
    object tertiaryResponse)
```

The helper must reset the admin fixture and configure one requested chat model, the three provider chat models, and one `chatScenarios` entry per provider model using the same response queue conventions as `ConfigureChannelsAsync`.

Create channel C on the existing OpenAI local route, using `tertiaryModel` as its distinct upstream model ID:

```csharp
NewChannel(
    TertiaryChannelId,
    model,
    "openai",
    ServerUrl + "/openai/v1",
    orderingWeight: 1)
```

Since A and C share the `openai` route, assert two OpenAI 503 requests and inspect the request journal to confirm both `primaryModel` and `tertiaryModel` were requested. The `groq` route must have exactly one 503 request.

- [ ] **Step 2: Add the all-failed chain case**

Configure A, B, and C with 503 responses whose codes/messages differ; use `ProviderError(503, "channel_a_failure", "A failed")`, `ProviderError(503, "channel_b_failure", "B failed")`, and `ProviderError(503, "channel_c_failure", "C final failure")`. Build a `FixedCandidateSelector` with priorities 0, 1, and 2, and call `SendSelectedAsync` with `CreateRetryPolicy(maxChannelRetries: 2)`.

Assert the client receives HTTP 503 and C's final error code and message; explicitly assert A's and B's unique codes are absent. Assert `_recorder.RequestChannels` equals `[PrimaryChannelId, FallbackChannelId, TertiaryChannelId]`, and each provider route has exactly one request with status 503. Do not assert a synthetic aggregate error.

- [ ] **Step 3: Run the three-channel case**

Run:

```powershell
$env:AXONHUB_E2E_LLM_TEST_SERVER_URL = 'http://localhost:8080'
dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~ThreeChannelFailureReturnsLastUpstreamError"
```

Expected: all three upstream attempts occur once, and the client-facing error is C's final provider error.

- [ ] **Step 4: Commit the three-channel case**

```powershell
git add tests\AxonHub.Api.Tests\LlmTestServerResilienceE2ETests.cs
git commit -m "test: cover exhausted three-channel fallback"
```

## Task 4: Run the complete resilience test gate

**Files:**
- Verify: `tests/AxonHub.Api.Tests/LlmTestServerResilienceE2ETests.cs`

- [ ] **Step 1: Run the full resilience class against local LlmTestServer**

Run:

```powershell
$env:AXONHUB_E2E_LLM_TEST_SERVER_URL = 'http://localhost:8080'
dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~LlmTestServerResilienceE2ETests"
```

Expected: all resilience cases pass, including the four added cases.

- [ ] **Step 2: Verify normal opt-in behavior without the server URL**

Run:

```powershell
Remove-Item Env:AXONHUB_E2E_LLM_TEST_SERVER_URL -ErrorAction SilentlyContinue
dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~LlmTestServerResilienceE2ETests"
```

Expected: the LlmTestServer cases are skipped by the existing `LlmTestServerFact` convention, with no external-process dependency.
