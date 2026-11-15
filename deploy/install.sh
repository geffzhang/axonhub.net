#!/bin/bash

# AxonHub.NET installer for Linux and macOS.
#
# Unlike the Go reference, axonhub.net is not distributed as a single Go
# binary. This installer publishes the API as a self-contained single-file
# executable from a source checkout, then installs the admin frontend next to
# the per-user config directory. PostgreSQL 17 must be provided separately.

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SOURCE_DIR="${AXONHUB_SOURCE_DIR:-$REPO_ROOT}"
INSTALL_DIR="${AXONHUB_INSTALL_DIR:-/usr/local/bin}"

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
PUBLISH_DIR="${BASE_DIR}/.publish"
DEFAULT_PORT=8090
PORT="$DEFAULT_PORT"
DB_DSN="${AXONHUB_DB_DSN:-Host=localhost;Port=5432;Database=axonhub;Username=axonhub;Password=axonhub}"

print_info() { echo -e "${BLUE}[INFO]${NC} $1" 1>&2; }
print_success() { echo -e "${GREEN}[SUCCESS]${NC} $1" 1>&2; }
print_warning() { echo -e "${YELLOW}[WARNING]${NC} $1" 1>&2; }
print_error() { echo -e "${RED}[ERROR]${NC} $1" 1>&2; }

usage() {
    cat 1>&2 <<EOF
AxonHub.NET installer

Usage:
  sudo ./deploy/install.sh [options]

Options:
  --source DIR     Source checkout to publish (default: repo containing this script)
  --db-dsn DSN     PostgreSQL connection string for a new config.yml
  --port PORT      HTTP port for a new config.yml (default: 8090)
  -h, --help       Show this help and exit

Environment:
  AXONHUB_SOURCE_DIR   Same as --source
  AXONHUB_DB_DSN       Same as --db-dsn
  AXONHUB_INSTALL_DIR  Binary directory (default: /usr/local/bin)

The installer never overwrites an existing config.yml. SQLite/MySQL are not
supported by axonhub.net; PostgreSQL 17 must be reachable at startup.
EOF
}

check_root() {
    if [[ $EUID -ne 0 ]]; then
        print_error "This installer must run as root (use sudo)"
        exit 1
    fi
}

parse_args() {
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --source)
                [[ $# -ge 2 ]] || { print_error "--source requires a directory"; exit 1; }
                SOURCE_DIR="$2"
                shift 2
                ;;
            --db-dsn)
                [[ $# -ge 2 ]] || { print_error "--db-dsn requires a value"; exit 1; }
                DB_DSN="$2"
                shift 2
                ;;
            --port)
                [[ $# -ge 2 ]] || { print_error "--port requires a value"; exit 1; }
                PORT="$2"
                shift 2
                ;;
            -h|--help)
                usage
                exit 0
                ;;
            *)
                print_error "Unknown option: $1"
                usage
                exit 1
                ;;
        esac
    done

    if ! [[ "$PORT" =~ ^[0-9]+$ ]] || (( PORT < 1 || PORT > 65535 )); then
        print_error "Invalid port: $PORT"
        exit 1
    fi

    SOURCE_DIR="$(cd "$SOURCE_DIR" 2>/dev/null && pwd)" || {
        print_error "Source directory not found: $SOURCE_DIR"
        exit 1
    }
    if [[ ! -f "$SOURCE_DIR/src/AxonHub.Api/AxonHub.Api.csproj" ]]; then
        print_error "Not an axonhub.net source checkout: $SOURCE_DIR"
        exit 1
    fi
}

detect_rid() {
    local arch os
    arch="$(uname -m)"
    os="$(uname -s | tr '[:upper:]' '[:lower:]')"

    case "$arch" in
        x86_64|amd64) arch="x64" ;;
        aarch64|arm64) arch="arm64" ;;
        *) print_error "Unsupported architecture: $arch"; exit 1 ;;
    esac

    case "$os" in
        linux) echo "linux-$arch" ;;
        darwin) echo "osx-$arch" ;;
        *) print_error "Unsupported operating system: $os"; exit 1 ;;
    esac
}

require_dotnet() {
    if ! command -v dotnet >/dev/null 2>&1; then
        print_error ".NET SDK 10 is required: https://dotnet.microsoft.com/"
        exit 1
    fi
}

run_as_target() {
    if [[ $EUID -eq 0 && -n "${SUDO_USER:-}" && "$SUDO_USER" != "root" ]]; then
        sudo -u "$SUDO_USER" env HOME="$TARGET_HOME" "$@"
    else
        "$@"
    fi
}

ensure_directories() {
    print_info "Preparing ${BASE_DIR}..."
    mkdir -p "$BASE_DIR/frontend" "$BASE_DIR/.dotnet"
    chown -R "$TARGET_USER:$TARGET_GROUP" "$BASE_DIR"
    chmod 755 "$BASE_DIR"
}

