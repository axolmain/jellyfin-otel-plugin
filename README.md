# Jellytel — OpenTelemetry plugin for Jellyfin

Bootstraps OpenTelemetry inside the Jellyfin process and exports metrics, traces, and logs to any OTLP-compatible backend (Grafana, Datadog, Honeycomb, an OTel Collector, etc.). Runs in-process — no sidecar, no scraping.

> Status: **early**. The current release exports **logs** only via Serilog. Metrics, traces, and the playback/library signals described in the roadmap are not implemented yet.

---

## Install

### Via custom repository (recommended)

1. Jellyfin Dashboard → **Plugins** → **Repositories** → **+**
2. Repository URL:
   ```
   https://raw.githubusercontent.com/axolmain/jellyfin-otel-plugin/master/manifest.json
   ```
3. **Catalog** → **Jellytel** → **Install** → restart Jellyfin
4. **Plugins** → **Jellytel** → set OTLP endpoint (e.g. `http://localhost:4318`) and save

### Manual

1. Download `jellytel_<version>.zip` from [Releases](https://github.com/axolmain/jellyfin-otel-plugin/releases)
2. Unzip into `<jellyfin-data>/plugins/Jellytel/`
3. Restart Jellyfin

Compatible with **Jellyfin 10.11.x** (`targetAbi: 10.11.0.0`, `net9.0`).

---

## Configuration

| Setting | Description |
|---|---|
| **OTLP Endpoint** | Base URL of an OTLP/HTTP collector (e.g. `http://localhost:4318`). Empty = export disabled. |
| **Service Name** | Value reported as the `service.name` resource attribute. Defaults to `jellyfin`. |
| **Backfill boot logs** | On startup, replays log events that occurred before the plugin loaded by re-parsing the current log file. Lossy on structured properties. |

Endpoint changes apply on save — no restart required.

---

## Quick OTel backend: Aspire dashboard

Don't have an OTel collector set up? The .NET Aspire **standalone dashboard** is the easiest way to see what Jellytel is emitting — both during plugin development and against a real Jellyfin install. It's a single container with a UI for metrics, logs, and traces, and it speaks OTLP out of the box.

```bash
docker run -d --rm --name aspire-dashboard \
  -p 18888:18888 \
  -p 4317:18889 \
  -p 4318:18890 \
  mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

Then in **Plugins → Jellytel**:

| Setting | Value |
|---|---|
| **OTLP Endpoint** | `http://localhost:4318` (same host as Jellyfin) — or `http://host.docker.internal:4318` if Jellyfin runs in Docker |

Grab the one-time login URL with `docker logs aspire-dashboard | grep login`, open it, and `jellyfin.*` + `jellytel.buffer.*` metrics should start showing up within a few seconds.

The dev-test script (see below) wires this up automatically — start/reload spins the dashboard, patches the plugin's OTLP endpoint, and prints the login URL.

---

## Roadmap

### Phase 1 — Bitrate & encoding visibility *(planned)*
Active streams by play method, per-session bitrate, aggregate outbound rate, transcode count by reason, ffmpeg encode-speed ratio. Stream start/stop and transcode start as log events.

### Phase 2 — Playback experience *(planned)*
Buffer-ahead distance, rebuffer/stall events, session vs. content duration, pause/resume.

### Phase 3 — Attribution *(planned)*
Per-user bandwidth, per-client-type breakdown, transcode-reason breakdown, playback request traces.

### Phase 4 — Library & storage *(planned)*
Item counts by type, storage per library, scheduled-task outcomes, scan/add/remove events.

### Non-goals
Replacing Jellyfin's `/metrics` endpoint, shipping dashboards, or running outside the Jellyfin process.

---

## Building from source

```
git clone https://github.com/axolmain/jellyfin-otel-plugin
cd jellyfin-otel-plugin
dotnet build -c Release
```

For a local install + Jellyfin debug session, see [`.vscode/tasks.json`](.vscode/tasks.json) and [`RELEASING.md`](RELEASING.md).

## Local development

Iterate against a throwaway Jellyfin in Docker:

```bash
./scripts/dev-test.sh            # interactive menu
./scripts/dev-test.sh reload     # rebuild + restage + restart
./scripts/dev-test.sh status     # what's running, what's loaded
./scripts/dev-test.sh logs       # tail filtered to Jellytel
```

Defaults: `jellyfin/jellyfin:10.11.8` on `http://localhost:8096`, state under
`~/jellytel-dev/`. The dev container is a clean Jellyfin every time, so the
plugin's behaviour during install/load is exactly what an end user will see.

Architecture and design rationale live under [`docs/`](docs/). Internal
gotchas and the dev workflow are documented in [`CLAUDE.md`](CLAUDE.md).

## License

GPLv3 — see [`LICENSE`](LICENSE). Plugins linking against Jellyfin are GPLv3 by transitivity.
