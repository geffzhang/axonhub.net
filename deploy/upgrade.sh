#!/bin/bash

# Rebuilds and reinstalls AxonHub.NET from a source checkout.
#
# axonhub.net does not publish standalone release archives yet, so "upgrade"
# means: update the source checkout, republish, keep the existing config, and
# restart the service.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_DIR="${AXONHUB_SOURCE_DIR:-$(cd "$SCRIPT_DIR/.." && pwd)}"
PULL="true"
if [[ -n "${SUDO_USER:-}" && "$SUDO_USER" != "root" ]]; then
    TARGET_USER="$SUDO_USER"
    TARGET_HOME="$(eval echo "~${SUDO_USER}")"
else
    TARGET_USER="${USER:-$(id -un)}"
    TARGET_HOME="$HOME"
fi

print_info() { echo -e "\033[0;34m[INFO]\033[0m $1" 1>&2; }
print_success() { echo -e "\033[0;32m[SUCCESS]\033[0m $1" 1>&2; }
print_warning() { echo -e "\033[0;33m[WARNING]\033[0m $1" 1>&2; }
print_error() { echo -e "\033[0;31m[ERROR]\033[0m $1" 1>&2; }

run_as_target() {
    if [[ $EUID -eq 0 && -n "${SUDO_USER:-}" && "$SUDO_USER" != "root" ]]; then
        sudo -u "$SUDO_USER" env HOME="$TARGET_HOME" "$@"
    else
        "$@"
    fi
}

usage() {
    cat 1>&2 <<EOF
AxonHub.NET upgrade

Usage:
  sudo ./deploy/upgrade.sh [options]

Options:
  --source DIR   Source checkout (default: repo containing this script)
  --no-pull      Rebuild the current checkout without running git pull
  -h, --help     Show this help and exit

The existing config.yml is preserved. The installer must be able to run
dotnet publish (the .NET SDK 10 is required).
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --source)
            [[ $# -ge 2 ]] || { print_error "--source requires a directory"; exit 1; }
            SOURCE_DIR="$2"; shift 2 ;;
        --no-pull) PULL="false"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) print_error "Unknown option: $1"; usage; exit 1 ;;
    esac
done

SOURCE_DIR="$(cd "$SOURCE_DIR" 2>/dev/null && pwd)" || {
    print_error "Source directory not found: $SOURCE_DIR"
    exit 1
}
if [[ ! -f "$SOURCE_DIR/src/AxonHub.Api/AxonHub.Api.csproj" ]]; then
    print_error "Not an axonhub.net source checkout: $SOURCE_DIR"
    exit 1
fi

if [[ "$PULL" == "true" ]]; then
    if git -C "$SOURCE_DIR" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
        if [[ -n "$(git -C "$SOURCE_DIR" status --porcelain)" ]]; then
            print_error "Source checkout has uncommitted changes."
            print_info "Commit/stash them, or run with --no-pull to rebuild the current tree."
            exit 1
        fi
        print_info "Updating $(git -C "$SOURCE_DIR" rev-parse --abbrev-ref HEAD)..."
        run_as_target git -C "$SOURCE_DIR" pull --ff-only
    else
        print_warning "Not a git checkout; rebuilding the current source tree."
    fi
fi

print_info "Rebuilding and reinstalling from $SOURCE_DIR..."
"$SCRIPT_DIR/install.sh" --source "$SOURCE_DIR"

print_info "Restarting AxonHub.NET..."
"$SCRIPT_DIR/restart.sh"
print_success "Upgrade completed"