publish_binary() {
    local rid="$1"
    print_info "Publishing AxonHub.Api for ${rid} (self-contained single file)..."
    rm -rf "$PUBLISH_DIR"
    mkdir -p "$PUBLISH_DIR"
    chown "$TARGET_USER:$TARGET_GROUP" "$PUBLISH_DIR"

    run_as_target dotnet publish \
        "$SOURCE_DIR/src/AxonHub.Api/AxonHub.Api.csproj" \
        --configuration Release \
        --runtime "$rid" \
        --self-contained true \
        --output "$PUBLISH_DIR" \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=None \
        -p:DebugSymbols=false

    if [[ ! -f "$PUBLISH_DIR/AxonHub.Api" ]]; then
        print_error "Publish succeeded but AxonHub.Api was not produced"
        exit 1
    fi
}

ensure_frontend() {
    local dist="$SOURCE_DIR/frontend/dist"
    if [[ -f "$dist/index.html" ]]; then
        return
    fi

    print_warning "frontend/dist is missing; building the admin UI..."
    if ! command -v pnpm >/dev/null 2>&1; then
        print_error "frontend/dist is missing and pnpm is not installed."
        print_info "Install Node.js 24 + pnpm 10, then run:"
        print_info "  cd frontend && pnpm install --frozen-lockfile && pnpm build"
        exit 1
    fi

    local pnpm_bin
    pnpm_bin="$(command -v pnpm)"
    run_as_target bash -c \
        'cd "$1" && PATH="$PATH:$2" "$3" install --frozen-lockfile && PATH="$PATH:$2" "$3" build' \
        bash "$SOURCE_DIR/frontend" "$(dirname "$(command -v node)")" "$pnpm_bin"
}

install_files() {
    print_info "Installing AxonHub.NET to ${INSTALL_DIR}..."
    mkdir -p "$INSTALL_DIR"
    install -m 0755 "$PUBLISH_DIR/AxonHub.Api" "$INSTALL_DIR/axonhub"

    rm -rf "$BASE_DIR/frontend/dist"
    cp -a "$SOURCE_DIR/frontend/dist" "$BASE_DIR/frontend/dist"
    chown -R "$TARGET_USER:$TARGET_GROUP" "$BASE_DIR/frontend"
    rm -rf "$PUBLISH_DIR"
}

yaml_quote() {
    # Config values live inside double-quoted YAML scalars.
    printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'
}

create_default_config() {
    if [[ -f "$CONFIG_FILE" ]]; then
        print_info "Keeping existing configuration at $CONFIG_FILE"
        return
    fi

    print_info "Creating default configuration at $CONFIG_FILE"
    cat > "$CONFIG_FILE" <<EOF
server:
  host: "0.0.0.0"
  port: ${PORT}
  name: "AxonHub"
  debug: false
  pid_file: "$(yaml_quote "$BASE_DIR/axonhub.pid")"

# axonhub.net supports PostgreSQL only. PostgreSQL 17 is recommended.
db:
  dialect: postgres
  dsn: "$(yaml_quote "$DB_DSN")"

# Optional multi-silo mode. Keep it disabled for one process. For Kubernetes
# or multiple VMs, enable it and point every replica at the same PostgreSQL.
orleans_cluster:
  enabled: false
  provider: adonet
  connection_string: ""
  cluster_id: "axonhub"
  service_id: "axonhub"
  silo_name: ""
  advertised_ip: ""
  silo_port: 11111
  gateway_port: 30000

# The .NET port does not consume the reference's cache/log/provider_quota
# sections. Logs go to stdout (redirected by start.sh / systemd).
EOF

    chown "$TARGET_USER:$TARGET_GROUP" "$CONFIG_FILE" 2>/dev/null || true
    chmod 600 "$CONFIG_FILE"
    print_warning "Edit ${CONFIG_FILE} before starting: set db.dsn to your PostgreSQL DSN."
}

configured_port() {
    local port
    port="$(run_as_target bash -c 'cd "$1" && "$2" config get server.port 2>/dev/null' \
        bash "$BASE_DIR" "$INSTALL_DIR/axonhub" || true)"
    if [[ "$port" =~ ^[0-9]+$ ]]; then
        echo "$port"
    else
        echo "$PORT"
    fi
}

main() {
    parse_args "$@"
    check_root
    require_dotnet

    local rid
    rid="$(detect_rid)"
    print_info "Platform: ${rid}"

    ensure_directories
    ensure_frontend
    publish_binary "$rid"
    install_files
    create_default_config

    local port
    port="$(configured_port)"
    print_success "AxonHub.NET installation completed"
    print_info "Configuration: ${CONFIG_FILE}"
    print_info "Frontend:      ${BASE_DIR}/frontend/dist"
    print_info "Next steps:"
    echo "  1. Edit ${CONFIG_FILE} and set db.dsn"
    echo "  2. Start:       ${SCRIPT_DIR}/start.sh"
    echo "  3. Stop:        ${SCRIPT_DIR}/stop.sh"
    echo "  4. Logs:        ${BASE_DIR}/axonhub.log"
    echo "  5. Web UI:      http://localhost:${port}"
    echo "  6. Autostart:   ${SCRIPT_DIR}/setup.sh install-autostart"
}

main "$@"
