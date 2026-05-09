# Releasing & Local Install

## Cutting a release

1. Bump `version` in `build.yaml` and the `<Version>` fields in `Directory.Build.props`.
2. Commit & push to `master`.
3. Tag and push:
   ```
   git tag v1.0.0.0
   git push origin v1.0.0.0
   ```
4. The `🚀 Publish Plugin` workflow builds, zips, creates a GitHub Release with the zip attached, and commits an updated `manifest.json` back to `master`.

You can also trigger it manually via the Actions tab (`workflow_dispatch`) and supply the version.

## Installing in Jellyfin

### Option A — Custom repository (recommended)

1. Jellyfin Dashboard → **Plugins** → **Repositories** → **+**.
2. Repository URL:
   ```
   https://raw.githubusercontent.com/axolmain/jellyfin-otel-plugin/master/manifest.json
   ```
3. Go to **Catalog** → **Jellytel** → **Install**.
4. Restart Jellyfin.
5. Configure under **Plugins** → **Jellytel** (set OTLP endpoint, e.g. `http://localhost:4318`).

### Option B — Manual drop

1. Download `jellytel_<version>.zip` from the GitHub release.
2. Unzip into `<jellyfin-data>/plugins/Jellytel/` (create the folder if needed).
3. Restart Jellyfin.

Linux default data dir: `~/.local/share/jellyfin`
macOS default: `~/.local/share/jellyfin` (varies by install)
Windows default: `%LOCALAPPDATA%\jellyfin`
