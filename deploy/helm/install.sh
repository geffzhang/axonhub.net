#!/bin/bash

# Installs or upgrades the AxonHub.NET Helm release.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RELEASE_NAME="${1:-axonhub}"
NAMESPACE="${2:-default}"
if [[ $# -ge 2 ]]; then
    shift 2
else
    shift "$#"
fi

if ! command -v helm >/dev/null 2>&1; then
    echo "[ERROR] helm is required" 1>&2
    exit 1
fi
if ! command -v kubectl >/dev/null 2>&1; then
    echo "[ERROR] kubectl is required" 1>&2
    exit 1
fi

echo "Installing/upgrading AxonHub.NET..."
echo "Release:   $RELEASE_NAME"
echo "Namespace: $NAMESPACE"
echo "Chart:     $SCRIPT_DIR"

helm upgrade --install "$RELEASE_NAME" "$SCRIPT_DIR" \
    --namespace "$NAMESPACE" \
    --create-namespace \
    --timeout 10m0s \
    "$@"

echo
echo "Installation completed."
echo "Access with:"
echo "  kubectl port-forward svc/$RELEASE_NAME 8090:8090 -n $NAMESPACE"
echo "  then open http://127.0.0.1:8090/"
echo
echo "Check status: kubectl get pods -n $NAMESPACE"
echo "View logs:    kubectl logs -l app.kubernetes.io/name=axonhub -n $NAMESPACE"
echo "Run test:     helm test $RELEASE_NAME -n $NAMESPACE"
echo "Uninstall:    helm uninstall $RELEASE_NAME -n $NAMESPACE"
