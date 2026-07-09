#!/bin/bash
# wsl-test.sh — Runs the Arma 3 Manager API natively in WSL2 (no containers).
# This bypasses Docker/Podman UDP NAT so Arma 3 direct-connect works
# from the same Windows machine via 127.0.0.1:2302
#
# Usage (from WSL2 terminal):
#   bash /mnt/c/Users/sword/OneDrive/Documentos/arma/arma_server/wsl-test.sh
#
# Or from Windows PowerShell:
#   wsl bash /mnt/c/Users/sword/OneDrive/Documentos/arma/arma_server/wsl-test.sh

set -e

ARMA3_DIR="${ARMA3_DIR:-/home/hela/arma3-test}"
WEB_PORT="${WEB_PORT:-8082}"
WEB_USERNAME="${WEB_USERNAME:-admin}"
WEB_PASSWORD="${WEB_PASSWORD:-admin}"
SESSION_SECRET="${SESSION_SECRET:-wsl-test-secret}"
STEAM_OWNER_IDS="${STEAM_OWNER_IDS:-76561198074208173}"

API_BIN_DIR="/tmp/arma3-manager-api"
API_DLL="$API_BIN_DIR/Arma3Manager.Api.dll"

REPO_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "=== Arma 3 Manager — WSL2 Native Test ==="
echo "  ARMA3_DIR : $ARMA3_DIR"
echo "  WEB_PORT  : $WEB_PORT"
echo ""

# ─── 1. Ensure .NET 10 runtime is installed ──────────────────────────────────
if ! command -v dotnet &>/dev/null || ! dotnet --list-runtimes 2>/dev/null | grep -q "Microsoft.NETCore.App 10"; then
    echo "[1/4] Installing .NET 10 runtime..."
    if command -v apt-get &>/dev/null; then
        # Try Microsoft package feed
        if ! dpkg -l dotnet-runtime-10.0 &>/dev/null 2>&1; then
            # Install via snap or direct installer as fallback
            if ! curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh 2>/dev/null; then
                echo "Cannot download dotnet installer. Install manually: sudo apt-get install dotnet-runtime-10.0"
                exit 1
            fi
            bash /tmp/dotnet-install.sh --runtime dotnet --version 10.0 --install-dir ~/.dotnet
            export DOTNET_ROOT="$HOME/.dotnet"
            export PATH="$HOME/.dotnet:$PATH"
        fi
    fi
else
    echo "[1/4] .NET 10 runtime: OK"
fi

# Add ~/.dotnet to PATH if installed there
if [ -f "$HOME/.dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
fi

# ─── 2. Extract API binary from container ────────────────────────────────────
if [ ! -f "$API_DLL" ]; then
    echo "[2/4] Extracting API binary from container..."
    # Try running container first
    CONTAINER_RUNNING=false
    if podman ps --format "{{.Names}}" 2>/dev/null | grep -q "arma3-api"; then
        CONTAINER_RUNNING=true
    fi

    if $CONTAINER_RUNNING; then
        podman cp arma3-api:/app "$API_BIN_DIR"
        echo "  Extracted from running container."
    else
        echo "  Starting container temporarily to extract binary..."
        # Start the container just long enough to copy the binary
        podman run -d --rm --name arma3-api-extract \
            localhost/arma3-manager-api:latest sleep 30 2>/dev/null || true
        sleep 2
        if podman cp arma3-api-extract:/app "$API_BIN_DIR" 2>/dev/null; then
            podman stop arma3-api-extract 2>/dev/null || true
            echo "  Extracted successfully."
        else
            # Last resort: try to find the image and extract with create (no run)
            CID=$(podman create localhost/arma3-manager-api:latest 2>/dev/null) || true
            if [ -n "$CID" ]; then
                podman cp "$CID":/app "$API_BIN_DIR"
                podman rm "$CID" >/dev/null 2>&1 || true
                echo "  Extracted from image."
            else
                echo "ERROR: Cannot extract API binary."
                echo "  Run 'python manage.py rebuild' from Windows PowerShell first, then retry."
                exit 1
            fi
        fi
    fi
else
    echo "[2/4] API binary: OK ($API_DLL)"
fi

# ─── 3. Ensure Arma 3 test directory exists ──────────────────────────────────
echo "[3/4] Checking Arma 3 directory: $ARMA3_DIR"
mkdir -p "$ARMA3_DIR/keys" "$ARMA3_DIR/serverprofile" "$ARMA3_DIR/mpmissions"

# Fix server.cfg mission template if it's still a custom map
SERVER_CFG="$ARMA3_DIR/server.cfg"
if [ ! -f "$SERVER_CFG" ] && [ -f "$REPO_DIR/config/server.cfg" ]; then
    cp "$REPO_DIR/config/server.cfg" "$SERVER_CFG"
fi
if [ -f "$SERVER_CFG" ] && grep -q "putoalv\|Russia-Ukraine\|Stratis\.Stratis" "$SERVER_CFG" 2>/dev/null; then
    sed -i 's/template = ".*"/template = "empty.VR"/' "$SERVER_CFG"
    echo "  Fixed server.cfg template → empty.VR"
fi

# ─── 4. Start the API ────────────────────────────────────────────────────────
echo "[4/4] Starting API on port $WEB_PORT..."
echo ""
echo "  Panel:  http://localhost:$WEB_PORT"
echo "  Login:  $WEB_USERNAME / $WEB_PASSWORD"
echo "  Arma 3 Direct Connect: 127.0.0.1:2302 (after clicking Start in the panel)"
echo ""
echo "  Press Ctrl+C to stop."
echo ""

export ARMA3_DIR WEB_PORT WEB_USERNAME WEB_PASSWORD SESSION_SECRET STEAM_OWNER_IDS
export ASPNETCORE_URLS="http://0.0.0.0:$WEB_PORT"
export DOTNET_NOLOGO=1

exec dotnet "$API_DLL"
