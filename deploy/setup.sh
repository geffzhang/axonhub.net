#!/bin/bash

# Manages auto-start for a user-level AxonHub.NET installation on Linux/macOS.

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

SERVICE_NAME="axonhub"
if [[ -n "${SUDO_USER:-}" && "$SUDO_USER" != "root" ]]; then
    USER_HOME="$(eval echo "~${SUDO_USER}")"
    TARGET_USER="$SUDO_USER"
else
    USER_HOME="$HOME"
    TARGET_USER="${USER:-$(id -un)}"
fi
BASE_DIR="${USER_HOME}/.config/axonhub"
BINARY_PATH="${AXONHUB_INSTALL_DIR:-/usr/local/bin}/axonhub"
PID_FILE="${BASE_DIR}/axonhub.pid"
LOG_FILE="${BASE_DIR}/axonhub.log"

OS="$(uname -s | tr '[:upper:]' '[:lower:]')"
ARCH="$(uname -m)"

print_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
print_success() { echo -e "${GREEN}[SUCCESS]${NC} $1"; }
print_warning() { echo -e "${YELLOW}[WARNING]${NC} $1"; }
print_error() { echo -e "${RED}[ERROR]${NC} $1"; }

usage() {
    cat 1>&2 <<EOF
AxonHub.NET Setup

Usage:
  ./setup.sh [command]

Commands:
  install-autostart    Start AxonHub.NET automatically on boot/login
  uninstall-autostart  Remove automatic startup
  status               Show autostart and process status

Platforms:
  Linux: systemd user service, then system service with sudo
  macOS: launchd user agent
EOF
}

systemd_user_service_path() {
    echo "${USER_HOME}/.config/systemd/user/${SERVICE_NAME}.service"
}

systemd_system_service_path() {
    echo "/etc/systemd/system/${SERVICE_NAME}.service"
}

write_systemd_service() {
    local service_path="$1"
    cat > "$service_path" <<EOF
[Unit]
Description=AxonHub.NET AI Gateway
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${TARGET_USER}
WorkingDirectory=${BASE_DIR}
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=${BASE_DIR}/.dotnet
EnvironmentFile=-${BASE_DIR}/axonhub.env
ExecStart=${BINARY_PATH}
Restart=always
RestartSec=5
TimeoutStopSec=30
KillSignal=SIGTERM
StandardOutput=append:${LOG_FILE}
StandardError=append:${LOG_FILE}

[Install]
WantedBy=default.target
EOF
}

install_systemd_user_service() {
    print_info "Installing systemd user service..."
    if [[ ! -x "$BINARY_PATH" ]]; then
        print_error "AxonHub.NET not found at $BINARY_PATH"
        print_info "Run deploy/install.sh first."
        return 1
    fi

    local service_path service_dir
    service_path="$(systemd_user_service_path)"
    service_dir="$(dirname "$service_path")"
    mkdir -p "$service_dir"
    write_systemd_service "$service_path"
    chown "$TARGET_USER:$(id -gn "$TARGET_USER" 2>/dev/null || echo "$TARGET_USER")" "$service_path" 2>/dev/null || true

    sudo -u "$TARGET_USER" systemctl --user daemon-reload
    sudo -u "$TARGET_USER" systemctl --user enable "$SERVICE_NAME.service"
    print_success "Systemd user service installed and enabled"
    print_info "It starts on next login; use 'systemctl --user start axonhub' now if needed."
    return 0
}

install_systemd_system_service() {
    print_info "Installing systemd system service..."
    if [[ $EUID -ne 0 ]]; then
        print_warning "Root privileges are required for the system service"
        print_info "Run: sudo ./setup.sh install-autostart"
        return 1
    fi
    if [[ ! -x "$BINARY_PATH" ]]; then
        print_error "AxonHub.NET not found at $BINARY_PATH"
        print_info "Run deploy/install.sh first."
        return 1
    fi

    local service_path
    service_path="$(systemd_system_service_path)"
    write_systemd_service "$service_path"
    sed -i 's/^WantedBy=default.target$/WantedBy=multi-user.target/' "$service_path"
    systemctl daemon-reload
    systemctl enable "$SERVICE_NAME.service"
    print_success "Systemd system service installed and enabled"
    return 0
}

uninstall_systemd_user_service() {
    local service_path
    service_path="$(systemd_user_service_path)"
    if [[ -f "$service_path" ]]; then
        sudo -u "$TARGET_USER" systemctl --user stop "$SERVICE_NAME.service" 2>/dev/null || true
        sudo -u "$TARGET_USER" systemctl --user disable "$SERVICE_NAME.service" 2>/dev/null || true
        rm -f "$service_path"
        sudo -u "$TARGET_USER" systemctl --user daemon-reload
        print_success "Systemd user service removed"
        return 0
    fi
    return 1
}

uninstall_systemd_system_service() {
    local service_path
    service_path="$(systemd_system_service_path)"
    if [[ -f "$service_path" ]]; then
        if [[ $EUID -ne 0 ]]; then
            print_error "Run with sudo to remove the system service"
            return 1
        fi
        systemctl stop "$SERVICE_NAME.service" 2>/dev/null || true
        systemctl disable "$SERVICE_NAME.service" 2>/dev/null || true
        rm -f "$service_path"
        systemctl daemon-reload
        print_success "Systemd system service removed"
        return 0
    fi
    return 1
}

