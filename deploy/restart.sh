#!/bin/bash

# Restarts AxonHub.NET by stopping and starting it.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FORCE="false"

print_info() { echo -e "\033[0;34m[INFO]\033[0m $1"; }
print_success() { echo -e "\033[0;32m[SUCCESS]\033[0m $1"; }
print_error() { echo -e "\033[0;31m[ERROR]\033[0m $1"; }

usage() {
    cat <<EOF
Usage: $0 [--force]

Restarts AxonHub.NET.
Options:
  --force     Force kill before restart
EOF
}

case "${1:-}" in
    --force) FORCE="true" ;;
    --help|-h) usage; exit 0 ;;
    "") ;;
    *) print_error "Unknown option: $1"; usage; exit 1 ;;
esac

print_info "Restarting AxonHub.NET..."
if [[ "$FORCE" == "true" ]]; then
    "$SCRIPT_DIR/stop.sh" --force || true
else
    "$SCRIPT_DIR/stop.sh" || true
fi

sleep 3
"$SCRIPT_DIR/start.sh"
print_success "AxonHub.NET restarted"
