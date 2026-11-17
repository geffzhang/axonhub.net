# Stream delivery and fallback terminal tests

## Goal

Close the remaining gateway resilience E2E gaps for LlmTestServer's
`DelayThenContinue` and `AbortConnection` stream-delivery faults, and verify the
client-visible terminal error when a three-channel fallback chain is exhausted.
Keep the change test-only and use the existing local LlmTestServer harness.

## Test design

Extend `LlmTestServerResilienceE2ETests` with four cases:

1. A delayed stream resumes within its request deadline. Assert complete text,
   normal finish and `[DONE]`, no fallback request, and upstream delivery-plan
   evidence.
2. A delayed stream that crosses an explicit request deadline ends as a timeout,
   not a successful completion. Assert that the first delivered prefix is not
   extended and inspect upstream delivery evidence.
3. An aborted upstream connection after the gateway receives the first content
   frame produces the OpenAI SSE transport-error event classified as
   `upstream_stream_interrupted`, without `[DONE]` or fallback. If the connection
   resets before the gateway receives content, a budgeted fallback may instead
   complete normally. Assert that the upstream journal records an applied
   `AbortConnection`, but use gateway-recorded upstream frames to select the
   expected outcome: a server-side flush does not acknowledge client receipt.
   Cover both receipt boundaries independently with deterministic endpoint tests.
4. Three distinct channels each return one distinguishable retryable 5xx.
   Assert all three were attempted once and the client receives channel C's
   final upstream status, error code, and message.

Reuse the current orchestrator, candidate selector, local server admin client,
and response-evidence helpers. Add only the channel/configuration helper support
needed for a third candidate. Do not change production behavior or introduce a
full Kestrel/reverse-proxy keepalive test in this scope.

## Validation

Run the new cases against a local LlmTestServer with
`AXONHUB_E2E_LLM_TEST_SERVER_URL` set, filtering on
`LlmTestServerResilienceE2ETests`. Also verify the suite's normal opt-in/skip
behavior when the environment variable is absent.

## Reference

- [LlmTestServer stream-delivery fault contract](https://raw.githubusercontent.com/managedcode/LlmTestServer/main/docs/Features/StreamDeliveryFaults.md)
