# Frontend Lint Baseline Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clear every diagnostic in the frontend ESLint baseline while preserving existing rules and runtime behavior.

**Architecture:** Keep `frontend/eslint.config.js` unchanged and repair the reported sources in small rule- and risk-oriented batches. Handle Hooks and exception findings before mechanical unused-symbol cleanup, then split React Refresh mixed exports only where necessary. Validate each batch with targeted ESLint, then run the frontend unit suite, production build, and the complete lint gate.

**Tech Stack:** React, TypeScript, ESLint 9 flat config, pnpm, Node.js test runner, Vite.

## Global Constraints

- Preserve the existing ESLint configuration and rule severities.
- Do not add ignores, disable rules, rename code merely to silence a rule, or hide diagnostics with baseline tooling.
- Keep runtime behavior unchanged. In particular, do not alter Hook execution order, dependency semantics, error propagation, or user-visible logging without a separately justified and tested change.
- Do not expand into unrelated refactoring or unrelated lint/format tooling.
- If an existing diagnostic requires an intentional behavior change or a broad architectural refactor to resolve, stop and request a scope decision before proceeding.

---

## Baseline and commands

The fresh baseline was collected from `frontend` on `chore/porting-closeout` at `802ed391a3b186a72a19f274004fb2a4800fa387`; only documentation commits followed, so the frontend source tree is unchanged. `pnpm lint` exited 1 with 252 ESLint diagnostics across 130 files: 159 errors and 93 warnings.

Run commands from `frontend`:

```powershell
pnpm exec eslint src\path\to\file.tsx
pnpm test:unit
pnpm build
pnpm lint
```

The ESLint counts by rule are: `@typescript-eslint/no-unused-vars` 110, `react-refresh/only-export-components` 48, `react-hooks/exhaustive-deps` 45, `no-empty` 22, `@typescript-eslint/ban-ts-comment` 10, `no-console` 9, `no-useless-escape` 3, `no-useless-catch` 2, `react-hooks/rules-of-hooks` 2, and `@tanstack/query/no-unstable-deps` 1. The pnpm notice about the existing `pnpm.overrides` key is not an ESLint diagnostic and is outside this plan.

## File responsibilities

- `frontend/src/features/threads/components/threads-columns.tsx` and `traces-columns.tsx`: table-column cell renderers and navigation.
- Other files named in Tasks 2–3: component and hook logic that ESLint flags for dependency correctness.
- Files named in Tasks 4–5: dialog/store error paths, unused bindings, comments, and diagnostic logging.
- Files named in Tasks 6–7: component and context export boundaries used by React Refresh.
- `frontend/src/**/*.test.mjs`: existing frontend unit tests; add a focused test only if an approved fix changes observable behavior.

## Task 1: Remove Hook calls from table cell callbacks

**Files:**
- Modify: `frontend/src/features/threads/components/threads-columns.tsx`
- Modify: `frontend/src/features/traces/components/traces-columns.tsx`

- [ ] Inspect each `cell` callback that calls `useCallback`; it is a TanStack renderer, not a React component or custom hook.
- [ ] Replace the hook call with an ordinary event callback that closes over the row ID and existing `navigateWithSearch` value. For the thread cell, preserve this exact navigation payload:

```tsx
onClick={() =>
  navigateWithSearch({
    to: '/project/threads/$threadId',
    params: { threadId: row.original.id },
  })
}
```

For the trace cell, use the existing `/project/traces/$traceId` route and `traceId` parameter. Preserve button markup, row value formatting, sorting, and hiding settings.
- [ ] Remove the React `useCallback` import only if it is unused elsewhere in that file.
- [ ] Run targeted ESLint on both files; expect zero `react-hooks/rules-of-hooks` diagnostics.

## Task 2: Stabilize React Query callback dependencies

**Files:**
- Modify: `frontend/src/components/layout/app-header.tsx`

- [ ] Destructure the stable `mutate` function from the `useMutation` result and call that function from `handleRefresh`; do not put the entire mutation result in the callback dependency array.
- [ ] Keep the existing pending state transitions, `onSettled` behavior, query refetch, and notifications unchanged.
- [ ] Run targeted ESLint; expect zero `@tanstack/query/no-unstable-deps` diagnostics. Run the frontend unit suite if the callback behavior changes.

