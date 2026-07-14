# Arquitectura .NET, Astro y Podman

## Flujo de ejecución

Nginx sirve el frontend estático producido por Astro y redirige `/api/*` y
`/auth/*` a Kestrel. El backend inicia SteamCMD y el servidor dedicado como
procesos hijos, mientras SQLite guarda únicamente metadatos del gestor.

## Límites de responsabilidad

- TOML contiene configuración necesaria antes de abrir SQLite.
- SQLite contiene estado mutable generado desde la interfaz.
- Los secretos se montan desde un archivo privado, nunca desde la imagen.
- Los archivos de Arma 3 permanecen en un volumen Podman.
- Astro y Vue no requieren un proceso Node en producción.

## Contenedores

`Containerfile.api` utiliza una compilación multi-stage de .NET y una imagen
runtime que contiene SteamCMD y dependencias Linux de Arma 3.

`Containerfile.frontend` compila Astro/Vue con Node y copia exclusivamente
`dist/` a Nginx. Chart.js se descarga únicamente al abrir el Dashboard y
Bootstrap JS se limita a Modal y Toast.

## Red

En modo bridge, Nginx resuelve `arma3-api:8080` dentro de `arma3-net` y
Podman publica los puertos UDP 2302–2305.

En modo host, el API utiliza `manager.host.toml` y escucha en 8081 para no
colisionar con el frontend público en 8080.

## Persistencia

```text
arma3-server  -> /arma3
steam-home    -> /home/arma3/Steam
steam-config  -> /home/arma3/.steam
aspnet-keys   -> /home/arma3/.aspnet
```

El despliegue reemplaza contenedores, no volúmenes. La base
`/arma3/manager.sqlite3` se conserva junto a los archivos del servidor.

## Compatibilidad

Las rutas REST, métodos, nombres de funciones del cliente y estructuras JSON se
mantuvieron durante la migración. El frontend utiliza polling REST para estado y
Server-Sent Events para logs.

## Flujo de logs en Linux

El proceso `arma3server_x64` entrega su salida RPT por `stdout` y `stderr`; el
backend usa esa salida como fuente canónica y no vuelve a leer archivos `.rpt`.
Cada línea entra en un buffer circular de 5,000 eventos con un ID estable. El
cliente recupera primero un snapshot por REST y continúa por SSE usando ese ID.
Si el cliente queda por detrás del buffer, recibe un evento `gap` y vuelve a
cargar el historial disponible en lugar de ocultar la pérdida.

La ruta `/api/logs/stream` envía un heartbeat cada 15 segundos. Nginx desactiva
buffering, caché, compresión y cierra el upstream si pasan 45 segundos sin datos.
Un proxy inverso adicional debe conservar `text/event-stream` sin buffering y
usar un timeout de lectura superior a 45 segundos.
