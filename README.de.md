# Arma 3 Server Manager

**Sprachen:** [Español](README.md) · [English](README.en.md) · [Deutsch](README.de.md)

Web-Panel zum Installieren, Aktualisieren und Betreiben eines dedizierten
Arma-3-Servers, seiner Mods, Creator-DLCs, Dateien, Konfiguration und
SteamCMD-Sitzungen.

## Architektur

| Schicht | Technologie |
|---|---|
| Frontend | Astro 7 + Vue 3, statische Ausgabe |
| Web-Proxy | Nginx Alpine |
| Backend | ASP.NET Core / .NET 10 |
| Zustand | SQLite |
| Konfiguration | TOML |
| Container | Podman |
| Deployment | Python, SSH und Remote-Podman |

```text
backend/Arma3Manager.Api/
├── Application/       Anwendungsprozesse und -werkzeuge
├── Configuration/     Lesen und Validieren von TOML
├── Contracts/         HTTP- und Persistenzverträge
├── Domain/            Servermodelle und Routen
├── Endpoints/         REST-API und Authentifizierung
├── Infrastructure/    SQLite, Metriken und Dateisystem
├── Security/          Zugangsdaten
└── Program.cs         Composition Root

web/
├── src/                Astro, Vue und Panel-Logik
├── public/             statische Dateien
└── dist/               reproduzierbare Ausgabe, nicht versioniert
```

## Konfiguration

Die Anwendung verwendet keine `.env`-Dateien mehr.

1. `config/manager.toml` für Ports, Netzwerk, Pfade und Limits bearbeiten.
2. Die private Datei erstellen:

```bash
cp config/manager.secrets.example.toml config/manager.secrets.toml
chmod 600 config/manager.secrets.toml
```

3. Das Panel-Passwort und das Session-Secret ersetzen.

Ein Session-Secret lässt sich erzeugen mit:

```bash
python3 -c "import secrets; print(secrets.token_hex(32))"
```

`manager.secrets.toml` ist von Git ausgeschlossen und wird nie in ein Image
kopiert. Beim Deployment wird sie per SCP übertragen und schreibgeschützt
eingehängt.

SQLite wird unter `/arma3/manager.sqlite3` gespeichert und enthält
editierbare Einstellungen, Mods, Modlisten und Authentifizierungs-Metadaten.
Große Dateien bleiben weiterhin im Volume `/arma3`.

### Hauptabschnitte

- `[web]`: interner API-Port, öffentlicher Port, Bind, URL und
  Initialkonto.
- `[server]`: Verzeichnisse, UDP-Ports, Netzwerk und Speicherlimit.
- `[steam]`: Benutzer, autorisierte Steam-IDs und Creator-DLC-App-IDs.
- `[runtime]`: Zeitzone und Simulatoren für Tests.

Für den Host-Netzwerkmodus `config/manager.host.toml` zusammen mit dem
Overlay `podman-compose.host.yml` verwenden.

## Entwicklung

Voraussetzungen: .NET 10, Node.js 24, Python 3.11+, Doxygen und Podman.

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

Die Ausgabe von Astro ist vollständig statisch. Node.js wird nur beim Build
benötigt; in Produktion liefert Nginx `web/dist` aus.

## Lokales Podman

```bash
podman compose -f podman-compose.yml up -d --build
podman compose -f podman-compose.yml ps
podman compose -f podman-compose.yml logs --tail 200
```

Host-Modus:

```bash
podman compose -f podman-compose.yml -f podman-compose.host.yml up -d --build
```

Persistente Volumes:

- `arma3-server`
- `steam-home`
- `steam-config`
- `aspnet-keys`

Das Entfernen oder Neuerstellen von Containern löscht diese Volumes nicht.

### Vollständiger Reset über die API

Der authentifizierte Endpunkt `POST /api/system/factory-reset` löscht
sämtliche von der Anwendung verwalteten persistenten Inhalte: Server, Mods,
SQLite, SteamCMD-Zugangsdaten, Steam-Konfiguration und Session-Schlüssel.
Dafür müssen der Spielserver und alle Wartungsaufgaben gestoppt sein.

```json
{
  "currentPassword": "aktuelles-panel-passwort",
  "confirmation": "RESET ALL ARMA3 DATA"
}
```

Die Anfrage legt einen atomaren Marker an und startet Kestrel neu. Bevor
SQLite beim nächsten Start geöffnet wird, leert das Backend die vier
Volumes und entfernt den Marker erst, wenn alle Vorgänge erfolgreich
abgeschlossen sind. Wird der Prozess unterbrochen, versucht es der nächste
Start erneut.

Das Container-Image ist unveränderlich und speichert weder Mods noch
Panel-Zustand; deshalb erhält die API keinen Zugriff auf den
Root-Socket von Podman. Um eine neue Image-Version zu verwenden, nach dem
Reset das normale Deployment ausführen.

## Remote-Deployment

