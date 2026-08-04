# Arma 3 Server Manager

**Languages:** [Español](README.md) · [English](README.en.md) · [Deutsch](README.de.md)

Web panel to install, update, and operate a dedicated Arma 3 server, its
mods, Creator DLCs, files, configuration, and SteamCMD sessions.

## Architecture

| Layer | Technology |
|---|---|
| Frontend | Astro 7 + Vue 3, static output |
| Web proxy | Nginx Alpine |
| Backend | ASP.NET Core / .NET 10 |
| State | SQLite |
| Configuration | TOML |
| Containers | Podman |
| Deployment | Python, SSH, and remote Podman |

```text
backend/Arma3Manager.Api/
├── Application/       application processes and utilities
├── Configuration/     TOML reading and validation
├── Contracts/         HTTP and persistence contracts
├── Domain/            server models and routes
├── Endpoints/         REST API and authentication
├── Infrastructure/    SQLite, metrics, and filesystem
├── Security/          credentials
└── Program.cs         composition root

web/
├── src/                Astro, Vue, and panel logic
├── public/             static assets
└── dist/               reproducible output, not versioned
```

## Configuration

The application no longer uses `.env` files.

1. Edit `config/manager.toml` for ports, network, paths, and limits.
2. Create the private file:

```bash
cp config/manager.secrets.example.toml config/manager.secrets.toml
chmod 600 config/manager.secrets.toml
```

3. Replace the panel password and the session secret.

You can generate a session secret with:

```bash
python3 -c "import secrets; print(secrets.token_hex(32))"
```

`manager.secrets.toml` is gitignored and never copied inside an image.
During deployment it's transferred via SCP and mounted read-only.

SQLite is kept at `/arma3/manager.sqlite3` and stores editable preferences,
mods, modlists, and authentication metadata. Large files continue to live in
the `/arma3` volume.

### Main sections

- `[web]`: internal API port, public port, bind, URL, and initial account.
- `[server]`: directories, UDP ports, network, and memory limit.
- `[steam]`: user, authorized Steam IDs, and Creator DLC app IDs.
- `[runtime]`: timezone and simulators for testing.

For host networking, use `config/manager.host.toml` with the
`podman-compose.host.yml` overlay.

## Development

Requirements: .NET 10, Node.js 24, Python 3.11+, Doxygen, and Podman.

Backend:

```bash
dotnet build Arma3Manager.slnx
dotnet test Arma3Manager.slnx
```

Frontend:

```bash
cd web
npm install
npm run dev
npm run build
```

Astro's output is fully static. Node.js is only involved at build time;
production serves `web/dist` through Nginx.

## Local Podman

```bash
podman compose -f podman-compose.yml up -d --build
podman compose -f podman-compose.yml ps
podman compose -f podman-compose.yml logs --tail 200
```

Host mode:

```bash
podman compose -f podman-compose.yml -f podman-compose.host.yml up -d --build
```

Persistent volumes:

- `arma3-server`
- `steam-home`
- `steam-config`
- `aspnet-keys`

Removing or recreating containers does not delete these volumes.

### Full reset from the API

The authenticated endpoint `POST /api/system/factory-reset` deletes all
persistent content managed by the application: server, mods, SQLite,
SteamCMD credentials, Steam configuration, and session keys. It requires the
game server and all maintenance tasks to be stopped.

```json
{
  "currentPassword": "current-panel-password",
  "confirmation": "RESET ALL ARMA3 DATA"
}
```

The request creates an atomic marker and restarts Kestrel. Before opening
SQLite on the next startup, the backend empties the four volumes and only
removes the marker once every operation completes successfully. If the
process is interrupted, the next startup retries it.

The container image is immutable and stores neither mods nor panel state;
that's why the API is not granted access to Podman's root socket. To use a
new image version, run the normal deployment after the reset.

## Remote deployment

Create the local target configuration:

```bash
cp deploy.example.toml deploy.toml
```

```toml
[dev]
server = "192.168.1.20"
username = "arma3"

[prod]
server = "203.0.113.20"
username = "arma3"
```

Selective deployments:

```bash
python3 deploy.py dev --check
python3 deploy.py dev --check --force   # dev only: skips only the 0600 mode check
python3 deploy.py dev --frontend
python3 deploy.py dev --backend
python3 deploy.py prod --frontend
python3 deploy.py prod --backend --yes
python3 deploy.py prod --frontend --backend --yes
```

Remote operation:

```bash
python3 deploy.py prod --status
python3 deploy.py prod --logs backend
python3 deploy.py prod --logs frontend
```

The script:

1. validates TOML, SSH, `rsync`, and Podman;
2. transfers a release with `rsync`, without secrets or build artifacts,
   while showing progress and every changed file;
3. builds only the requested image on the Linux server;
4. preserves volumes;
5. replaces only the selected container;
6. waits for the health check;
7. restores the previous image if the update fails;
8. restarts the frontend after a backend-only deploy so Nginx resolves the
   new API container's address;
9. removes old, container-less images only when they carry the
   `project=arma3-manager` label; it never runs a global prune or touches
   unrelated images.

Every SSH operation is printed before it runs. Driver and Podman diagnostics
stream to the terminal even when stdout is reserved for parsing the contract's
JSON responses.

To reach the development panel from another computer, change `web.bind_ip`
to `"0.0.0.0"`. Keep `"127.0.0.1"` if the panel will sit behind a local
reverse proxy.

Updating the backend stops the Arma 3 process because it currently lives in
the API container. That's why it requires confirmation or `--yes`.

## Backend documentation

```bash
doxygen Doxyfile
```

The HTML documentation is generated at
`docs/generated/backend/html/index.html`. The `Doxyfile` fails on
documentation errors and excludes `bin/` and `obj/`.

## API and security

- `/api/health` is public for health checks.
- `/api/auth/*` and `/auth/steam/*` manage sessions.
- The rest of `/api/*` requires authentication.
- The file editor restricts paths to `/arma3`.
- SQLite and its WAL/SHM files are protected from the panel.
- Steam credentials are redacted in command logs.

## Pre-production verification

```bash
dotnet test Arma3Manager.slnx
cd web && npm run build
cd .. && doxygen Doxyfile
python3 -m py_compile deploy.py
podman build -f Containerfile.api -t arma3-manager-api:test .
podman build -f Containerfile.frontend -t arma3-manager-frontend:test .
```