## Task 3: Correct Hook dependency findings

Fix only the `react-hooks/exhaustive-deps` diagnostics in the files below. For each diagnostic, read the complete hook and the referenced values before editing. Add a genuinely required dependency; if that dependency is unstable, memoize the value at its source or move derivation into the hook callback. Do not suppress a dependency or alter effect timing to silence ESLint.

### Task 3A: Shared components and API key/channel flows

**Files:**
- `frontend/src/components/ai-elements/branch.tsx`
- `frontend/src/components/ai-elements/message.tsx`
- `frontend/src/components/ai-elements/prompt-input.tsx`
- `frontend/src/components/auto-complete-select.tsx`
- `frontend/src/components/data-table-column-order-dialog.tsx`
- `frontend/src/components/price-schedule-editor.tsx`
- `frontend/src/features/analytics/components/dimension-pie-charts.tsx`
- `frontend/src/features/apikeys/components/apikeys-profiles-dialog.tsx`
- `frontend/src/features/apikeys/components/apikeys-table.tsx`
- `frontend/src/features/apikeys/components/apikeys-view-dialog.tsx`
- `frontend/src/features/channels/components/channels-table.tsx`
- `frontend/src/features/channels/hooks/use-device-flow.ts`
- `frontend/src/features/channels/index.tsx`
- `frontend/src/features/errors/not-found-error.tsx`

- [ ] Fix the reported Hook dependencies without changing dialog, table, or device-flow behavior.
- [ ] Run targeted ESLint on these files and `pnpm test:unit`; investigate any changed test behavior rather than updating expected output to match blindly.

### Task 3B: Models, permission, playground, and request details

**Files:**
- `frontend/src/features/models/components/models-association-dialog.tsx`
- `frontend/src/features/models/components/models-batch-create-dialog.tsx`
- `frontend/src/features/models/components/models-table.tsx`
- `frontend/src/features/permission-demo/index.tsx`
- `frontend/src/features/playground/index.tsx`
- `frontend/src/features/requests/components/chunks-dialog.tsx`
- `frontend/src/features/requests/components/request-body-drawer.tsx`
- `frontend/src/features/requests/components/request-detail-content.tsx`
- `frontend/src/features/requests/components/request-detail-page.tsx`

- [ ] Resolve the reported dependencies and unstable expressions at their source. Preserve request selection, streaming, playground, and model-table behavior.
- [ ] Run targeted ESLint and `pnpm test:unit`. If a dependency fix changes when an effect runs or what request it sends, stop and obtain approval before changing that behavior.

### Task 3C: Remaining tables and permission hooks

**Files:**
- `frontend/src/features/proejct-users/components/users-invite-dialog.tsx`
- `frontend/src/features/prompt-protection-rules/components/rules-table.tsx`
- `frontend/src/features/prompts/components/prompts-table.tsx`
- `frontend/src/features/requests/components/requests-table.tsx`
- `frontend/src/features/roles/components/roles-table.tsx`
- `frontend/src/features/threads/components/threads-columns.tsx`
- `frontend/src/features/traces/components/traces-columns.tsx`
- `frontend/src/hooks/useAnimatedList.ts`
- `frontend/src/hooks/useRequestPermissions.ts`
- `frontend/src/hooks/useRoutePermissions.ts`

- [ ] Resolve the remaining reported dependencies, preserving filtering, pagination, animation, and permission checks.
- [ ] Run targeted ESLint and `pnpm test:unit`.

## Task 4: Make empty error paths explicit without changing outcomes

**Files:**
- `frontend/src/features/apikeys/components/apikeys-bulk-archive-dialog.tsx`
- `frontend/src/features/apikeys/components/apikeys-bulk-disable-dialog.tsx`
- `frontend/src/features/apikeys/components/apikeys-bulk-enable-dialog.tsx`
- `frontend/src/features/apikeys/components/apikeys-create-dialog.tsx`
- `frontend/src/features/apikeys/components/apikeys-edit-dialog.tsx`
- `frontend/src/features/apikeys/components/apikeys-status-dialog.tsx`
- `frontend/src/features/channels/components/channels-bulk-archive-dialog.tsx`
- `frontend/src/features/channels/components/channels-bulk-delete-dialog.tsx`
- `frontend/src/features/channels/components/channels-bulk-disable-dialog.tsx`
- `frontend/src/features/channels/components/channels-bulk-enable-dialog.tsx`
- `frontend/src/features/channels/components/channels-delete-dialog.tsx`
- `frontend/src/features/channels/components/channels-status-dialog.tsx`
- `frontend/src/features/models/components/models-bulk-disable-dialog.tsx`
- `frontend/src/features/models/components/models-bulk-enable-dialog.tsx`
- `frontend/src/features/models/components/models-delete-dialog.tsx`
- `frontend/src/features/models/components/models-status-dialog.tsx`
- `frontend/src/stores/authStore.ts`
- `frontend/src/stores/projectStore.ts`
- `frontend/src/features/data-storages/components/create-data-storage-dialog.tsx`
- `frontend/src/features/data-storages/components/edit-data-storage-dialog.tsx`