check_systemd_status() {
    local user_service system_service
    user_service="$(systemd_user_service_path)"
    system_service="$(systemd_system_service_path)"
    if [[ -f "$user_service" ]] && sudo -u "$TARGET_USER" systemctl --user is-enabled "$SERVICE_NAME.service" >/dev/null 2>&1; then
        echo "enabled (user)"
    elif [[ -f "$system_service" ]] && systemctl is-enabled "$SERVICE_NAME.service" >/dev/null 2>&1; then
        echo "enabled (system)"
    elif [[ -f "$user_service" || -f "$system_service" ]]; then
        echo "disabled (service file exists)"
    else
        echo "disabled"
    fi
}

launchd_plist_path() {
    echo "${USER_HOME}/Library/LaunchAgents/com.axonhub.net.plist"
}

write_launchd_plist() {
    local plist_path="$1"
    cat > "$plist_path" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.axonhub.net</string>
    <key>ProgramArguments</key>
    <array>
        <string>${BINARY_PATH}</string>
    </array>
    <key>WorkingDirectory</key>
    <string>${BASE_DIR}</string>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <dict>
        <key>SuccessfulExit</key>
        <false/>
    </dict>
    <key>StandardOutPath</key>
    <string>${LOG_FILE}</string>
    <key>StandardErrorPath</key>
    <string>${LOG_FILE}</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>ASPNETCORE_ENVIRONMENT</key>
        <string>Production</string>
        <key>PATH</key>
        <string>/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin</string>
    </dict>
</dict>
</plist>
EOF
}

install_launchd_service() {
    print_info "Installing launchd user agent..."
    if [[ ! -x "$BINARY_PATH" ]]; then
        print_error "AxonHub.NET not found at $BINARY_PATH"
        print_info "Run deploy/install.sh first."
        return 1
    fi

    local plist_path
    plist_path="$(launchd_plist_path)"
    mkdir -p "$(dirname "$plist_path")"
    write_launchd_plist "$plist_path"
    chown "$TARGET_USER:$(id -gn "$TARGET_USER" 2>/dev/null || echo "$TARGET_USER")" "$plist_path" 2>/dev/null || true
    sudo -u "$TARGET_USER" launchctl unload "$plist_path" 2>/dev/null || true
    sudo -u "$TARGET_USER" launchctl load "$plist_path"
    print_success "Launchd user agent installed"
    return 0
}

uninstall_launchd_service() {
    local plist_path
    plist_path="$(launchd_plist_path)"
    if [[ -f "$plist_path" ]]; then
        sudo -u "$TARGET_USER" launchctl unload "$plist_path" 2>/dev/null || true
        rm -f "$plist_path"
        print_success "Launchd user agent removed"
        return 0
    fi
    return 1
}

check_launchd_status() {
    if [[ -f "$(launchd_plist_path)" ]]; then
        if sudo -u "$TARGET_USER" launchctl list 2>/dev/null | grep -q "com.axonhub.net"; then
            echo "enabled"
        else
            echo "disabled (plist exists)"
        fi
    else
        echo "disabled"
    fi
}

install_autostart() {
    print_info "Installing AxonHub.NET auto-start..."
    case "$OS" in
        linux)
            if ! install_systemd_user_service; then
                print_info "Falling back to system service"
                install_systemd_system_service
            fi
            ;;
        darwin) install_launchd_service ;;
        *) print_error "Unsupported operating system: $OS"; exit 1 ;;
    esac
}

uninstall_autostart() {
    print_info "Removing AxonHub.NET auto-start..."
    case "$OS" in
        linux)
            uninstall_systemd_user_service || true
            uninstall_systemd_system_service || true
            ;;
        darwin) uninstall_launchd_service || true ;;
        *) print_error "Unsupported operating system: $OS"; exit 1 ;;
    esac
}

show_status() {
    print_info "AxonHub.NET status"
    echo "  Platform: ${OS} (${ARCH})"
    echo "  User:     ${TARGET_USER}"
    echo "  Base:     ${BASE_DIR}"
    echo "  Binary:   ${BINARY_PATH}"
    case "$OS" in
        linux) echo "  Autostart: $(check_systemd_status)" ;;
        darwin) echo "  Autostart: $(check_launchd_status)" ;;
    esac

    if [[ -f "$PID_FILE" ]]; then
        local pid
        pid="$(cat "$PID_FILE" 2>/dev/null || true)"
        if [[ "$pid" =~ ^[0-9]+$ ]] && kill -0 "$pid" 2>/dev/null; then
            echo "  Process:  running (PID ${pid})"
            return
        fi
    fi
    local pids
    pids="$(pgrep -x axonhub 2>/dev/null || true)"
    if [[ -n "$pids" ]]; then
        echo "  Process:  running (PID ${pids//$'\n'/ })"
    else
        echo "  Process:  not running"
    fi
}

case "${1:-}" in
    install-autostart|enable-autostart) install_autostart ;;
    uninstall-autostart|disable-autostart) uninstall_autostart ;;
    status|check) show_status ;;
    -h|--help) usage ;;
    *) usage; exit 1 ;;
esac
