# Arma 3 Server Manager

Panel web para administrar un servidor dedicado de Arma 3 con Podman.

Stack actual:

- Frontend estatico servido con nginx
- Backend REST en C# / .NET 10 con Kestrel
- SQLite para estado del panel
- SteamCMD dentro del contenedor del backend
- Podman Compose para levantar frontend + backend

> La implementacion legacy Node/Docker no forma parte del flujo actual.

## Features

- Dashboard con metricas de RAM, disco y grafica de recursos.
- Start, Stop y Restart del servidor.
- Instalacion/actualizacion del servidor dedicado con SteamCMD.
- Login interactivo de SteamCMD con soporte para Steam Guard.
- Instalacion de mods por Workshop ID.
- Importacion de presets HTML de Arma 3 Launcher.
- Modlists guardadas, activables cuando el servidor esta apagado.
- File Manager para navegar, editar, subir y borrar archivos del vault.
- Editor de `server.cfg` y `basic.cfg`.
- Logs del panel, SteamCMD y tareas.
- Settings para cambiar usuario/password del panel y linkear Steam despues.
- Reset de SteamCMD para dejarlo como primera instalacion.

## Requirements

En Windows:

- Windows 10/11
- WSL2
- Podman Desktop o Podman CLI
- Python 3

En Linux:

- Podman 5.x o superior
- Python 3

La cuenta de Steam usada por SteamCMD debe tener Arma 3 en su biblioteca para instalar el server dedicado y descargar Workshop mods.

## Quick Start

1. Copia el archivo de entorno:

```powershell
copy .env.example .env
```

2. Edita `.env`:

```env
WEB_USERNAME=admin
WEB_PASSWORD=changeme123
SESSION_SECRET=replace_with_random_secret
STEAM_USER=your_steam_username
STEAM_PASS=
SERVER_PORT=2302
SERVER_MEM_LIMIT=14g
```

Puedes dejar `STEAM_PASS` vacio y hacer el login interactivo desde el panel.

3. Levanta la plataforma:

```powershell
python manage.py rebuild
```

4. Abre:

```text
http://localhost:8080
```

Login inicial por defecto si no cambiaste `.env`:

```text
admin / changeme123
```

## Comandos

El proyecto incluye `manage.py` como wrapper de Podman Compose.

```powershell
python manage.py start
python manage.py stop
python manage.py restart
python manage.py rebuild
python manage.py status
python manage.py logs
python manage.py delete --yes
python manage.py reset-volumes --yes
```

Que hace cada comando:

| Comando | Accion |
|---|---|
| `start` | Levanta contenedores existentes |
| `stop` | Detiene contenedores sin borrarlos |
| `restart` | Detiene y vuelve a levantar |
| `rebuild` | Reconstruye imagenes y levanta |
| `status` | Muestra estado de contenedores |
| `logs` | Muestra los ultimos logs |
| `delete --yes` | Borra contenedores, conserva volumenes |
| `reset-volumes --yes` | Borra contenedores y volumenes persistentes |

En Windows, si Podman Machine esta apagada, `manage.py` intenta iniciarla automaticamente.

## Reset de Fabrica

Para dejar todo desde cero, incluyendo vault, SteamCMD cache, SQLite, configs, servidor y mods:

```powershell
python manage.py reset-volumes --yes
python manage.py rebuild
```

Esto borra los volumenes:

- `arma3-server`
- `steam-home`
- `steam-config`
- `aspnet-keys`

No borra el codigo del repo.

## SteamCMD

SteamCMD vive dentro del contenedor `arma3-api`.

Flujo recomendado en instalacion limpia:

1. Abre `Mods`.
2. Usa `Reset SteamCMD` si quieres limpiar la sesion/cache.
3. Usa `SteamCMD Login`.
4. Escribe usuario y password de Steam.
5. Si Steam Guard pide verificacion, acepta en el celular o escribe el codigo.
6. Cuando SteamCMD termine bien, instala server/mods.

El estado de Steam se persiste en los volumenes:

```yaml
steam-home:/home/arma3/Steam
steam-config:/home/arma3/.steam
```

Si borras volumenes, tendras que hacer login de SteamCMD otra vez.

## Instalar Servidor de Arma 3