- [ ] For each empty catch, inspect whether a mutation hook/global handler already surfaces the error, or whether the catch intentionally preserves a storage fallback. Where it is an intentional no-op after an existing error handler, add a concise explanation inside the catch; where a fallback already exists, preserve it. Do not silently discard a newly unhandled error, add a toast, or change propagation without approval.
- [ ] For the two `no-useless-catch` findings, remove only try/catch wrappers that merely rethrow the same error. Preserve `finally` behavior and all surrounding state updates.
- [ ] Run targeted ESLint and `pnpm test:unit`; confirm there are no `no-empty` or `no-useless-catch` findings and no changed error outcomes.

## Task 5: Remove unused bindings in shared and foundational modules

**Files:**
- `frontend/src/components/ai-elements/prompt-input.tsx`
- `frontend/src/components/language-switch.tsx`
- `frontend/src/components/price-schedule-editor.tsx`
- `frontend/src/components/project-guard.tsx`
- `frontend/src/features/analytics/components/dimension-detail-table.tsx`
- `frontend/src/features/auth/forgot-password/components/forgot-password-form.tsx`
- `frontend/src/features/dashboard/components/channel-performance-stats.tsx`
- `frontend/src/features/dashboard/components/performance-chart.tsx`
- `frontend/src/features/errors/not-found-error.tsx`
- `frontend/src/features/permission-demo/index.tsx`
- `frontend/src/features/playground/index.tsx`
- `frontend/src/hooks/use-error-handler.ts`
- `frontend/src/hooks/useLanguage.ts`
- `frontend/src/routes/__root.tsx`
- `frontend/src/stores/authStore.ts`
- `frontend/src/stores/projectStore.ts`

- [ ] Remove imports and local bindings only when they have no reads. For an intentionally unused positional argument, use the configured `_` prefix only after confirming the argument position is part of a callback/API contract.
- [ ] Run targeted ESLint on these files and `pnpm test:unit`.

## Task 6: Remove unused bindings in API key and channel modules

**Files:**
- `frontend/src/features/apikeys/components/apikeys-bulk-archive-dialog.tsx`
- `frontend/src/features/apikeys/components/apikeys-bulk-disable-dialog.tsx`
- `frontend/src/features/apikeys/components/apikeys-bulk-enable-dialog.tsx`
- `frontend/src/features/apikeys/components/apikeys-create-dialog.tsx`
- `frontend/src/features/apikeys/components/apikeys-edit-dialog.tsx`
- `frontend/src/features/apikeys/components/apikeys-profile-templates-dialog.tsx`
- `frontend/src/features/apikeys/components/apikeys-status-dialog.tsx`
- `frontend/src/features/channels/components/channel-expanded-row.tsx`
- `frontend/src/features/channels/components/channels-bulk-apply-template-dialog.tsx`
- `frontend/src/features/channels/components/channels-bulk-archive-dialog.tsx`
- `frontend/src/features/channels/components/channels-bulk-clear-template-dialog.tsx`
- `frontend/src/features/channels/components/channels-bulk-delete-dialog.tsx`
- `frontend/src/features/channels/components/channels-bulk-disable-dialog.tsx`
- `frontend/src/features/channels/components/channels-bulk-enable-dialog.tsx`
- `frontend/src/features/channels/components/channels-bulk-import-dialog.tsx`
- `frontend/src/features/channels/components/channels-bulk-ordering-dialog.tsx`
- `frontend/src/features/channels/components/channels-delete-dialog.tsx`
- `frontend/src/features/channels/components/channels-error-resolved-dialog.tsx`
- `frontend/src/features/channels/components/channels-proxy-dialog.tsx`
- `frontend/src/features/channels/components/channels-status-dialog.tsx`
- `frontend/src/features/channels/components/channels-test-dialog.tsx`
- `frontend/src/features/channels/components/channels-test-history-drawer.tsx`
- `frontend/src/features/channels/components/data-table-toolbar.tsx`

