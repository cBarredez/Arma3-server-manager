# Arma 3 Server Manager

![Backend](https://img.shields.io/badge/backend-.NET%2010-512BD4)
![Frontend](https://img.shields.io/badge/frontend-nginx%20%2B%20SPA-009639)
![Runtime](https://img.shields.io/badge/runtime-Podman-892CA0)
![Database](https://img.shields.io/badge/database-SQLite-003B57)

Panel web para administrar un servidor dedicado de Arma 3 usando Podman, SteamCMD, .NET/Kestrel, SQLite y un frontend estatico servido por nginx.

El objetivo del proyecto es levantar una plataforma portable que pueda correr localmente en WSL/Podman y despues moverse a un servidor dedicado o VPS.

## Tabla de Contenidos

- [Stack](#stack)
- [Features](#features)
- [Requisitos](#requisitos)
- [Inicio Rapido](#inicio-rapido)
- [Comandos de Operacion](#comandos-de-operacion)
- [SteamCMD](#steamcmd)
- [Instalar el Servidor de Arma 3](#instalar-el-servidor-de-arma-3)
- [Puertos](#puertos)
- [Volumenes y Datos Persistentes](#volumenes-y-datos-persistentes)
- [Reset de Fabrica](#reset-de-fabrica)
- [Variables de Entorno](#variables-de-entorno)
- [Estructura del Proyecto](#estructura-del-proyecto)
- [Preparar GitHub](#preparar-github)

## Stack

| Capa | Tecnologia |
|---|---|
| Frontend | HTML/CSS/JS SPA |
| Web server | nginx |
| Backend | C# / .NET 10 / Kestrel |
| API | REST |
| Estado | SQLite |
| Runtime | Podman Compose |
| Steam | SteamCMD dentro del contenedor API |

> El flujo actual usa Podman, Containerfile, nginx, .NET/Kestrel y SQLite.

## Features

- Dashboard con metricas de RAM, disco y grafica de recursos.
- Start, Stop y Restart del servidor.
- Instalacion y actualizacion del servidor dedicado via SteamCMD.
- Login interactivo de SteamCMD con soporte para Steam Guard.
- Instalacion de mods por Workshop ID.
- Importacion de presets HTML de Arma 3 Launcher.
- Modlists guardadas y activables cuando el servidor esta apagado.
- File Manager para navegar, editar, subir y borrar archivos del vault.
- Editor de `server.cfg` y `basic.cfg`.
- Logs de panel, SteamCMD y tareas.
- Settings para cambiar usuario/password del panel.
- Link posterior con Steam OpenID.
- Reset de SteamCMD para dejar la sesion como primera instalacion.

## Requisitos

### Windows

- Windows 10/11
- WSL2
- Podman Desktop o Podman CLI
- Python 3

### Linux

- Podman 5.x o superior
- Python 3

### Steam

La cuenta usada por SteamCMD debe tener Arma 3 en su biblioteca para:

- instalar/actualizar el servidor dedicado
- descargar mods de Steam Workshop

## Inicio Rapido

1. Clona el repo:

```powershell
git clone https://github.com/cBarredez/Arma3-server-manager.git
cd Arma3-server-manager
```

2. Crea tu archivo `.env`:

```powershell
copy .env.example .env
```

3. Edita `.env`:

```env
WEB_USERNAME=admin
WEB_PASSWORD=changeme123
SESSION_SECRET=replace_with_random_secret
STEAM_USER=your_steam_username
STEAM_PASS=
SERVER_PORT=2302
SERVER_MEM_LIMIT=14g
```

Puedes dejar `STEAM_PASS` vacio y hacer login interactivo desde el panel.

4. Levanta la plataforma:

```powershell
python manage.py rebuild
```

5. Abre el panel:

```text
http://localhost:8080
```

Login inicial si no cambiaste `.env`:

```text
admin / changeme123
```

## Comandos de Operacion

El proyecto incluye `manage.py`, un wrapper para Podman Compose.

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

## SteamCMD

SteamCMD corre dentro del contenedor `arma3-api`.

Flujo recomendado para instalacion limpia:

1. Abre `Mods`.
2. Presiona `Reset SteamCMD` si quieres limpiar cache/sesion.
3. Presiona `SteamCMD Login`.
4. Escribe usuario y password de Steam.
5. Si Steam Guard pide verificacion, acepta en el celular o escribe el codigo.
6. Cuando SteamCMD termine con exito, instala servidor o mods.

El estado de SteamCMD se guarda en:

```yaml
steam-home:/home/arma3/Steam
steam-config:/home/arma3/.steam
```

Si borras esos volumenes, tendras que hacer login de SteamCMD otra vez.

## Instalar el Servidor de Arma 3

Desde el panel:

1. Entra al Dashboard.
2. Presiona `Install / Update Server`.
3. Si el panel pide SteamCMD login, completa Steam Guard.
4. Reintenta la instalacion.

AppIDs usados:

| Recurso | AppID |
|---|---|
| Arma 3 Dedicated Server | `233780` |
| Arma 3 Workshop | `107410` |

## Puertos

Panel web:

```text
8080/tcp
```

Arma 3:

```text
2302/udp
2303/udp
2304/udp
2305/udp
```

Para jugadores externos:

- En casa: forwardea esos puertos UDP en tu router hacia la maquina que corre Podman.
- En VPS/dedicado: abre esos puertos en firewall o security group.

## Volumenes y Datos Persistentes

`podman-compose.yml` usa volumenes nombrados:

```yaml
volumes:
  - arma3-server:/arma3
  - steam-home:/home/arma3/Steam
  - steam-config:/home/arma3/.steam
  - aspnet-keys:/home/arma3/.aspnet
```

`/arma3` es el vault principal:

- binarios del servidor
- mods
- configs
- perfiles
- misiones
- base SQLite del panel

## Reset de Fabrica

Para dejar todo desde cero, incluyendo vault, SteamCMD cache, SQLite, configs, servidor y mods:

```powershell
python manage.py reset-volumes --yes
python manage.py rebuild
```

Esto borra:

- `arma3-server`
- `steam-home`
- `steam-config`
- `aspnet-keys`

No borra el codigo local del repo.

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

## Estructura del Proyecto

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

## Probar Cambios Locales

```powershell
python -m py_compile manage.py
node --check web/public/js/app.js
python manage.py rebuild
python manage.py status
```

Pruebas minimas:

- Login del panel.
- Navegar a `#mods` y refrescar con F5.
- `Reset SteamCMD` muestra confirmacion.
- Dashboard actualiza metricas.
- `Install / Update Server` pide SteamCMD login si no hay sesion.

## Preparar GitHub

Antes de commitear:

```powershell
git status --short
```

No deberias ver:

- `.env`
- `web/node_modules/`
- `__pycache__/`
- `bin/`
- `obj/`

Flujo de commit:

```powershell
git add .
git commit -m "Update project documentation"
git push
```
