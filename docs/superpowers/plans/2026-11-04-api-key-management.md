# API Key Management Implementation Plan

> **Status: superseded by the implementation (2026-11-04, slice §87).** The delivered slice
> keeps this plan's store/service/GraphQL architecture, but three plan-time statements were
> revised during implementation and the unchecked boxes below are historical, not open work:
>
> - `rotateAPIKey` / `ApiKeyManagementService.RotateAsync` **is** implemented (the reference
>   `RotateAPIKey` exists and belongs to this slice); the "do not implement rotation" constraint
>   below is void.
> - `bulkArchiveAPIKeys` sets `status = "archived"` and does **not** write `deleted_at`, matching
>   the reference's plain aggregate update.
> - The endpoint integration tests live in `tests\AxonHub.Api.Tests\AdminApiKeysEndpointTests.cs`
>   (not `ApiKeyManagementGraphQLTests.cs`).
>
> See `porting\01-baseline.md` §87 and `porting\differences.jsonl` C176 for the delivered scope
> and the accepted differences.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the management API Key query and mutations from the frozen AxonHub Go reference into the .NET 10 GraphQL surface with matching behavior and permissions.

**Architecture:** Add a focused `ApiKeyManagementService` that composes the existing `IApiKeyStore`, management identity, and `PermissionValidator`; extend persistence for project-scoped listing, duplicate-name checks, updates, status changes, and bulk soft deletion. Add GraphQL query/input/output types and mutations following existing management connection/GID patterns, then lock persistence, service, and endpoint permissions with tests.

**Tech Stack:** C# 14, .NET 10, ASP.NET Core, HotChocolate, EF Core, Dapper, xUnit, PostgreSQL, frozen Go reference `b8bb3421e1fe946cfc4f0d86e8412098738f2793` (`v1.0.0-beta11`).

## Global Constraints

- Preserve the behavior of the frozen reference commit `b8bb3421e1fe946cfc4f0d86e8412098738f2793`; do not use a drifting upstream checkout.
- Implement create, paginated query, single update, status update, profiles update, rotation, bulk enable/disable, bulk archive, and `node(APIKey)` lookup. Rotation is implemented in the delivered slice (the earlier plan excluded it; the frozen reference's `RotateAPIKey` and the frontend's `rotateAPIKey` both include it).
- Match the reference APIKey GraphQL node, which exposes the raw `key` field; creation returns the newly generated key, while queries remain constrained by the reference privacy policy.
- Enforce `read_api_keys` for reads and `write_api_keys` for writes using the reference's project-aware privacy rules: system scope or project scope, global-owner bypass, project filtering, and personal-key visibility limited to the creator except project/system owners.
- Write tests before production code and verify each new behavior with a focused test run.
- Preserve existing GraphQL GID, cursor pagination, JSON encoding, error wrapping, and soft-delete conventions.

---

## File Map

- `src\AxonHub.Persistence\Stores\ApiKeyStore.cs` — extend `IApiKeyStore` and the Dapper/EF store for live API Key management queries and writes.
- `src\AxonHub.Api\Management\ApiKeyManagementService.cs` — create focused service, input commands, filters, and permission enforcement.
- `src\AxonHub.Api\GraphQL\ApiKeyTypes.cs` — define API Key GraphQL output/input/filter/order types and API Key connection.
- `src\AxonHub.Api\GraphQL\AdminQuery.cs` — expose paginated `apiKeys`.
- `src\AxonHub.Api\GraphQL\AdminMutation.cs` — expose API Key create/update/status/profiles/bulk mutations.
- `src\AxonHub.Api\Program.cs` — register the management service.
- `tests\AxonHub.Persistence.Tests\ApiKeyStoreTests.cs` — validate real PostgreSQL persistence semantics.
- `tests\AxonHub.Api.Tests\ApiKeyManagementServiceTests.cs` — service behavior, permissions, updates, and operation semantics.
- `tests\AxonHub.Api.Tests\AdminApiKeysEndpointTests.cs` — schema/resolver integration and auth/scope restrictions.
- `porting\01-baseline.md` — record slice §87 and exact reference-test coverage.
- `porting\03-acceptance.md` — record focused test/build results and any remaining differences.

## Reference Behavior To Preserve

