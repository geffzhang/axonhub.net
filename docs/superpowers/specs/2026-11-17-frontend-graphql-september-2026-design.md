# Frontend GraphQL September 2026 Compatibility

## Status

Approved design, 2026-11-17.

## Context

The admin API now uses the GraphQL over HTTP September 2026 Edition. Successful GraphQL responses may use `application/graphql-response+json`; execution results containing errors can use HTTP 294, and request errors can use HTTP 422. The frontend's shared client currently accepts only `application/json`, so it rejects the new response media type before parsing the GraphQL body. Multipart backup restore sends GraphQL directly and must advertise the same response preferences.

## Design

Keep the existing fetch-based client. Define one shared `Accept` value preferring `application/graphql-response+json` while retaining `application/json` as a compatibility fallback. The shared JSON response parser will compare the normalized media type (ignoring parameters) against those two supported values.

Preserve the existing auth behavior for HTTP 401/403. Parse GraphQL responses for both successful and non-success HTTP statuses so 294 execution results continue through the existing `errors` handling, while 422 request errors retain the GraphQL error message and HTTP status. Do not treat 294 as a transport failure: it is a 2xx response with a GraphQL error result.

The multipart restore request will send the shared `Accept` value but will not set `Content-Type`; the browser must continue generating the multipart boundary. It will keep reading the GraphQL response JSON and reporting its first error.

## Scope

- Update the shared request headers and response media-type check in `frontend/src/gql/graphql.ts`.
- Apply the shared `Accept` value to the multipart restore request in `frontend/src/features/system/data/system.ts`.
- Add focused tests in `frontend/src/gql/graphql.test.mjs` for preferred negotiation, legacy JSON compatibility, HTTP 294 execution errors, and HTTP 422 request errors.

Replacing the GraphQL transport library, changing UI error presentation, and changing backend behavior are out of scope.

## Validation

- Run the GraphQL client Node tests.
- Run the frontend production build and lint.
