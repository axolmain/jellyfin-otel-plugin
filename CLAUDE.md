# Jellytel — agent guide

Working notes for future Claude sessions on this repo. Keep this short and
factual; this file is auto-loaded into the agent's context.

---

## Project shape

- C# Jellyfin plugin. One csproj: `Jellyfin.Plugin.Jellytel/`.
- Target: **net9.0**, targetAbi **10.11.0.0** (Jellyfin server 10.11.x).
- Three feature areas:
  - `Logs/` — Serilog → OTLP log forwarding (existing, working).
  - `Metrics/` — `System.Diagnostics.Metrics` instruments + custom OTLP/HTTP
    exporter under `Metrics/Export/`. Panels (`Metrics/Panels/*Metrics.cs`)
    create instruments on `JellytelMeter.Instance`; the bootstrapper wires a
    `MeterCollector` to an `IMetricExporter`.
  - `LocalBuffer/` — SQLite ring buffer of metric samples + REST API under
    `Api/` + embedded sparkline dashboard. Independent of OTLP.
- Embedded HTML pages: `Configuration/configPage.html` (settings form) +
  `Configuration/dashboard.html` (sparklines). Both registered in
  `Plugin.GetPages()`.
- Docs in `docs/`. Read them before re-deriving anything — `jellyfin-events.md`,
  `jellyfin-dashboard-surface.md`, `local-buffer.md`, `exporter-architecture.md`.

---

## Dev workflow

Use the script — **don't reinvent it**:

```bash
./scripts/dev-test.sh                # interactive menu
./scripts/dev-test.sh reload         # build + restage + restart container
./scripts/dev-test.sh status         # what's running, what's loaded
./scripts/dev-test.sh logs           # tail filtered to Jellytel
```

The script runs a throwaway Jellyfin in Docker against `~/jellytel-dev/`,
mirrors the same DLL set the CI workflow ships, and verifies plugin load
after each restart.

Dev container creds (set once during onboarding, persisted across reloads):
- user: `root`
- pass: `admin`

---

## Hard-won lessons — read before changing things

### 1. The OpenTelemetry SDK does not load on net9 Jellyfin

`OpenTelemetry 1.13+` depends on `System.Diagnostics.DiagnosticSource 10.x`.
The host (net9) loads its own 9.x copy of that assembly. Result: `MeterListener`
has a different vtable than the SDK was compiled against, and you get a
`TypeLoadException` at plugin load on `OpenTelemetryMetricsListener.InstrumentPublished`.

Older OTel SDK versions have unresolved CVEs the consumer can't accept.

**Resolution:** we removed the OTel SDK entirely. Metrics are collected via a
plain `MeterListener` (`Metrics/Export/MeterCollector.cs`) and exported by
hand-rolled OTLP/HTTP protobuf (`Metrics/Export/OtlpHttpExporter.cs`) over
vendored `.proto` files in `Proto/`. See `docs/exporter-architecture.md`.

**Do not add the OTel SDK back** unless Jellyfin moves to net10. If you do,
delete the abstraction layer at the same time — having both is just noise.

### 2. Plugin DLL versions must match what the host ships

The plugin compiles against transitive deps, but Jellyfin's `AssemblyLoadContext`
unifies common libraries against what the host already has loaded. If the
plugin DLL has a binding reference to `Serilog 4.3.0` and Jellyfin ships
`Serilog 4.2.0`, the load fails with `FileNotFoundException`.

**Always pin shared deps to the host version.** Check
`Jellyfin/Directory.Packages.props` in the Jellyfin source tree for the
authoritative versions. Today:

- `Serilog 4.2.0` (host on 10.11.x) — pin explicitly with `<ExcludeAssets>runtime</ExcludeAssets>`
- `Microsoft.Data.Sqlite 9.0.0` (host on 10.11.x bundles 9.0.x) — same treatment. **Do not bump to 10.x** until Jellyfin moves to net10 — that version is on Jellyfin's master branch only; the shipped 10.11.x line is still on 9.
- `Serilog.Sinks.OpenTelemetry 4.1.1` — last version that requires only `Serilog >= 4.0.0`. Newer sink versions pull `Serilog 4.3+`.

**Trap:** checking `Directory.Packages.props` in the Jellyfin **source tree** gives you the *next* version, not the one currently shipping. Verify against the actual `jellyfin/jellyfin:<tag>` Docker image with:

```bash
docker cp jellytel-test:/jellyfin/<Pkg>.dll /tmp/x.dll
python3 -c "import re; d=open('/tmp/x.dll','rb').read(); print(sorted(set(re.findall(rb'\d+\.\d+\.\d+\.\d+', d))))"
```

When in doubt: dev-test → reload → tail logs for `FileNotFoundException`.

**`<ExcludeAssets>runtime</ExcludeAssets>`** = "compile against this API but
trust the host to provide the binary at runtime." Use for anything the host
already loads. Skip it for things the host doesn't ship (e.g. our custom
protobuf-generated code, `Google.Protobuf`, `Grpc.*`).

### 3. Jellyfin web's "Settings" button picks the wrong page if you're not careful

`jellyfin-web` picks the Settings target with:

```js
pages.find(p => p.EnableInMainMenu) ?? pages[0]
```

If multiple pages are registered with `EnableInMainMenu = true`, the **first**
one in the `GetPages()` list wins. The config form must therefore be:
- registered first in `GetPages()`, AND
- have `EnableInMainMenu = true`

