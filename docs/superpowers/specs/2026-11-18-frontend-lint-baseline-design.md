# Frontend Lint Baseline Repair Design

## Context

The frontend lint backlog was explicitly deferred as a separate closeout item. On branch `chore/porting-closeout`, at commit `802ed391a3b186a72a19f274004fb2a4800fa387`, a fresh `pnpm lint` run exited with code 1 and reported 252 diagnostics across 130 files: 159 errors and 93 warnings.

The current ESLint configuration is in `frontend/eslint.config.js`. Relevant existing rules include React Hooks recommended rules, `no-console` as an error, `@typescript-eslint/no-unused-vars` as an error, and `react-refresh/only-export-components` as a warning. Top diagnostic counts were:

| Rule | Count |
| --- | ---: |
| `@typescript-eslint/no-unused-vars` | 110 |
| `react-refresh/only-export-components` | 48 |
| `react-hooks/exhaustive-deps` | 45 |
| `no-empty` | 22 |
| `@typescript-eslint/ban-ts-comment` | 10 |
| `no-console` | 9 |
| Other rules combined | 8 |

## Goal

Clear all 252 baseline diagnostics without changing or disabling ESLint rules, broadly suppressing files, or changing application behavior. The final acceptance target is a full `pnpm lint` run with zero errors and zero warnings.

## Approved approach

Make narrow, rule- and risk-oriented batches rather than sweeping changes. Start with behavior-sensitive findings (Hooks dependency/order violations, exception handling, empty catches, and logging), then clear unused symbols and TypeScript directives, and finish React Refresh export-boundary warnings. Use targeted ESLint runs after each batch, and add or run tests when a proposed fix can affect behavior.

Only files reported by the fresh lint baseline are initially in scope. `eslint --fix` may be considered for an individual, clearly understood diagnostic, but broad repository-wide autofix is excluded; every applied automated edit must be reviewed as a normal code change.

## Constraints and non-goals

- Preserve the existing ESLint configuration and rule severities.
- Do not add ignores, disable rules, rename code merely to silence a rule, or hide diagnostics with baseline tooling.
- Keep runtime behavior unchanged. In particular, do not alter Hook execution order, dependency semantics, error propagation, or user-visible logging without a separately justified and tested change.
- Do not expand into unrelated refactoring or unrelated lint/format tooling.
- If an existing diagnostic requires an intentional behavior change or a broad architectural refactor to resolve, stop and request a scope decision before proceeding.

## Execution and verification

1. Establish the exact diagnostics and touched files from the current `pnpm lint` output.
2. Resolve findings in small risk-oriented batches; run targeted ESLint and relevant tests for each batch.
3. Run the frontend unit suite and production build after the lint backlog is clean.
4. Run full `pnpm lint` and require exit code zero with zero errors and zero warnings.
5. Record the final diagnostics count and validation results in the closeout checklist and acceptance ledger.

## Acceptance

- `pnpm lint` completes with 0 errors and 0 warnings under the unchanged existing configuration.
- Frontend unit tests pass; production build succeeds.
- All changed behavior, if any was separately approved, is covered by focused tests.
- No unrelated files or broad suppressions were introduced.
