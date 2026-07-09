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
- [Ports](#ports)
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
