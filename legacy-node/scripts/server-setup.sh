#!/bin/bash
# Run this script ON YOUR SERVER via SSH to install the web management panel.
# It installs Node.js + pm2, copies the panel files, and starts everything.
#
# Usage:
#   bash server-setup.sh
#
set -e

PANEL_DIR="${HOME}/arma3-manager"
NODE_VERSION=20

echo "=== Arma 3 Manager — Server Setup ==="

# ─── 1. Detect Arma 3 directory ──────────────────────────────────────────────
if [ -z "${ARMA3_DIR}" ]; then
    # Common locations
    for candidate in /home/container /home/arma3 /opt/arma3 "${HOME}/arma3" "${HOME}"; do
        if [ -f "${candidate}/arma3server_x64" ] || [ -f "${candidate}/arma3server" ]; then
            ARMA3_DIR="${candidate}"
            break
        fi
    done
fi

if [ -z "${ARMA3_DIR}" ]; then
    echo ""
    echo "Could not auto-detect your Arma 3 directory."
    read -rp "Enter the full path to your Arma 3 server directory: " ARMA3_DIR
fi

echo "Arma 3 directory: ${ARMA3_DIR}"

# ─── 2. Install Node.js if missing ───────────────────────────────────────────
if ! command -v node &>/dev/null; then
    echo "Installing Node.js ${NODE_VERSION}..."
    if command -v apt-get &>/dev/null; then
        curl -fsSL "https://deb.nodesource.com/setup_${NODE_VERSION}.x" | sudo -E bash -
        sudo apt-get install -y nodejs
    elif command -v yum &>/dev/null; then
        curl -fsSL "https://rpm.nodesource.com/setup_${NODE_VERSION}.x" | sudo bash -
        sudo yum install -y nodejs
    else
        echo "ERROR: Cannot auto-install Node.js. Install it manually then re-run."
        exit 1
    fi
fi

echo "Node.js: $(node --version)"

# ─── 3. Install pm2 (process manager — keeps panel running) ──────────────────
if ! command -v pm2 &>/dev/null; then
    echo "Installing pm2..."
    sudo npm install -g pm2
fi

# ─── 4. Create panel directory and copy files ─────────────────────────────────
mkdir -p "${PANEL_DIR}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "${SCRIPT_DIR}")"

if [ -d "${REPO_ROOT}/web" ]; then
    echo "Copying web panel files from ${REPO_ROOT}/web ..."
    cp -r "${REPO_ROOT}/web/." "${PANEL_DIR}/"
else
    echo "ERROR: Could not find the web/ folder at ${REPO_ROOT}/web"
    echo "Make sure you're running this from the arma_server project directory."
    exit 1
fi

# ─── 5. Install npm dependencies ─────────────────────────────────────────────
echo "Installing dependencies..."
cd "${PANEL_DIR}"
npm install --omit=dev

# ─── 6. Create .env if it doesn't exist ──────────────────────────────────────
ENV_FILE="${PANEL_DIR}/.env"
if [ ! -f "${ENV_FILE}" ]; then
    echo ""
    echo "=== Configuration ==="
    read -rp "Panel port (default 8080): " WEB_PORT
    WEB_PORT="${WEB_PORT:-8080}"

    read -rp "Panel admin username (default admin): " WEB_USERNAME
    WEB_USERNAME="${WEB_USERNAME:-admin}"

    # Generate random session secret
    SESSION_SECRET=$(node -e "console.log(require('crypto').randomBytes(32).toString('hex'))")

    cat > "${ENV_FILE}" <<EOF
ARMA3_DIR=${ARMA3_DIR}
WEB_PORT=${WEB_PORT}
WEB_USERNAME=${WEB_USERNAME}
WEB_PASSWORD=changeme
SESSION_SECRET=${SESSION_SECRET}
STEAM_USER=anonymous
STEAM_PASS=
STEAM_OWNER_IDS=
BASE_URL=
SERVER_PORT=2302
EOF
    echo ""
    echo "Created ${ENV_FILE} — edit it to set your passwords and Steam ID."
fi

# ─── 7. Create pm2 ecosystem file ────────────────────────────────────────────
cat > "${PANEL_DIR}/ecosystem.config.js" <<EOF
require('dotenv').config();
module.exports = {
  apps: [{
    name      : 'arma3-manager',
    script    : 'server.js',
    cwd       : '${PANEL_DIR}',
    env_file  : '${ENV_FILE}',
    watch     : false,
    autorestart: true,
    log_date_format: 'YYYY-MM-DD HH:mm:ss',
  }],
};
EOF

# ─── 8. Start / restart the panel ────────────────────────────────────────────
echo ""
echo "Starting Arma 3 Manager panel..."
pm2 stop arma3-manager 2>/dev/null || true
pm2 start "${PANEL_DIR}/ecosystem.config.js"
pm2 save

# Make pm2 start on reboot
pm2 startup 2>/dev/null | grep -E "^sudo" | bash 2>/dev/null || true

# ─── Done ─────────────────────────────────────────────────────────────────────
echo ""
echo "=== Done! ==="
echo ""
echo "Panel is running. Find your server IP with:"
echo "  curl -s ifconfig.me"
echo ""
echo "Then open: http://YOUR_SERVER_IP:${WEB_PORT}"
echo ""
echo "Next steps:"
echo "  1. Edit ${ENV_FILE}  — set WEB_PASSWORD, STEAM_USER, STEAM_OWNER_IDS"
echo "  2. pm2 restart arma3-manager   — apply changes"
echo "  3. pm2 logs arma3-manager      — view live logs"
