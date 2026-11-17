import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import ts from 'typescript';

const sourcePath = join(import.meta.dirname, 'graphql.ts');
const source = readFileSync(sourcePath, 'utf8');
const transpiled = ts
  .transpileModule(source, {
    compilerOptions: {
      module: ts.ModuleKind.ESNext,
      target: ts.ScriptTarget.ES2023,
    },
  })
  .outputText.replaceAll("import { toast } from 'sonner';", 'const toast = { error() {} };')
  .replaceAll("import { ensureFreshAccessToken } from '@/lib/auth-session';", 'const ensureFreshAccessToken = async () => "";')
  .replaceAll(
    "import { getTokenFromStorage, removeTokenFromStorage } from '@/stores/authStore';",
    'const getTokenFromStorage = () => ""; const removeTokenFromStorage = () => {};'
  )
  .replaceAll("import i18n from '@/lib/i18n';", 'const i18n = { t: (key) => key };');

const moduleUrl = `data:text/javascript;base64,${Buffer.from(transpiled).toString('base64')}`;
const {
  GRAPHQL_ACCEPT_HEADER,
  GraphQLRequestError,
  graphqlRequest,
  isUnauthorizedGraphQLError,
} = await import(moduleUrl);

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

test('does not classify upstream unauthorized provider failures as login expiration', () => {
  const error = {
    message: 'model fetch returned error: failed to fetch models: GET - https://provider.example/v1/models with status 401 Unauthorized',
    path: ['syncChannelModels'],
  };

  assert.equal(isUnauthorizedGraphQLError(error), false);
});

test('classifies explicit GraphQL authentication codes as login expiration', () => {
  assert.equal(isUnauthorizedGraphQLError({ extensions: { code: 'UNAUTHENTICATED' } }), true);
});

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
