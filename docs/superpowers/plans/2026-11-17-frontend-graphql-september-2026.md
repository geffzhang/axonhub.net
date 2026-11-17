# Frontend GraphQL September 2026 Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the frontend negotiate and parse September 2026 GraphQL-over-HTTP responses while remaining compatible with legacy JSON responses.

**Architecture:** Keep the existing fetch-based GraphQL client and export one shared `Accept` header value. The shared client will accept both supported GraphQL response media types and keep its existing auth and GraphQL error handling; multipart backup restore will reuse the same `Accept` value without setting a multipart `Content-Type`.

**Tech Stack:** TypeScript, Node.js built-in test runner, TypeScript transpileModule, pnpm, Vite, ESLint.

## Global Constraints

- Keep the existing fetch-based client; do not replace the GraphQL transport library.
- Prefer `application/graphql-response+json` while retaining `application/json` as a compatibility fallback.
- Normalize response media types by ignoring parameters before comparing against the two supported values.
- Parse GraphQL responses for both successful and non-success HTTP statuses; preserve HTTP 294 execution-error handling and HTTP 422 request-error message/status.
- Multipart restore must send the shared `Accept` value and must not set `Content-Type`; the browser must generate its multipart boundary.
- Do not change UI error presentation or backend behavior.

---

### Task 1: Negotiate and parse both GraphQL response media types

**Files:**
- Modify: `frontend/src/gql/graphql.test.mjs`
- Modify: `frontend/src/gql/graphql.ts`

**Interfaces:**
- Produces `GRAPHQL_ACCEPT_HEADER`, exported from `graphql.ts` and used by the shared client and multipart restore.
- `graphqlRequest<T>` remains the existing public request function and continues throwing `GraphQLRequestError` for GraphQL errors.

- [ ] **Step 1: Extend the existing test module imports and add request-test helpers**

Replace the existing dynamic import destructuring in `frontend/src/gql/graphql.test.mjs` with:

```js
const {
  GRAPHQL_ACCEPT_HEADER,
  GraphQLRequestError,
  graphqlRequest,
  isUnauthorizedGraphQLError,
} = await import(moduleUrl);
```

Add these helpers after the dynamic import:

```js
function mockResponse(status, contentType, body) {
  return {
    status,
    ok: status >= 200 && status < 300,
    headers: new Headers({ 'content-type': contentType }),
    json: async () => body,
  };
}

async function withMockedFetch(response, run) {
  const originalFetch = globalThis.fetch;
  let requestInit;

  globalThis.fetch = async (_input, init) => {
    requestInit = init;
    return response;
  };

  try {
    await run(() => requestInit);
  } finally {
    globalThis.fetch = originalFetch;
  }
}
```

- [ ] **Step 2: Add tests for preferred negotiation and both accepted media types**

Append these tests to `graphql.test.mjs`:

```js
test('prefers the September 2026 response media type and parses its parameters', async () => {
  await withMockedFetch(
    mockResponse(200, 'application/graphql-response+json; charset=utf-8', { data: { value: 'ok' } }),
    async (getRequestInit) => {
      assert.deepEqual(await graphqlRequest('query Test { value }'), { value: 'ok' });
      assert.equal(GRAPHQL_ACCEPT_HEADER, 'application/graphql-response+json, application/json;q=0.9');
      assert.equal(getRequestInit()?.headers.Accept, GRAPHQL_ACCEPT_HEADER);
    }
  );
});

test('continues to parse legacy application/json responses', async () => {
  await withMockedFetch(
    mockResponse(200, 'application/json; charset=utf-8', { data: { value: 'legacy' } }),
    async () => {
      assert.deepEqual(await graphqlRequest('query Test { value }'), { value: 'legacy' });
    }
  );
});
```

- [ ] **Step 3: Add tests for HTTP 294 execution errors and HTTP 422 request errors**

Append these tests to `graphql.test.mjs`:

```js
test('handles HTTP 294 execution errors through GraphQL error handling', async () => {
  await withMockedFetch(
    mockResponse(294, 'application/graphql-response+json', {
      data: { value: null },
      errors: [{ message: 'Resolver failed' }],
    }),
    async () => {
      await assert.rejects(graphqlRequest('query Test { value }'), (error) => {
        assert.ok(error instanceof GraphQLRequestError);
        assert.equal(error.message, 'Resolver failed');
        return true;
      });
    }
  );
});

test('preserves the GraphQL message and HTTP status for HTTP 422 request errors', async () => {
  await withMockedFetch(
    mockResponse(422, 'application/graphql-response+json', {
      errors: [{ message: 'Invalid GraphQL request' }],
    }),
    async () => {
      await assert.rejects(graphqlRequest('query Test { value }'), (error) => {
        assert.ok(error instanceof GraphQLRequestError);
        assert.equal(error.message, 'Invalid GraphQL request');
        assert.equal(error.status, 422);
        return true;
      });
    }
  );
});
```

