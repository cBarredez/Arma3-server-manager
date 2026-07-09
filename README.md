# Arma 3 Server Manager

![Backend](https://img.shields.io/badge/backend-.NET%2010-512BD4)
![Frontend](https://img.shields.io/badge/frontend-nginx%20%2B%20SPA-009639)
![Runtime](https://img.shields.io/badge/runtime-Podman-892CA0)
![Database](https://img.shields.io/badge/database-SQLite-003B57)

Web panel to manage a dedicated Arma 3 server using Podman, SteamCMD, .NET/Kestrel, SQLite, and a static frontend served by nginx.

The goal of the project is to provide a portable platform that can run locally in WSL/Podman and later be deployed to a dedicated server or VPS.

---

## Table of Contents

- [Stack](#stack)
- [Features](#features)
- [Requirements](#requirements)
- [Quick Start](#quick-start)
- [Operation Commands](#operation-commands)
- [SteamCMD](#steamcmd)
- [Install Arma 3 Server](#install-arma-3-server)
- [Creator DLCs](#creator-dlcs)
- [Ports](#ports)
- [Which IP To Use](#which-ip-to-use)
- [Volumes and Persistent Data](#volumes-and-persistent-data)
- [Factory Reset](#factory-reset)
- [Environment Variables](#environment-variables)
- [Project Structure](#project-structure)
- [Testing Local Changes](#testing-local-changes)
- [Prepare GitHub](#prepare-github)

---

## Stack

| Layer       | Technology                  |
|-------------|-----------------------------|
| Frontend    | HTML/CSS/JS SPA             |
| Web server  | nginx                       |
| Backend     | C# / .NET 10 / Kestrel      |
| API         | REST                        |
| State       | SQLite                      |
| Runtime     | Podman Compose              |
| Steam       | SteamCMD inside API container |

---

## Features

- Dashboard with RAM, disk, and resource usage graphs.
- Start, Stop, and Restart server actions.
- Install and update dedicated server via SteamCMD.
- Interactive SteamCMD login with Steam Guard support.
- Install mods via Workshop ID.
- Import Arma 3 Launcher HTML presets.
- Save and activate modlists when server is offline.
- File Manager to browse, edit, upload, and delete files.
- Editor for `server.cfg` and `basic.cfg`.
- Logs for panel, SteamCMD, and tasks.
- Settings to change panel username/password.
- Steam OpenID linking.
- Reset SteamCMD to clear session/cache.

---

## Requirements

### Windows
- Windows 10/11
- WSL2
- Podman Desktop or Podman CLI
- Python 3

### Linux
- Podman 5.x or higher
- Python 3

### Steam
The SteamCMD account must own Arma 3 to:
- install/update the dedicated server
- download mods from Steam Workshop

---

## Quick Start

1. Clone the repo:
   ```powershell
   git clone https://github.com/cBarredez/Arma3-server-manager.git
   cd Arma3-server-manager
   ```

2. Review the public template:
   ```powershell
   notepad .env
   ```

3. Start Podman if needed:
   ```powershell
   podman machine start
   ```

4. Build and start the platform:
   ```powershell
   python manage.py rebuild
   ```

5. Open the panel:
   ```text
   http://localhost:8080
   ```

Default login values are defined in `.env`. Change `WEB_PASSWORD`,
`SESSION_SECRET`, and `SERVER_PASSWORD_ADMIN` before deploying anywhere real.

---

## Operation Commands

Use `manage.py` as the main local operator:

```powershell
python manage.py start
python manage.py stop
python manage.py restart
python manage.py rebuild
python manage.py logs
python manage.py status
```

Use a specific env file:

```powershell
python manage.py --env-file .env.public-test start
python manage.py --env-file .env.public-test rebuild
```

Create a public-test env from the safe template:

```powershell
copy .env.public-test.example .env.public-test
notepad .env.public-test
```

`PUBLIC_JOIN_HOST` should be your public IPv4 or DNS name. Keep the web panel
bound to `127.0.0.1` unless you intentionally want to expose it.

---

## SteamCMD

The panel includes an interactive SteamCMD login window under **Mods**. Leave
`STEAM_PASS` blank and log in through the UI when Steam Guard is required.

SteamCMD state is stored in Podman volumes:

- `steam-home`
- `steam-config`

The Arma 3 server files, mods, presets, SQLite database, and configs are stored
in the `arma3-server` volume.

---

## Install Arma 3 Server

After signing into SteamCMD:

1. Go to **Dashboard**.
2. Click **Install / Update Server**.
3. Watch **Server Logs** until SteamCMD exits successfully.

Workshop mods can be installed from **Mods** by Workshop ID or by importing an
Arma 3 Launcher HTML preset.

Saved HTML presets are stored under:

```text
/arma3/presets/modlists
```

---

## Creator DLCs

Creator DLCs are managed in **Mods -> Creator DLCs**.

- **Download DLCs** runs SteamCMD for Creator DLC server files.
- Available DLC folders are detected in `/arma3`.
- Enabled DLCs are added to the server startup `-mod=` list.

Known folders currently detected:

```text
gm, vn, csla, ws, spe, rf, ef
```

---

## Ports

The management panel is exposed on TCP `8080` by default and is bound to
`127.0.0.1` when `WEB_BIND_IP=127.0.0.1`.

The Arma 3 game server uses UDP ports:

```text
2302 game
2303 query
2304 BattlEye
2305 VON
```

For external players, forward those UDP ports from your router/firewall to the
server LAN IP, then set:

```env
PUBLIC_JOIN_HOST=YOUR_PUBLIC_IP_OR_DOMAIN
```

---

## Which IP To Use

Use a different address depending on where the player or admin is connecting
from:

| Scenario | Address to use | Purpose |
|----------|----------------|---------|
| Same PC as the server | `127.0.0.1:2302` | Join the Arma 3 server locally |
| Same home/LAN network | `YOUR_SERVER_LAN_IP:2302` | Join from another PC on the same router |
| Outside your network | `YOUR_PUBLIC_IP_OR_DOMAIN:2302` | Join from the internet |
| Web panel on server PC | `http://127.0.0.1:8080` | Manage the server privately |

Recommended public-test setup:

```env
WEB_BIND_IP=127.0.0.1
BASE_URL=http://127.0.0.1:8080
PUBLIC_JOIN_HOST=YOUR_PUBLIC_IP_OR_DOMAIN
SERVER_PORT=2302
```

Router/firewall rules for public players:

```text
UDP 2302 -> YOUR_SERVER_LAN_IP
UDP 2303 -> YOUR_SERVER_LAN_IP
UDP 2304 -> YOUR_SERVER_LAN_IP
UDP 2305 -> YOUR_SERVER_LAN_IP
```

Do not expose the web panel publicly unless you add proper network protection.
The game ports can be public; the panel should usually stay local/private.

---

## Volumes and Persistent Data

Rebuilds do not delete downloaded data. These Podman volumes persist across
container rebuilds/restarts:

```text
arma3-server
steam-home
steam-config
aspnet-keys
```

The SQLite database lives at:

```text
/arma3/manager.sqlite3
```

---

## Factory Reset

To reset SteamCMD from the panel, use **Settings -> SteamCMD Factory Setup**.

To remove all Podman data manually, stop the platform first and remove the
volumes with care. This deletes server files, mods, presets, config, and the
SQLite database.

---

## Environment Variables

Committed env files:

- `.env`: safe base template for normal local/server usage.
- `.env.public-test.example`: safe template for public connectivity testing.

Ignored env files:

- `.env.public-test`: local copy that may contain your public IP.
- `.env.local`, `.env.private`, and other `.env.*` files.

Important values:

| Variable | Purpose |
|----------|---------|
| `WEB_BIND_IP` | Bind web panel to localhost or another interface |
| `BASE_URL` | Panel callback URL |
| `PUBLIC_JOIN_HOST` | Address players use to join the game server |
| `SERVER_PORT` | Main Arma 3 UDP port |
| `SERVER_MAX_PLAYERS` | Default max players |
| `SERVER_MEM_LIMIT` | Podman memory limit for API/game container |
| `CREATOR_DLC_APP_IDS` | Optional extra app IDs for the DLC download action |

---

## Project Structure

```text
backend/Arma3Manager.Api/   .NET 10 REST API
frontend/                   nginx config and startup script
web/public/                 static SPA frontend
config/                     default Arma server config
manage.py                   Podman helper CLI
podman-compose.yml          Podman Compose-compatible definition
Containerfile.api           backend/API image
Containerfile.frontend      frontend/nginx image
```

---

## Testing Local Changes

Build and run:

```powershell
python manage.py rebuild
```

Inspect logs:

```powershell
python manage.py logs
```

Check running containers:

```powershell
podman ps
```

---

## GitHub Notes

Do not commit real credentials, Steam passwords, Steam owner IDs, private IPs, or
public IPs tied to your home network. Use `.env.public-test.example` as the file
to share with other server operators.