- [ ] Remove truly unused imports/locals and preserve every handler argument position used by React or TanStack callbacks.
- [ ] Run targeted ESLint and `pnpm test:unit`; verify the dialogs still render and submit their existing values.

## Task 7: Remove unused bindings in models and project administration

**Files:**
- `frontend/src/features/models/components/models-action-dialog.tsx`
- `frontend/src/features/models/components/models-association-dialog.tsx`
- `frontend/src/features/models/components/models-bulk-disable-dialog.tsx`
- `frontend/src/features/models/components/models-bulk-enable-dialog.tsx`
- `frontend/src/features/models/components/models-delete-dialog.tsx`
- `frontend/src/features/models/components/models-status-dialog.tsx`
- `frontend/src/features/models/components/models-unassociated-dialog.tsx`
- `frontend/src/features/proejct-users/components/data-table-toolbar.tsx`
- `frontend/src/features/proejct-users/components/project-user-action-dialog.tsx`
- `frontend/src/features/proejct-users/components/users-columns.tsx`
- `frontend/src/features/proejct-users/components/users-delete-dialog.tsx`
- `frontend/src/features/proejct-users/data/users.ts`
- `frontend/src/features/project-roles/components/roles-action-dialog.tsx`
- `frontend/src/features/projects/components/projects-action-dialog.tsx`
- `frontend/src/features/prompts/components/prompts-columns.tsx`

- [ ] Remove only unused symbols, preserving form fields, role/project mutations, and column output.
- [ ] Run targeted ESLint and `pnpm test:unit`.

## Task 8: Remove unused bindings in request, settings, and identity modules

**Files:**
- `frontend/src/features/requests/components/chunk-item.tsx`
- `frontend/src/features/requests/components/request-detail-content.tsx`
- `frontend/src/features/requests/components/request-detail-page.tsx`
- `frontend/src/features/roles/data/roles.ts`
- `frontend/src/features/settings/index.tsx`
- `frontend/src/features/system/components/proxy-presets-settings.tsx`
- `frontend/src/features/threads/components/thread-detail-page.tsx`
- `frontend/src/features/traces/components/data-table-toolbar.tsx`
- `frontend/src/features/traces/components/traces-table.tsx`
- `frontend/src/features/traces/data/schema.ts`
- `frontend/src/features/users/components/data-table-toolbar.tsx`
- `frontend/src/features/users/components/users-action-dialog.tsx`
- `frontend/src/features/users/components/users-add-to-project-dialog.tsx`
- `frontend/src/features/users/components/users-change-password-dialog.tsx`
- `frontend/src/features/users/components/users-delete-dialog.tsx`
- `frontend/src/features/users/components/users-status-dialog.tsx`

- [ ] Remove unused symbols without changing request rendering, roles, settings, permissions, or user mutations.
- [ ] Run targeted ESLint and `pnpm test:unit`.

## Task 9: Resolve console, TypeScript directive, and escape diagnostics

**Files:**
- `frontend/src/components/initialization-guard.tsx`
- `frontend/src/components/json-tree-view.tsx`
- `frontend/src/features/models/components/models-action-dialog.tsx`
- `frontend/src/features/models/components/models-batch-create-dialog.tsx`
- `frontend/src/features/models/components/models-columns.tsx`
- `frontend/src/features/models/data/providers.ts`
- `frontend/src/features/requests/components/chunks-dialog.tsx`
- `frontend/src/features/requests/components/curl-preview-dialog.tsx`
- `frontend/src/features/requests/components/request-detail-content.tsx`
- `frontend/src/features/requests/components/response-flow.tsx`
- `frontend/src/features/settings/security/security-form.tsx`
- `frontend/src/features/system/data/timezones.ts`
- `frontend/src/hooks/use-version-check.ts`
- `frontend/src/routes/oauth/oidc/idp-callback.tsx`

