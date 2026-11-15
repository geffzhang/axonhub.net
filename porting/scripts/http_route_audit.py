#!/usr/bin/env python3
"""Audit the port's HTTP route surface against the reference route table.

The reference registers 76 HTTP routes in internal/server/routes.go,
internal/server/codex_routes.go and internal/server/api/oidc.go. The table
below is the normalized (method, path) set read from those files at the frozen
reference commit. The port scan is generic: every MapGet/MapPost/MapPut/
MapPatch/MapDelete/MapGraphQL call with a literal path is collected from
src/AxonHub.Api.

Normalization ignores route parameter names and maps gin wildcards to ASP.NET
catch-alls, because neither changes which requests reach a handler:

    /v1/models/:model          -> /v1/models/*model
    /v1/models/{*model}        -> /v1/models/{**model}

Static fallback (NoRoute / MapFallback), middleware order and authentication
are out of scope; those are covered by the endpoint tests and the existing
difference entries.

Exit code 1 on any missing or extra route.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

# internal/server/routes.go and internal/server/api/oidc.go.
REFERENCE_ROUTES = [
    ("GET", "/favicon"),
    ("GET", "/health"),
    ("GET", "/auth/invitations/:token"),
    ("POST", "/auth/invitations/:token/register"),
    ("GET", "/admin/system/status"),
    ("POST", "/admin/system/initialize"),
    ("POST", "/admin/auth/signin"),
    ("POST", "/admin/auth/refresh"),
    ("GET", "/oauth/oidc/providers"),
    ("GET", "/oauth/oidc/authorize/:provider"),
    ("GET", "/oauth/oidc/callback"),
    ("GET", "/oauth/oidc/callback/:provider"),
    ("POST", "/oauth/oidc/exchange"),
    ("GET", "/admin/playground"),
    ("POST", "/admin/invitations"),
    ("POST", "/admin/codex/oauth/start"),
    ("POST", "/admin/codex/oauth/exchange"),
    ("POST", "/admin/codex/auth/decode"),
    ("POST", "/admin/xai/oauth/start"),
    ("POST", "/admin/xai/oauth/exchange"),
    ("POST", "/admin/xai/oauth/sso"),
    ("POST", "/admin/claudecode/oauth/start"),
    ("POST", "/admin/claudecode/oauth/exchange"),
    ("POST", "/admin/antigravity/oauth/start"),
    ("POST", "/admin/antigravity/oauth/exchange"),
    ("POST", "/admin/copilot/oauth/start"),
    ("POST", "/admin/copilot/oauth/poll"),
    ("GET", "/admin/oidc/link/:provider"),
    ("POST", "/admin/playground/chat"),
    ("GET", "/admin/requests/:request_id/content"),
    ("GET", "/admin/requests/:request_id/preview"),
    ("POST", "/admin/graphql"),
    ("POST", "/openapi/v1/graphql"),
    ("GET", "/openapi/v1/playground"),
    ("POST", "/openapi/webhook/echo"),
    # internal/server/codex_routes.go.
    ("GET", "/codex/v1/user-auth-credential/whoami"),
    ("GET", "/codex/api/codex/accounts/check"),
    ("GET", "/codex/models"),
    ("POST", "/codex/images/generations"),
    ("POST", "/codex/images/edits"),
    ("POST", "/codex/responses"),
    ("GET", "/codex/responses"),
    # routes.go OpenAI group.
    ("GET", "/v1/responses"),
    ("POST", "/v1/chat/completions"),
    ("POST", "/v1/completions"),
    ("POST", "/v1/responses/compact"),
    ("POST", "/v1/responses"),
    ("GET", "/v1/models"),
    ("GET", "/v1/models/*model"),
    ("POST", "/v1/embeddings"),
    ("POST", "/v1/moderations"),
    ("POST", "/v1/alpha/search"),
    ("POST", "/v1/decisions"),
    ("POST", "/v1/images/generations"),
    ("POST", "/v1/images/edits"),
    ("POST", "/v1/videos"),
    ("GET", "/v1/videos/:id"),
    ("DELETE", "/v1/videos/:id"),
    ("POST", "/v1/audio/speech"),
    ("POST", "/v1/audio/transcriptions"),
    ("POST", "/v1/audio/translations"),
    ("POST", "/v1/messages"),
    ("POST", "/v1/rerank"),
    ("POST", "/v1/systemone"),
    ("POST", "/jina/v1/embeddings"),
    ("POST", "/jina/v1/rerank"),
    ("POST", "/typesafe/v1/systemone"),
    ("POST", "/anthropic/v1/messages"),
    ("GET", "/anthropic/v1/models"),
    ("POST", "/doubao/v3/contents/generations/tasks"),
    ("GET", "/doubao/v3/contents/generations/tasks/:id"),
    ("DELETE", "/doubao/v3/contents/generations/tasks/:id"),
    # Both the /gemini/:gemini-api-version and /v1beta alias groups.
    ("POST", "/gemini/:gemini-api-version/models/*action"),
    ("GET", "/gemini/:gemini-api-version/models"),
    ("POST", "/v1beta/models/*action"),
    ("GET", "/v1beta/models"),
]

MAP_METHODS = {
    "MapGet": "GET",
    "MapPost": "POST",
    "MapPut": "PUT",
    "MapPatch": "PATCH",
    "MapDelete": "DELETE",
    "MapGraphQL": "POST",
}

MAPPING = re.compile(
    r"\.(MapGet|MapPost|MapPut|MapPatch|MapDelete|MapMethods|MapGraphQL)"
    r"\(\s*\"([^\"]+)\""
)


def normalize(path: str) -> str:
    """Drop parameter names and unify wildcard spellings."""
    path = re.sub(r"\{\*\*?[A-Za-z_][A-Za-z0-9_-]*\}", "{**}", path)
    path = re.sub(r"/\*[A-Za-z_][A-Za-z0-9_-]*", "/{**}", path)
    path = re.sub(r":([A-Za-z_][A-Za-z0-9_-]*)", "{}", path)
    path = re.sub(r"\{[A-Za-z_][A-Za-z0-9_-]*\}", "{}", path)
    return path


def scan_port(root: Path) -> set[tuple[str, str]]:
    routes: set[tuple[str, str]] = set()
    for path in root.rglob("*.cs"):
        text = path.read_text(encoding="utf-8")
        for match in MAPPING.finditer(text):
            kind, route = match.group(1), match.group(2)
            if kind == "MapMethods":
                # No current MapMethods call carries a literal path; fail loudly
                # if one appears so it cannot silently escape the audit.
                raise SystemExit(f"unsupported MapMethods route in {path}: {route}")
            routes.add((MAP_METHODS[kind], normalize(route)))
    return routes


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--port",
        type=Path,
        default=Path("src/AxonHub.Api"),
        help="port source root (default: src/AxonHub.Api)",
    )
    args = parser.parse_args()

    if not args.port.is_dir():
        print(f"port source root not found: {args.port}", file=sys.stderr)
        return 2

    reference = {(method, normalize(route)) for method, route in REFERENCE_ROUTES}
    port = scan_port(args.port)

    missing = sorted(reference - port)
    extra = sorted(port - reference)

    print(f"reference routes: {len(reference)}")
    print(f"port routes:      {len(port)}")
    for method, route in missing:
        print(f"MISSING {method:6} {route}")
    for method, route in extra:
        print(f"EXTRA   {method:6} {route}")

    if missing or extra:
        print("HTTP route surface differs from the reference.", file=sys.stderr)
        return 1

    print("HTTP route surface matches the reference.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
