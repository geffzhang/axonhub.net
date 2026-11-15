#!/bin/bash

# Stops AxonHub.NET started by deploy/start.sh.

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

if [[ -n "${SUDO_USER:-}" && "$SUDO_USER" != "root" ]]; then
    TARGET_USER="$SUDO_USER"
    TARGET_HOME="$(eval echo "~${SUDO_USER}")"
else
    TARGET_USER="${USER:-$(id -un)}"
    TARGET_HOME="$HOME"
fi

BASE_DIR="${TARGET_HOME}/.config/axonhub"
BINARY_PATH="${AXONHUB_INSTALL_DIR:-/usr/local/bin}/axonhub"
PID_FILE="${BASE_DIR}/axonhub.pid"
PROCESS_NAME="axonhub"
TIMEOUT=15

print_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
print_success() { echo -e "${GREEN}[SUCCESS]${NC} $1"; }
print_warning() { echo -e "${YELLOW}[WARNING]${NC} $1"; }
print_error() { echo -e "${RED}[ERROR]${NC} $1"; }

find_pids() {
    {
        pgrep -x "$PROCESS_NAME" 2>/dev/null || true
        pgrep -f "$BINARY_PATH" 2>/dev/null || true
    } | sort -u
}

stop_pid() {
    local pid="$1"
    if ! kill -0 "$pid" 2>/dev/null; then
        return 0
    fi

    print_info "Sending SIGTERM to PID ${pid}..."
    kill -TERM "$pid" 2>/dev/null || true
    local count=0
    while kill -0 "$pid" 2>/dev/null && (( count < TIMEOUT )); do
        sleep 1
        ((count += 1))
    done

    if kill -0 "$pid" 2>/dev/null; then
        print_warning "PID ${pid} did not stop gracefully; sending SIGKILL"
        kill -KILL "$pid" 2>/dev/null || true
        sleep 2
    fi

    if kill -0 "$pid" 2>/dev/null; then
        print_error "Failed to stop PID ${pid}"
        return 1
    fi
    return 0
}

main() {
    print_info "Stopping AxonHub.NET..."
    local stopped=0
    local pid_file_pid=""

    if [[ -f "$PID_FILE" ]]; then
        pid_file_pid="$(cat "$PID_FILE" 2>/dev/null || true)"
        if [[ "$pid_file_pid" =~ ^[0-9]+$ ]]; then
            stop_pid "$pid_file_pid" && stopped=1
        else
            print_warning "Invalid PID file: $PID_FILE"
        fi
    fi

    if [[ "$stopped" -eq 0 ]]; then
        local pids scan_failed=0
        pids="$(find_pids)"
        if [[ -n "$pids" ]]; then
            for pid in $pids; do
                stop_pid "$pid" || scan_failed=1
            done
            if [[ "$scan_failed" -eq 0 ]]; then
                stopped=1
            else
                print_error "Some AxonHub.NET processes could not be stopped"
                return 1
            fi
        fi
    fi

    rm -f "$PID_FILE"
    if [[ "$stopped" -eq 1 ]]; then
        print_success "AxonHub.NET stopped"
    else
        print_info "No AxonHub.NET process was running"
    fi
}

case "${1:-}" in
    --force)
        print_info "Force stopping all AxonHub.NET processes..."
        pids="$(find_pids)"
        if [[ -n "$pids" ]]; then
            for pid in $pids; do
                kill -KILL "$pid" 2>/dev/null || true
            done
        fi
        rm -f "$PID_FILE"
        print_success "All AxonHub.NET processes stopped"
        ;;
    --help|-h)
        cat <<EOF
Usage: $0 [--force]

Stops AxonHub.NET directly (no systemd).
Options:
  --force     Force kill AxonHub.NET processes
EOF
        ;;
    "")
        main
        ;;
    *)
        print_error "Unknown option: $1"
        exit 1
        ;;
esac
