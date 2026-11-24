# Dashboard token overflow design

## Problem

Dashboard token aggregates are read as 64-bit integers, but several resolver paths narrow them to `int` with checked casts. When an aggregate exceeds `int.MaxValue`, the GraphQL request fails with an arithmetic overflow instead of returning the token statistics.

Affected paths:

- Token totals by channel and model.
- Dashboard token totals by API key.
- Token usage for selected API keys, including Top Models.

The user-usage endpoint already preserves larger counts by returning `long`.

## Design

Use `long` end-to-end for token counts in the affected service records and GraphQL response types. Remove the narrowing casts in those paths while leaving aggregate queries, ordering, authorization, and filtering unchanged. Keep frontend values numeric; no query or UI changes are needed.

Add GraphQL endpoint regression tests that return values above `int.MaxValue` for each affected path, including selected-key Top Models. Assert the exact values are serialized and readable as 64-bit integers.

## Alternatives

- Encode counts as strings: avoids integer-width limits but changes the consumer contract and requires frontend changes.
- Clamp counts to the 32-bit range: prevents the exception but reports inaccurate statistics.

Returning 64-bit integers is consistent with the existing user-usage fix and preserves exact values.

## Non-goals

This change does not alter database aggregation semantics, request-count fields, token calculations, UI formatting, or other analytics endpoints.