- `CreateAPIKey` defaults type to `user`, receives a required project ID at the GraphQL boundary (the schema-level default only applies when omitted by non-GraphQL callers), rejects creation of `noauth` type, validates allowed IPs, checks project-admin authorization, and rejects duplicate live names within the project atomically.
- User API Keys are associated with the current user; service-account API Keys use supplied scopes or an empty list and are not permitted to gain scopes the caller lacks.
- `UpdateAPIKey` supports name changes and scopes/allowed-IP clear, replace, and append semantics. Only fields handled by the reference service are applied.
- `UpdateAPIKeyStatus` changes one key's enabled/disabled status; `rotateAPIKey` replaces the key material; bulk enable/disable operate on supplied IDs and bulk archive sets `status = "archived"` (no soft-delete timestamp).
- `UpdateAPIKeyProfiles` persists the supplied profile value using the reference-compatible JSON shape.
- Reads and mutations operate only on live rows and enforce scope/ownership boundaries at the project ID. List queries must apply the same project and personal-key filter as `UserPersonalAPIKeyReadRule`; writes must resolve each target key's project before authorization, including bulk operations.
- GraphQL connection ordering/filtering, GID parsing, pagination validation, and raw-key output must match `ent.resolvers.go`, `ent.graphql`, and the generated APIKey node.

### Task 1: API Key Store Listing And Management Writes

**Files:**
- Modify: `src\AxonHub.Persistence\Stores\ApiKeyStore.cs`
- Test: `tests\AxonHub.Persistence.Tests\ApiKeyStoreTests.cs`

**Interfaces:**
- Produces store methods for project/live-row listing, atomic project/name existence checks, single-record updates, status changes, bulk status updates, and bulk archive.
- Use the existing `ListCursor`, `ListPage<T>`, `SortDirection`, and store error/null conventions from `ProjectStore` and `UserAdminStore`.

- [ ] **Step 1: Add failing PostgreSQL tests**

