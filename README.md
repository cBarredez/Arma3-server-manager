# Arma 3 Server Manager

Panel web para instalar, actualizar y operar un servidor dedicado de Arma 3, sus
mods, Creator DLCs, archivos, configuración y sesiones de SteamCMD.

## Arquitectura

| Capa | Tecnología |
|---|---|
| Frontend | Astro 7 + Vue 3, salida estática |
| Proxy web | Nginx Alpine |
| Backend | ASP.NET Core / .NET 10 |
| Estado | SQLite |
| Configuración | TOML |
| Contenedores | Podman |
| Despliegue | Python, SSH y Podman remoto |

```text
backend/Arma3Manager.Api/
├── Application/       procesos y utilidades de aplicación
├── Configuration/     lectura y validación TOML
├── Contracts/         contratos HTTP y persistencia
├── Domain/            modelos y rutas del servidor
├── Endpoints/         API REST y autenticación
├── Infrastructure/    SQLite, métricas y sistema de archivos
├── Security/          credenciales
└── Program.cs         composition root

web/
├── src/                Astro, Vue y lógica del panel
├── public/             archivos estáticos
└── dist/               salida reproducible, no versionada
```

## Configuración

La aplicación ya no utiliza archivos `.env`.

1. Edita `config/manager.toml` para puertos, red, rutas y límites.
2. Crea el archivo privado:

```bash
cp config/manager.secrets.example.toml config/manager.secrets.toml
chmod 600 config/manager.secrets.toml
```

3. Reemplaza la contraseña del panel y el secreto de sesión.

Puedes generar un secreto de sesión con:

```bash
python3 -c "import secrets; print(secrets.token_hex(32))"
```

`manager.secrets.toml` está ignorado por Git y nunca se copia dentro de una
imagen. Durante el despliegue se transfiere por SCP y se monta como solo lectura.

SQLite se conserva en `/arma3/manager.sqlite3` y almacena preferencias
editables, mods, modlists y metadatos de autenticación. Los archivos grandes
continúan en el volumen `/arma3`.

### Secciones principales

- `[web]`: puerto interno del API, puerto público, bind, URL y cuenta inicial.
- `[server]`: directorios, puertos UDP, red y límite de memoria.
- `[steam]`: usuario, Steam IDs autorizados y Creator DLC app IDs.
- `[runtime]`: zona horaria y simuladores para pruebas.

Para red host utiliza `config/manager.host.toml` con el overlay
`podman-compose.host.yml`.

## Desarrollo

Requisitos: .NET 10, Node.js 24, Python 3.11+, Doxygen y Podman.

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

La salida de Astro es completamente estática. Node.js sólo participa en la
compilación; producción sirve `web/dist` mediante Nginx.

## Podman local

```bash
podman compose -f podman-compose.yml up -d --build
podman compose -f podman-compose.yml ps
podman compose -f podman-compose.yml logs --tail 200
```

Modo host:

```bash
podman compose -f podman-compose.yml -f podman-compose.host.yml up -d --build
```

Volúmenes persistentes:

- `arma3-server`
- `steam-home`
- `steam-config`
- `aspnet-keys`

Eliminar o recrear contenedores no elimina estos volúmenes.

## Despliegue remoto

Crea la configuración local de destinos:

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

Despliegues selectivos:

```bash
python3 deploy.py dev --check
python3 deploy.py dev --frontend
python3 deploy.py dev --backend
python3 deploy.py prod --frontend
python3 deploy.py prod --backend --yes
python3 deploy.py prod --frontend --backend --yes
```

Operación remota:

```bash
python3 deploy.py prod --status
python3 deploy.py prod --logs backend
python3 deploy.py prod --logs frontend
```

El script:

1. valida TOML, SSH y Podman;
2. transfiere una release sin secretos ni artefactos;
3. construye sólo la imagen solicitada en el servidor Linux;
4. conserva volúmenes;
5. reemplaza únicamente el contenedor seleccionado;
6. espera el health check;
7. restaura la imagen anterior si la actualización falla.

Para acceder al panel de desarrollo desde otra computadora, cambia
`web.bind_ip` a `"0.0.0.0"`. Conserva `"127.0.0.1"` si el panel estará detrás
de un proxy inverso local.

Actualizar el backend detiene el proceso de Arma 3 porque actualmente vive en el
contenedor API. Por eso requiere confirmación o `--yes`.

## Documentación backend

```bash
doxygen Doxyfile
```

La documentación HTML se genera en
`docs/generated/backend/html/index.html`. El archivo `Doxyfile` falla ante
errores de documentación y excluye `bin/` y `obj/`.

## API y seguridad

- `/api/health` es público para health checks.
- `/api/auth/*` y `/auth/steam/*` administran sesiones.
- El resto de `/api/*` requiere autenticación.
- El editor de archivos restringe rutas a `/arma3`.
- SQLite y sus archivos WAL/SHM están protegidos desde el panel.
- Las credenciales de Steam se redactan en logs de comandos.

## Verificación antes de producción

```bash
dotnet test Arma3Manager.slnx
cd web && npm run build
cd .. && doxygen Doxyfile
python3 -m py_compile deploy.py
podman build -f Containerfile.api -t arma3-manager-api:test .
podman build -f Containerfile.frontend -t arma3-manager-frontend:test .
```