(Both pages here have `EnableInMainMenu = true` so they each get a sidebar
entry. Order in `GetPages()` decides which is "Settings".)

### 4. Embedded plugin pages: `<head>` gets stripped

Jellyfin's web client extracts only the `<div data-role="page">` from a
plugin's HTML resource and injects it into the dashboard DOM. **Anything in
`<head>`** — `<style>`, `<link rel="stylesheet">`, external CDN imports —
**is silently discarded**.

Put `<style>` blocks **inside** the page div, scoped by an ID selector
(e.g. `#JellytelDashboardPage .jt-foo`). Don't rely on external CDN CSS;
write your own minimal styles.

External `<script src=...>` *does* work because the web client passes the
HTML through innerHTML, which executes inline scripts but does not execute
external script tags either. Anything you need to run, write inline.

### 5. Don't return `503` for "feature is disabled in config"

The API controllers return 200 with `BufferEnabled: false` (or empty
`Points`) when the local buffer is off, not 503. 503 is "service failure"
and causes the dashboard JS to throw uncaught promise rejections. "User
hasn't enabled this yet" is a normal 200 state.

JS fetches must also have `.catch` handlers even when the server is
well-behaved — defense in depth.

### 6. Docker on Mac: `/tmp` is not shared by default

`docker run -v /tmp/...:/foo` *appears* to mount but the container sees an
empty directory. Stage dev data under `$HOME` (Docker Desktop shares
`/Users/<you>` by default). The dev-test script already does this.

### 7. The dashboard's "An error occurred while getting the plugin details from the repository" banner is cosmetic

It means the plugin catalog tried to phone home to a repository URL and
either there isn't one configured or it doesn't list this exact GUID. The
installed plugin works fine. Ignore unless we add a repository for the dev
container.

### 8. Reconfigure-on-save vs. restart

All three bootstrappers (logs, metrics, local buffer) subscribe to
`Plugin.Instance.ConfigurationChanged` and rebuild their pipelines when
the user saves the config page. **No Jellyfin restart needed** to apply
config changes. If you add a new bootstrapper, follow the same pattern in
`MetricsBootstrapper`/`LocalBufferBootstrapper`.

---

## Things that are deliberately NOT done

- **OTel SDK.** See #1 above. Don't add back without removing the abstraction.
- **gRPC OTLP transport.** HTTP+protobuf only. No need for the second wire format.
- **Per-export retry queue.** If OTLP is down, samples drop. The local
  SQLite buffer is the durable copy if you need one.
- **Trace export.** Metrics + logs only for v1.
- **Tiered metric rollups.** Single-resolution ring buffer, retention is a
  config knob.

---

## Style/lint

- `TreatWarningsAsErrors=true`, `AnalysisMode=AllEnabledByDefault`,
  StyleCop + extra analyzers.
- One public type per file (SA1402).
- File name matches first public type (SA1649).
- Use `SuppressMessage` with a `Justification` when an analyzer is wrong
  for the situation — the abstraction layer specifically suppresses CA1859
  because the interface IS the design.
- XML doc comments required on public APIs (`GenerateDocumentationFile=true`).

---

## Docs maintenance

When you change behavior, update the docs in the same pass. The relevant
files (this list is not exhaustive — grep for stale claims):

- `README.md` — user-facing settings table, status banner, install/Aspire flow.
- `docs/exporter-architecture.md` — pipeline shapes, vendored protos list.
- `docs/local-buffer.md` — API surface, response shapes, retention.
- `docs/DEVELOPING.md` — "(current)" sections describing how each subsystem
  works today.

**Rewrite, don't accrete.** Replace stale prose with the current truth in
place. Do not write "we used to do X, now we do Y", "previously this was Z",
or "as of the trace pipeline addition…" in the docs. The doc should read as
if the current state has always been the case. Changelog entries belong in
git commit messages, GitHub release notes, and `build.yaml` — not in
reference docs that someone reads to learn how the system works *now*.

A good test: if a reader who never saw the prior version of the doc would
find a sentence confusing or unnecessary, delete it.

---

## Repository layout cheatsheet

```
Jellyfin.Plugin.Jellytel/
  Api/                       REST controllers for the dashboard page
  Configuration/             configPage.html, dashboard.html, PluginConfiguration.cs
  LocalBuffer/               SQLite store, recorders, snapshotter
  Logs/                      Serilog → OTLP bootstrap + boot-log replayer
  Metrics/
    Export/                  IMeterCollector + IMetricExporter abstraction
                             (MeterCollector, OtlpHttpExporter, NullExporter)
    Panels/                  IMetricPanel implementations (SessionMetrics, …)
    JellytelMeter.cs         The shared Meter all panels create instruments on
    MetricsBootstrapper.cs   Wires collector + exporter + panels
  Traces/
    Export/                  IActivityCollector + ITraceExporter abstraction
                             (ActivityCollector, OtlpHttpTraceExporter, NullTraceExporter)
    JellytelActivitySource.cs  Shared ActivitySource for plugin-side spans
    TracesBootstrapper.cs    Wires listener + exporter; CSV allowlist of sources
  Proto/                     Vendored OTLP .proto files (codegen at build time)
  Plugin.cs                  BasePlugin<PluginConfiguration>, IHasWebPages
  PluginServiceRegistrator.cs  DI registrations
docs/                        Reference + architecture docs
scripts/dev-test.sh          Dev harness — interactive menu + CLI
.github/workflows/publish.yaml  Tagged releases: build, zip, manifest, gh release
```