Desde el panel:

1. Entra al Dashboard.
2. Presiona `Install / Update Server`.
3. Si el panel pide SteamCMD login, completa el flujo de Steam Guard.
4. Reintenta la instalacion.

Steam AppID usado:

```text
233780
```

Workshop AppID usado para mods:

```text
107410
```

## Puertos

El panel web usa TCP:

```text
8080/tcp
```

Arma 3 usa UDP:

```text
2302/udp
2303/udp
2304/udp
2305/udp
```

En casa, abre/forwardea esos puertos UDP hacia la PC o servidor donde corre Podman.

En VPS/dedicado, abre esos puertos en firewall/security group.

## Volumenes

`podman-compose.yml` usa volumenes nombrados:

```yaml
volumes:
  - arma3-server:/arma3
  - steam-home:/home/arma3/Steam
  - steam-config:/home/arma3/.steam
  - aspnet-keys:/home/arma3/.aspnet
```

`/arma3` es el vault principal:

- binarios del server
- mods
- configs
- perfiles
- misiones
- SQLite del panel

## Variables de Entorno

Variables principales en `.env`:

| Variable | Uso |
|---|---|
| `WEB_PORT` | Puerto HTTP del panel |
| `WEB_USERNAME` | Usuario inicial del panel |
| `WEB_PASSWORD` | Password inicial del panel |
| `SESSION_SECRET` | Secreto de sesiones |
| `STEAM_USER` | Usuario SteamCMD sugerido |
| `STEAM_PASS` | Password SteamCMD opcional |
| `SERVER_PORT` | Puerto base UDP de Arma |
| `SERVER_QUERY_PORT` | Puerto query UDP |
| `BATTLEYE_PORT` | Puerto BattlEye UDP |
| `VON_PORT` | Puerto VON UDP |
| `SERVER_NAME` | Nombre por defecto del servidor |
| `SERVER_PASSWORD` | Password de jugadores |
| `SERVER_PASSWORD_ADMIN` | Password admin de Arma |
| `SERVER_MAX_PLAYERS` | Max players por defecto |
| `SERVER_MEM_LIMIT` | Limite de RAM del contenedor API/server |
| `BASE_URL` | URL publica para callback Steam OpenID |
| `STEAM_OWNER_IDS` | Steam64 IDs permitidos para link/login Steam |
| `TZ` | Timezone |

Despues de cambiar `.env`, reconstruye:

```powershell
python manage.py rebuild
```

## Estructura

```text
arma_server/
  backend/Arma3Manager.Api/   REST API .NET 10 / Kestrel
  config/                     configs base server.cfg y basic.cfg
  frontend/nginx.conf         nginx para frontend y proxy API
  web/public/                 SPA HTML/CSS/JS
  Containerfile.api           imagen backend + SteamCMD + runtime Arma
  Containerfile.frontend      imagen nginx frontend
  podman-compose.yml          stack Podman
  manage.py                   comandos locales
  .env.example                plantilla de configuracion
```

## Preparar Repo en GitHub

Antes de subir:

1. Verifica que `.env` no exista en Git:

```powershell
git status --short
```

2. Inicializa repo si aun no existe:

```powershell
git init
```

3. Agrega archivos:

```powershell
git add .
```

4. Revisa que no se agreguen secretos ni dependencias:

```powershell
git status --short
```

No deberias ver:

- `.env`
- `web/node_modules/`
- `__pycache__/`
- archivos `.pdb`, `bin/`, `obj/`

5. Commit inicial:

```powershell
git commit -m "Initial Arma 3 server manager"
```

6. Crea un repo vacio en GitHub.

7. Conecta remoto:

```powershell
git branch -M main
git remote add origin https://github.com/TU_USUARIO/TU_REPO.git
git push -u origin main
```

## Probar Antes de Subir

```powershell
python -m py_compile manage.py
node --check web/public/js/app.js
python manage.py rebuild
python manage.py status
```

Luego abre:

```text
http://localhost:8080
```

Prueba minimo:

- Login del panel.
- Navegar a `#mods` y refrescar con F5.
- `Reset SteamCMD` muestra confirmacion.
- Dashboard actualiza metricas.
- `Install / Update Server` pide SteamCMD login si no hay sesion.