Lokale Zielkonfiguration erstellen:

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

Selektive Deployments:

```bash
python3 deploy.py dev --check
python3 deploy.py dev --check --force   # nur Dev: überspringt nur die 0600-Prüfung
python3 deploy.py dev --frontend
python3 deploy.py dev --backend
python3 deploy.py prod --frontend
python3 deploy.py prod --backend --yes
python3 deploy.py prod --frontend --backend --yes
```

Remote-Betrieb:

```bash
python3 deploy.py prod --status
python3 deploy.py prod --logs backend
python3 deploy.py prod --logs frontend
```

Das Skript:

1. validiert TOML, SSH, `rsync` und Podman;
2. überträgt mit `rsync` ein Release ohne Secrets oder Build-Artefakte und
   zeigt Fortschritt sowie jede geänderte Datei;
3. baut nur das angeforderte Image auf dem Linux-Server;
4. erhält die Volumes;
5. ersetzt nur den ausgewählten Container;
6. wartet auf den Health-Check;
7. stellt bei fehlgeschlagenem Update das vorherige Image wieder her;
8. startet das Frontend nach einem reinen Backend-Deploy neu, damit Nginx
   die Adresse des neuen API-Containers auflöst;
9. entfernt alte, containerlose Images nur, wenn sie das Label
   `project=arma3-manager` tragen; führt keinen globalen Prune aus und
   rührt keine fremden Images an.

Jede SSH-Operation wird vor der Ausführung angezeigt. Diagnosen des Drivers
und von Podman werden in Echtzeit ausgegeben, auch wenn stdout zum Parsen der
JSON-Antworten des Vertrags reserviert wird.

Um das Entwicklungspanel von einem anderen Rechner aus zu erreichen,
`web.bind_ip` auf `"0.0.0.0"` setzen. `"127.0.0.1"` beibehalten, wenn das
Panel hinter einem lokalen Reverse-Proxy stehen soll.

Ein Backend-Update stoppt den Arma-3-Prozess, da dieser derzeit im
API-Container läuft. Deshalb ist eine Bestätigung oder `--yes` erforderlich.

## Server-Manager-v1-Vertrag

Dieses Repository implementiert den gemeinsamen Vertrag in
[`server-manager.contract.json`](server-manager.contract.json) und das lokale
JSON-Protokoll in [`manager_driver.py`](manager_driver.py). Das erste
Deployment des Patches behält das Podman-Secret, die Konfiguration, alle vier
Volumes, das Netzwerk, die Ports und die vorhandenen Daten bei. Es speichert
eine stabile UUID und erstellt nur die ausgewählten Container mit den
v1-OCI-Labels neu. Das Runtime-Manifest enthält reale Image-Referenzen, aber
niemals Secret-Werte.

Der Hub kann diese Instanz unter demselben Host und Unix-Benutzer erkennen und
vor Ort übernehmen. Die Übernahme erstellt eine private `controller.json`
(`0600`), und ein Lock pro Instanz serialisiert alle Operationen. Solange ein
Hub-Claim besteht, weist `deploy.py` manuelle Änderungen zurück. Das Trennen
gibt die Kontrolle frei, ohne Ressourcen zu stoppen oder zu löschen; danach
ist das manuelle Deployment wieder möglich.

Diese erste Version veröffentlicht Erkennung, Übernahme, Lebenszyklus,
Gesundheitsprüfung und Trennung als Fähigkeiten. Noch nicht angekündigte
Standardbefehle — Provisionierung, Recreate, Update, Secret-Rotation und
Zerstörung — liefern eine typisierte Nicht-Unterstützt-Antwort und dürfen vom
Hub nicht angeboten werden. Die kanonische Spezifikation, Secret-Richtlinie
und das Übernahmeverfahren liegen unter `game_servers_manager_hub/docs/`.

## Backend-Dokumentation

```bash
doxygen Doxyfile
```

Die HTML-Dokumentation wird unter
`docs/generated/backend/html/index.html` erzeugt. Die `Doxyfile` schlägt bei
Dokumentationsfehlern fehl und schließt `bin/` und `obj/` aus.

## API und Sicherheit

- `/api/health` ist öffentlich für Health-Checks.
- `/api/auth/*` und `/auth/steam/*` verwalten Sitzungen.
- Der Rest von `/api/*` erfordert Authentifizierung.
- Der Datei-Editor beschränkt Pfade auf `/arma3`.
- SQLite und seine WAL/SHM-Dateien sind vom Panel aus geschützt.
- Steam-Zugangsdaten werden in Befehlsprotokollen geschwärzt.

## Prüfung vor Produktion

```bash
dotnet test Arma3Manager.slnx
cd web && npm run build
cd .. && doxygen Doxyfile
python3 -m py_compile deploy.py
podman build -f Containerfile.api -t arma3-manager-api:test .
podman build -f Containerfile.frontend -t arma3-manager-frontend:test .
```
