// Test-only name disambiguation:
//
// * AxonHub.Api.Orchestration also declares a narrow GoJson (Quote helpers),
//   while the tests mean the Go-compatible JSON marshaller in AxonHub.Llm.Json.
// * The Orleans packages add a global using for Orleans.Runtime, which also
//   declares WrappedException; the tests mean the pipeline's ported one.
global using GoJson = AxonHub.Llm.Json.GoJson;
global using WrappedException = AxonHub.Llm.Pipeline.WrappedException;
