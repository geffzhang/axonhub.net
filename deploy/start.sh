#!/bin/bash

# Starts AxonHub.NET directly (no systemd), with a PID file and log file.

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
TARGET_GROUP="$(id -gn "$TARGET_USER" 2>/dev/null || echo "$TARGET_USER")"

BASE_DIR="${TARGET_HOME}/.config/axonhub"
CONFIG_FILE="${BASE_DIR}/config.yml"
BINARY_PATH="${AXONHUB_INSTALL_DIR:-/usr/local/bin}/axonhub"
PID_FILE="${BASE_DIR}/axonhub.pid"
LOG_FILE="${BASE_DIR}/axonhub.log"
DEFAULT_PORT=8090

print_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
print_success() { echo -e "${GREEN}[SUCCESS]${NC} $1"; }
print_warning() { echo -e "${YELLOW}[WARNING]${NC} $1"; }
print_error() { echo -e "${RED}[ERROR]${NC} $1"; }

run_as_target() {
    if [[ $EUID -eq 0 && -n "${SUDO_USER:-}" && "$SUDO_USER" != "root" ]]; then
        sudo -u "$SUDO_USER" env HOME="$TARGET_HOME" "$@"
    else
        "$@"
    fi
}

get_configured_port() {
    local port
    port="$(run_as_target bash -c 'cd "$1" && "$2" config get server.port 2>/dev/null' \
        bash "$BASE_DIR" "$BINARY_PATH" || true)"
    if [[ "$port" =~ ^[0-9]+$ ]]; then
        echo "$port"
    else
        echo "$DEFAULT_PORT"
    fi
}

check_port() {
    local port="$1"
    if command -v ss >/dev/null 2>&1; then
        if ss -ltn 2>/dev/null | awk '{print $4}' | grep -Eq "[:.]${port}$"; then
            print_warning "Port ${port} is already in use"
            return 1
        fi
    elif command -v lsof >/dev/null 2>&1; then
        if lsof -nP -iTCP:"${port}" -sTCP:LISTEN >/dev/null 2>&1; then
            print_warning "Port ${port} is already in use"
            return 1
        fi
    fi
    return 0
}

start_directly() {
    local port="$1"

    if [[ -f "$PID_FILE" ]]; then
        local pid
        pid="$(cat "$PID_FILE" 2>/dev/null || true)"
        if [[ "$pid" =~ ^[0-9]+$ ]] && kill -0 "$pid" 2>/dev/null; then
            print_warning "AxonHub.NET is already running (PID: $pid)"
            return 0
        fi
        print_info "Removing stale PID file"
        rm -f "$PID_FILE"
    fi

    if [[ ! -x "$BINARY_PATH" ]]; then
        print_error "AxonHub.NET binary not found at $BINARY_PATH"
        print_info "Run deploy/install.sh first."
        return 1
    fi
    if [[ ! -f "$CONFIG_FILE" ]]; then
        print_warning "Configuration not found at $CONFIG_FILE"
        print_info "The server requires PostgreSQL; create it with deploy/install.sh."
    fi

    mkdir -p "$BASE_DIR/.dotnet"
    : > "$LOG_FILE"
    chown "$TARGET_USER:$TARGET_GROUP" "$BASE_DIR" "$BASE_DIR/.dotnet" "$LOG_FILE" 2>/dev/null || true

    print_info "Starting AxonHub.NET (port ${port})..."
    if [[ $EUID -eq 0 ]]; then
        sudo -u "$TARGET_USER" env HOME="$TARGET_HOME" \
            bash -c 'cd "$1" || exit 1; ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}" DOTNET_BUNDLE_EXTRACT_BASE_DIR="$1/.dotnet" "$2" >> "$3" 2>&1 & echo $! > "$4"' \
            bash "$BASE_DIR" "$BINARY_PATH" "$LOG_FILE" "$PID_FILE"
        local pid
        pid="$(cat "$PID_FILE" 2>/dev/null || true)"
    else
        (
            cd "$BASE_DIR"
            ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}" \
                DOTNET_BUNDLE_EXTRACT_BASE_DIR="$BASE_DIR/.dotnet" \
                "$BINARY_PATH" >> "$LOG_FILE" 2>&1 &
            echo "$!" > "$PID_FILE"
        )
        local pid
        pid="$(cat "$PID_FILE" 2>/dev/null || true)"
    fi

    sleep 2
    if [[ "$pid" =~ ^[0-9]+$ ]] && kill -0 "$pid" 2>/dev/null; then
        print_success "AxonHub.NET started (PID: $pid)"
        echo "  PID:    $pid"
        echo "  Config: $CONFIG_FILE"
        echo "  Log:    $LOG_FILE"
        echo "  Web:    http://localhost:${port}"
        print_info "Stop with deploy/stop.sh; view logs with: tail -f $LOG_FILE"
        return 0
    fi

    print_error "AxonHub.NET failed to start"
    if [[ -f "$LOG_FILE" ]]; then
        print_info "Last log lines:"
        tail -n 20 "$LOG_FILE" || true
    fi
    rm -f "$PID_FILE"
    return 1
}

main() {
    local port
    port="$(get_configured_port)"
    if ! check_port "$port"; then
        print_error "Cannot start AxonHub.NET: port ${port} is already in use"
        return 1
    fi
    start_directly "$port"
}

case "${1:-}" in
    --help|-h)
        cat <<EOF
Usage: $0

Starts AxonHub.NET directly (no systemd).
Config: $CONFIG_FILE
Logs:   $LOG_FILE
PID:    $PID_FILE
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
