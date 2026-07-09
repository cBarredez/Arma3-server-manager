#!/bin/bash
# Install or update the Arma 3 dedicated server via SteamCMD.
# AppID 233780 requires a Steam account that OWNS Arma 3.
# Anonymous login will fail with "No subscription".
set -e

ARMA3_DIR="${ARMA3_DIR:-/arma3}"
STEAMCMD="${STEAMCMD_DIR:-/steamcmd}/steamcmd.sh"
STEAM_USER="${STEAM_USER:-}"
STEAM_PASS="${STEAM_PASS:-}"

if [ -z "${STEAM_USER}" ] || [ "${STEAM_USER}" = "anonymous" ]; then
    echo "[install-server] ERROR: AppID 233780 requires a Steam account that owns Arma 3."
    echo "[install-server] Set STEAM_USER and STEAM_PASS in your .env file."
    exit 1
fi

echo "[install-server] Installing Arma 3 dedicated server (AppID 233780) as ${STEAM_USER}..."

${STEAMCMD} \
    +force_install_dir "${ARMA3_DIR}" \
    +login "${STEAM_USER}" "${STEAM_PASS}" \
    +app_update 233780 validate \
    +quit

echo "[install-server] Done."
