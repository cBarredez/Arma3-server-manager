# .NET/Kestrel + SQLite + Podman migration

The current runtime is a separated frontend/backend stack using Podman, nginx, ASP.NET Core/Kestrel, and SQLite. The older Node-based implementation has been removed from the active repository.

## Layout

- `backend/Arma3Manager.Api`: REST API on ASP.NET Core/Kestrel targeting .NET 10.
- `web/public`: static frontend assets.
- `Containerfile.api`: builds/runs the .NET backend with SteamCMD and Arma 3 runtime dependencies.
- `Containerfile.frontend`: serves the frontend through nginx.
- `podman-compose.yml`: runs `api` and `frontend` as separate services.

## SQLite state

The .NET backend stores manager state in:

```text
/arma3/manager.sqlite3
```

The database is for manager metadata only:

- startup settings
- SteamCMD username/session metadata
- mods registry
- modlists
- active modlist setting
- future task history

It does not store real server files, downloaded mods, missions, keys, or large logs.

On first boot, the backend imports existing JSON files if present:

- `/arma3/startup.json`
- `/arma3/steamcmd-auth.json`

The existing game/server files stay in the `/arma3` volume.

## Run with Podman

```bash
podman compose -f podman-compose.yml up --build
```

Open:

```text
http://localhost:8080
```

The frontend container proxies these paths to the backend container:

- `/api/*`
- `/auth/*`

## Resource limits

The API/game-server container has a compose memory cap controlled by:

```env
SERVER_MEM_LIMIT=14g
```

On Windows/WSL, the WSL VM memory limit is controlled by `%USERPROFILE%\.wslconfig`:

```ini
[wsl2]
memory=14GB
```

The Podman WSL disk was resized with:

```powershell
wsl --shutdown
wsl --manage podman-machine-default --resize 200GB
```

## Windows troubleshooting

If PowerShell says `podman` is not recognized, Podman may be installed but not on `PATH`.

Check the common user install path:

```powershell
& "$env:LOCALAPPDATA\Programs\Podman\podman.exe" --version
```

Add it to your user `PATH`, then open a new terminal:

```powershell
[Environment]::SetEnvironmentVariable(
  "Path",
  [Environment]::GetEnvironmentVariable("Path", "User") + ";$env:LOCALAPPDATA\Programs\Podman",
  "User"
)
```

If `podman machine list` fails with `mkdir ...\.config: Cannot create a file when that file already exists`, inspect `~\.config`. Podman expects it to be a directory:

```powershell
$cfg = "$env:USERPROFILE\.config"
Get-Item -Force $cfg | Format-List FullName,Mode,Length,Attributes
```

If it is a file, back it up and create the directory:

```powershell
Rename-Item -LiteralPath $cfg -NewName ".config.backup"
New-Item -ItemType Directory -Path $cfg
```

Then initialize/start the Podman VM:

```powershell
podman machine init
podman machine start
podman machine list
```

## Local .NET development

This project targets `.NET 10`. Install the .NET 10 SDK, then:

```bash
dotnet restore backend/Arma3Manager.Api/Arma3Manager.Api.csproj
dotnet run --project backend/Arma3Manager.Api/Arma3Manager.Api.csproj
```

For separated local frontend/backend development, set:

```js
window.ARMA3_API_BASE = 'http://localhost:8080';
window.ARMA3_REST_ONLY = true;
```

or use the nginx frontend container, which writes those values into `/config.js` at startup.

## Current migration status

The new backend covers the same REST surface used by the frontend and persists manager state to SQLite. The previous Socket.IO live updates are intentionally not carried over yet; the frontend now supports REST-only polling mode for the separated deployment.

Recommended next step: replace REST-only polling with SignalR if live logs/progress updates become important in the .NET backend.
