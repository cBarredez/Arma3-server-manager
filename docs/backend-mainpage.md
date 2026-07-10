# Backend de Arma 3 Server Manager

El backend expone una API REST estable sobre ASP.NET Core y administra el proceso
del servidor dedicado, SteamCMD, mods, archivos, métricas y configuración.

## Organización

- `Configuration`: carga y valida la configuración TOML de arranque.
- `Contracts`: modelos intercambiados por la API y persistidos como metadatos.
- `Domain`: rutas detectadas y configuración mutable de inicio de ARMA 3.
- `Endpoints`: superficie HTTP y autenticación.
- `Application`: procesos, comandos, archivos protegidos y presets.
- `Infrastructure`: SQLite, métricas del contenedor y reparación de mods.
- `Security`: derivación y verificación de credenciales.

## Configuración y estado

La configuración de infraestructura se lee desde `config/manager.toml`. Los
secretos pueden superponerse mediante `config/manager.secrets.toml`. SQLite
conserva únicamente estado mutable del gestor; los archivos del servidor y mods
permanecen en el volumen `/arma3`.

## Seguridad

Todas las rutas bajo `/api`, excepto salud y autenticación, requieren una sesión
válida. Las operaciones de archivos pasan por `PathGuard` y la base SQLite está
protegida contra edición o eliminación desde el panel.

## Generación

```bash
doxygen Doxyfile
```