Add focused tests for: project filter + live-row pagination/order, no visibility of soft-deleted keys, duplicate live name detection within the same project but not across projects, update clear/replace/append persistence, status mutation, and bulk archive setting `status = "archived"` (leaving `deleted_at` untouched) without touching other projects.

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests\AxonHub.Persistence.Tests\AxonHub.Persistence.Tests.csproj -c Release --filter FullyQualifiedName~ApiKeyStoreTests`
Expected: FAIL because the required store methods are not yet defined.

- [ ] **Step 3: Implement store methods**

Extend `IApiKeyStore` with typed query/update commands. Ensure all reads use `deleted_at = 0`, all project ownership predicates are explicit, update timestamps follow existing store conventions, and bulk status updates affect only requested live IDs (the reference `bulkArchiveAPIKeys` writes only `status`, so there is no `deleted_at` change to make). Keep EF Core writes and Dapper reads consistent with the existing store.

- [ ] **Step 4: Run focused persistence tests**

Run: `dotnet test tests\AxonHub.Persistence.Tests\AxonHub.Persistence.Tests.csproj -c Release --filter FullyQualifiedName~ApiKeyStoreTests`
Expected: PASS with all API Key store tests; tests requiring unavailable PostgreSQL infrastructure must report their normal explicit infrastructure failure, not be silently skipped.

### Task 2: Management Service Semantics And Authorization

**Files:**
- Create: `src\AxonHub.Api\Management\ApiKeyManagementService.cs`
- Modify: `src\AxonHub.Api\Management\PermissionValidator.cs`
- Test: `tests\AxonHub.Api.Tests\ApiKeyManagementServiceTests.cs`

**Interfaces:**
- Service methods: `CreateAsync(CreateApiKeyCommand)`, `UpdateAsync(UpdateApiKeyCommand)`, `UpdateStatusAsync(long, string)`, `UpdateProfilesAsync(long, ApiKeyProfilesModel)`, `RotateAsync(long)`, `BulkDisableAsync(IReadOnlyList<long>)`, `BulkEnableAsync(IReadOnlyList<long>)`, `BulkArchiveAsync(IReadOnlyList<long>)`, `ListAsync(ApiKeyListQuery)`, and `GetNodeAsync(long)`.
- Add permission validation matching `UserPersonalAPIKeyReadRule` and `UserProjectScopeWriteRule`: system scopes, project membership scopes, project owner edges, global owner, and personal-key creator restrictions.

- [ ] **Step 1: Write failing service tests**

Cover create defaults and ownership, denied `noauth`, project-admin authorization, allowed-IP invalid input, duplicate names, update clear/replace/append, status operations, archive invisibility, read scope checks, project filtering, personal-key creator visibility, and cross-project single/bulk write denial. Use real service logic and existing fake-store patterns.

- [ ] **Step 2: Run service tests to verify failure**

Run: `dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj -c Release --filter FullyQualifiedName~ApiKeyManagementServiceTests`
Expected: FAIL because the service and permission check are missing.

- [ ] **Step 3: Implement service and validation**

Implement only required command/query types and service behavior. Resolve current user before creation; authorize creation with both `write_users` and `write_roles` project scopes; authorize key mutations with `write_api_keys`; enforce query project visibility and personal-key creator rules with `read_api_keys`. Validate allowed IP entries using existing shared IP parsing/matching primitives; preserve reference defaults, error prefixes, and duplicate-check transaction requirement. Return raw key in the GraphQL node because the reference exposes it.

- [ ] **Step 4: Verify service tests**

Run the same focused command.
Expected: PASS with every new service behavior test and no regression.

### Task 3: GraphQL API Key Types, Query, And Mutations

**Files:**
- Create: `src\AxonHub.Api\GraphQL\ApiKeyTypes.cs`
- Modify: `src\AxonHub.Api\GraphQL\AdminQuery.cs`
- Modify: `src\AxonHub.Api\GraphQL\AdminMutation.cs`
- Modify: `src\AxonHub.Api\Program.cs`
- Test: `tests\AxonHub.Api.Tests\AdminApiKeysEndpointTests.cs`

**Interfaces:**
- GraphQL query `apiKeys(after, first, before, last, orderBy, where)` returns a cursor connection.
- Mutations: `createAPIKey`, `updateAPIKey`, `updateAPIKeyStatus`, `updateAPIKeyProfiles`, `rotateAPIKey`, `bulkDisableAPIKeys`, `bulkEnableAPIKeys`, and `bulkArchiveAPIKeys`.
- Input naming, ID/GID arguments, status enum values, profiles, and API Key output fields must match the frozen GraphQL schema/resolvers.

- [ ] **Step 1: Add failing GraphQL integration tests**

Assert schema names and response shapes; verify create/update/status/profiles and bulk operations; verify read/write scopes and cross-project denial; verify create/query return `key` as the reference node does; verify malformed GIDs and pagination inputs surface errors.

- [ ] **Step 2: Run integration tests to verify failure**

Run: `dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj -c Release --filter FullyQualifiedName~ApiKeyManagementGraphQLTests`
Expected: FAIL due to missing fields and service registration.

- [ ] **Step 3: Implement GraphQL fields and DI**

Follow `AdminQuery` and `AdminMutation` management patterns. Parse/format IDs with `GidValue` and existing `ManagementMapping` helpers; use existing connection builders where type-compatible. Register `ApiKeyManagementService` with `IApiKeyStore`, `IManagementIdentity`, and `PermissionValidator`. Keep resolver behavior thin and preserve the reference's GraphQL node fields including `key`.

- [ ] **Step 4: Verify GraphQL integration tests**

Run the same focused command.
Expected: PASS, including authorized and unauthorized paths.

### Task 4: Porting Ledgers And Final Verification

**Files:**
- Modify: `porting\01-baseline.md`
- Modify: `porting\03-acceptance.md`

- [ ] **Step 1: Record §87 behavior evidence**

Document source methods/tests, implemented test files and counts, operation scope, GraphQL/auth behavior, and the intentionally deferred surfaces (quota usages, token usage stats, profile templates, the `requests` edge, `UpdateAPIKeyScopes`, `CreateLLMAPIKey`, cache invalidation, personal-key user hooks). Rotation is in scope and implemented.

- [ ] **Step 2: Record differences and open gaps**

Add each unresolved reference mismatch to the existing difference catalog with the repository's next available ID, cause, decision, and test evidence. Do not describe an unimplemented path as equivalent.

- [ ] **Step 3: Run focused verification**

Run:

```powershell
dotnet test tests\AxonHub.Persistence.Tests\AxonHub.Persistence.Tests.csproj -c Release --filter FullyQualifiedName~ApiKeyStoreTests
dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj -c Release --filter "FullyQualifiedName~ApiKeyManagement|FullyQualifiedName~AdminApiKeysEndpoint"
```

Expected: both focused suites pass with nonzero executed tests.

- [ ] **Step 4: Build the solution**

Run: `dotnet build AxonHub.slnx -c Release`
Expected: 0 warnings and 0 errors.

- [ ] **Step 5: Record measured totals**

Update acceptance counts and date from actual command output. Leave unrelated historical measurements untouched.
