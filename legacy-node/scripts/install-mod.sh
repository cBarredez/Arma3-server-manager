#!/bin/bash
# Download a single Steam Workshop mod for Arma 3 (AppID 107410)
# and install it into the mods directory with proper lowercase symlinks.
# Usage: install-mod.sh <workshop_id> [display_name]
set -e

WORKSHOP_ID="$1"
DISPLAY_NAME="${2:-}"

if [ -z "${WORKSHOP_ID}" ]; then
    echo "[install-mod] ERROR: Workshop ID required." >&2
    exit 1
fi

if ! [[ "${WORKSHOP_ID}" =~ ^[0-9]+$ ]]; then
    echo "[install-mod] ERROR: Workshop ID must be numeric." >&2
    exit 1
fi

ARMA3_DIR="${ARMA3_DIR:-/arma3}"
MODS_DIR="${ARMA3_DIR}/mods"
KEYS_DIR="${ARMA3_DIR}/keys"
STEAMCMD="${STEAMCMD_DIR:-/steamcmd}/steamcmd.sh"
STEAM_USER="${STEAM_USER:-anonymous}"
STEAM_PASS="${STEAM_PASS:-}"
WORKSHOP_CONTENT="${ARMA3_DIR}/steamapps/workshop/content/107410"

echo "[install-mod] Downloading Workshop item ${WORKSHOP_ID}..."

${STEAMCMD} \
    +force_install_dir "${ARMA3_DIR}" \
    +login "${STEAM_USER}" ${STEAM_PASS:+"${STEAM_PASS}"} \
    +workshop_download_item 107410 "${WORKSHOP_ID}" validate \
    +quit

MOD_SRC="${WORKSHOP_CONTENT}/${WORKSHOP_ID}"

if [ ! -d "${MOD_SRC}" ]; then
    echo "[install-mod] ERROR: Downloaded mod directory not found: ${MOD_SRC}" >&2
    exit 1
fi

# ─── Determine mod folder name ────────────────────────────────────────────────
if [ -n "${DISPLAY_NAME}" ]; then
    # Sanitise display name for use as a directory name
    SAFE_NAME=$(echo "${DISPLAY_NAME}" | tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9_-]/_/g')
    MOD_FOLDER="@${SAFE_NAME}"
elif [ -f "${MOD_SRC}/meta.cpp" ]; then
    RAW_NAME=$(grep -oP '(?<=name = ")[^"]+' "${MOD_SRC}/meta.cpp" | head -1 || true)
    if [ -n "${RAW_NAME}" ]; then
        SAFE_NAME=$(echo "${RAW_NAME}" | tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9_-]/_/g')
        MOD_FOLDER="@${SAFE_NAME}"
    else
        MOD_FOLDER="@${WORKSHOP_ID}"
    fi
else
    MOD_FOLDER="@${WORKSHOP_ID}"
fi

MOD_DEST="${MODS_DIR}/${MOD_FOLDER}"

echo "[install-mod] Installing mod to ${MOD_DEST}..."
mkdir -p "${MODS_DIR}"
cp -r "${MOD_SRC}" "${MOD_DEST}"

# ─── Lowercase all files inside the mod (Linux case-sensitivity fix) ──────────
find "${MOD_DEST}" -depth | while IFS= read -r item; do
    dir=$(dirname "${item}")
    base=$(basename "${item}")
    lower=$(echo "${base}" | tr '[:upper:]' '[:lower:]')
    if [ "${base}" != "${lower}" ] && [ -e "${dir}/${base}" ]; then
        mv "${dir}/${base}" "${dir}/${lower}" 2>/dev/null || true
    fi
done

# ─── Copy server keys ─────────────────────────────────────────────────────────
mkdir -p "${KEYS_DIR}"
for keys_dir in "${MOD_DEST}/keys" "${MOD_DEST}/key"; do
    if [ -d "${keys_dir}" ]; then
        echo "[install-mod] Copying keys from ${keys_dir}..."
        find "${keys_dir}" -name "*.bikey" -exec cp {} "${KEYS_DIR}/" \;
    fi
done

echo "[install-mod] Mod ${WORKSHOP_ID} installed as ${MOD_FOLDER}"
