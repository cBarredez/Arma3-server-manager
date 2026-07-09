#!/bin/bash
set -e

ARMA3_DIR="${ARMA3_DIR:-/arma3}"
CONFIG_DIR="${ARMA3_DIR}/config"
DEFAULTS_DIR="/defaults/config"
LOG_PREFIX="[entrypoint]"

echo "${LOG_PREFIX} Starting Arma 3 Server Manager..."

# ─── Initialise config directory from defaults ────────────────────────────────
if [ -d "${DEFAULTS_DIR}" ]; then
    for f in "${DEFAULTS_DIR}"/*; do
        fname=$(basename "$f")
        if [ ! -f "${CONFIG_DIR}/${fname}" ]; then
            echo "${LOG_PREFIX} Copying default config: ${fname}"
            cp "$f" "${CONFIG_DIR}/${fname}"
        fi
    done
fi

# ─── Apply environment variables to server.cfg ───────────────────────────────
SERVER_CFG="${CONFIG_DIR}/server.cfg"
if [ -f "${SERVER_CFG}" ]; then
    if [ -n "${SERVER_NAME}" ]; then
        sed -i "s|hostname = \".*\"|hostname = \"${SERVER_NAME}\"|" "${SERVER_CFG}"
    fi
    if [ -n "${SERVER_PASSWORD}" ]; then
        sed -i "s|^password = \".*\"|password = \"${SERVER_PASSWORD}\"|" "${SERVER_CFG}"
    fi
    if [ -n "${SERVER_PASSWORD_ADMIN}" ]; then
        sed -i "s|passwordAdmin = \".*\"|passwordAdmin = \"${SERVER_PASSWORD_ADMIN}\"|" "${SERVER_CFG}"
    fi
    if [ -n "${SERVER_MAX_PLAYERS}" ]; then
        sed -i "s|maxPlayers = [0-9]*|maxPlayers = ${SERVER_MAX_PLAYERS}|" "${SERVER_CFG}"
    fi
fi

# ─── Install Arma 3 dedicated server if not already present ──────────────────
ARMA3_BIN="${ARMA3_DIR}/arma3server_x64"
if [ ! -f "${ARMA3_BIN}" ]; then
    echo "${LOG_PREFIX} Arma 3 server not found. Installing via SteamCMD with configured Steam account..."
    /scripts/install-server.sh
    echo "${LOG_PREFIX} Arma 3 server installed."
else
    echo "${LOG_PREFIX} Arma 3 server found at ${ARMA3_BIN}"
fi

# ─── Start the web management panel (Node.js) ────────────────────────────────
echo "${LOG_PREFIX} Starting web management panel on port ${WEB_PORT:-8080}..."
exec node /app/server.js