- [ ] **Step 4: Run the GraphQL client tests and confirm the new cases fail before implementation**

Run from `frontend`:

```powershell
pnpm exec node --test src/gql/graphql.test.mjs
```

Expected: the pre-existing authentication-classification tests pass; the new tests fail because the shared client does not send the preferred `Accept` value and rejects `application/graphql-response+json` before parsing.

- [ ] **Step 5: Add the shared Accept value and supported-media-type validation**

In `frontend/src/gql/graphql.ts`, export this header value next to `GRAPHQL_ENDPOINT`:

```ts
export const GRAPHQL_ACCEPT_HEADER = 'application/graphql-response+json, application/json;q=0.9';
```

Add a module-local set for supported response media types:

```ts
const GRAPHQL_RESPONSE_MEDIA_TYPES = new Set([
  'application/graphql-response+json',
  'application/json',
]);
```

Set the default request headers to:

```ts
const headers: Record<string, string> = {
  'Content-Type': 'application/json',
  Accept: GRAPHQL_ACCEPT_HEADER,
};
```

Keep the existing custom-header merge after the defaults, so callers may still override default request headers. Replace the current `contentType.includes('application/json')` check with normalized media-type validation:

```ts
const contentType = response.headers.get('content-type');
const mediaType = contentType?.split(';', 1)[0].trim().toLowerCase();
if (!mediaType || !GRAPHQL_RESPONSE_MEDIA_TYPES.has(mediaType)) {
  throw new GraphQLRequestError('Server returned non-JSON response. Please check the API endpoint.', {
    status: response.status,
  });
}
```

Leave the status/error handling order unchanged: parse JSON first, then handle non-2xx request errors, then process execution-level `result.errors`. This keeps 422 message/status behavior and allows HTTP 294 responses through the existing GraphQL error path.

- [ ] **Step 6: Run the GraphQL client tests and confirm all cases pass**

Run from `frontend`:

```powershell
pnpm exec node --test src/gql/graphql.test.mjs
```

Expected: all six tests pass, including the two existing authentication tests and the four new September 2026 compatibility tests.

- [ ] **Step 7: Commit the shared-client change and focused tests**

```powershell
git add frontend/src/gql/graphql.ts frontend/src/gql/graphql.test.mjs
git commit -m "fix(frontend): accept September 2026 GraphQL responses" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

Expected: the commit contains only `graphql.ts` and `graphql.test.mjs`; unrelated pre-existing worktree changes remain uncommitted.

### Task 2: Apply shared negotiation to multipart backup restore

**Files:**
- Modify: `frontend/src/features/system/data/system.ts`

**Interfaces:**
- Consumes `GRAPHQL_ACCEPT_HEADER` exported by `frontend/src/gql/graphql.ts` in Task 1.
- Multipart restore continues using the existing GraphQL multipart request and response/error handling.

- [ ] **Step 1: Import the shared Accept value**

Update the existing import in `system.ts`:

```ts
import { GRAPHQL_ACCEPT_HEADER, graphqlRequest, GraphQLRequestError } from '@/gql/graphql';
```

- [ ] **Step 2: Send the shared Accept header on multipart restore**

Add `Accept: GRAPHQL_ACCEPT_HEADER` to the headers object in the `fetch('/admin/graphql', ...)` call inside `useRestore`:

```ts
headers: {
  Authorization: token ? `Bearer ${token}` : '',
  Accept: GRAPHQL_ACCEPT_HEADER,
},
```

Do not add a `Content-Type` header; `body: formData` must let the browser supply the multipart boundary.

- [ ] **Step 3: Run all frontend unit tests**

Run from `frontend`:

```powershell
pnpm test:unit
```

Expected: all frontend Node unit tests pass, including the GraphQL September 2026 compatibility tests.

- [ ] **Step 4: Build and lint the frontend**

Run from `frontend`:

```powershell
pnpm build
pnpm lint
```

Expected: Vite production build succeeds and ESLint exits successfully.

- [ ] **Step 5: Commit the multipart restore change**

```powershell
git add frontend/src/features/system/data/system.ts
git commit -m "fix(frontend): negotiate GraphQL media type for restore" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

Expected: the commit contains only `system.ts`; unrelated pre-existing worktree changes remain uncommitted.