- [ ] Replace `@ts-ignore` with `@ts-expect-error` only where the following line is intentionally expected to remain a type error; otherwise fix the underlying type. Keep a concise reason for each retained directive.
- [ ] Remove debug-only `console` calls. If a call is the only user-visible error path, stop and request approval before replacing it with a different behavior.
- [ ] Remove only the `no-useless-escape` backslashes that do not alter the regular expression/string meaning.
- [ ] Run targeted ESLint and relevant frontend tests.

## Task 10: Split shared React Refresh exports

**Files:**
- `frontend/src/components/ai-elements/code-block.tsx`
- `frontend/src/components/ai-elements/edge.tsx`
- `frontend/src/components/ai-elements/masked-code-block.tsx`
- `frontend/src/components/ai-elements/prompt-input.tsx`
- `frontend/src/components/language-provider.tsx`
- `frontend/src/components/permission-guard.tsx`
- `frontend/src/features/apikeys/components/apikeys-columns.tsx`
- `frontend/src/features/apikeys/components/apikeys-template-transfer-dialog.tsx`
- `frontend/src/features/apikeys/context/apikeys-context.tsx`
- `frontend/src/features/channels/components/channels-action-dialog.tsx`
- `frontend/src/features/channels/components/channels-columns.tsx`
- `frontend/src/features/channels/components/channels-proxy-dialog.tsx`
- `frontend/src/features/channels/utils/error-formatter.tsx`

- [ ] For each warning, identify the non-component export. Move only shared constants, types, or pure utilities into a narrowly scoped adjacent module; preserve import surfaces for application consumers.
- [ ] Do not move components solely to silence warnings when a small helper extraction suffices.
- [ ] Run targeted ESLint, `pnpm test:unit`, and `pnpm build`.

## Task 11: Split feature context and remaining React Refresh exports

**Files:**
- `frontend/src/features/data-storages/context/data-storages-context.tsx`
- `frontend/src/features/models/components/models-columns.tsx`
- `frontend/src/features/models/components/models-table.tsx`
- `frontend/src/features/models/context/models-context.tsx`
- `frontend/src/features/project-roles/context/roles-context.tsx`
- `frontend/src/features/projects/context/projects-context.tsx`
- `frontend/src/features/prompt-protection-rules/components/rules-columns.tsx`
- `frontend/src/features/prompt-protection-rules/context/rules-context.tsx`
- `frontend/src/features/prompts/components/prompts-columns.tsx`
- `frontend/src/features/prompts/context/prompts-context.tsx`
- `frontend/src/features/requests/context/requests-context.tsx`
- `frontend/src/features/roles/context/roles-context.tsx`
- `frontend/src/features/system/context/system-context.tsx`
- `frontend/src/features/threads/components/threads-columns.tsx`
- `frontend/src/features/traces/components/traces-columns.tsx`
- `frontend/src/features/traces/context/traces-context.tsx`

- [ ] Keep component modules limited to component exports where the warning requires it; move context objects, hooks, types, and pure values to adjacent modules as needed.
- [ ] Update all imports and verify provider/hook pairing and table-column behavior remain unchanged.
- [ ] Run targeted ESLint, `pnpm test:unit`, and `pnpm build`.

## Task 12: Final full validation and closeout update

**Files:**
- Modify: `docs/superpowers/plans/2026-11-18-porting-closeout-todo.md`
- Modify: `porting/03-acceptance.md`

- [ ] From `frontend`, run `pnpm lint`; expected ESLint output: 0 errors, 0 warnings, exit code 0.
- [ ] From `frontend`, run `pnpm test:unit`; expected: all executed tests pass, with only the documented timezone-conditional skip in the default timezone.
- [ ] From `frontend`, run `pnpm build`; expected: exit code 0. Existing bundle-size notices are not lint diagnostics and should be recorded, not silently changed here.
- [ ] Record the source commit tested, command results, test totals, and any documented skip in the closeout checklist and acceptance ledger.

## Implementation notes

- Run targeted lint after each task, for example: `pnpm exec eslint src\features\threads\components\threads-columns.tsx src\features\traces\components\traces-columns.tsx`.
- Do not change expected test results merely to make a run green. If a fix changes behavior, stop and request approval as required by the approved design.
- Commit each independently verified batch with the repository's required Copilot coauthor trailer; do not amend earlier commits.
