#!/usr/bin/env bash
#
# Jellytel dev-test harness. Boots a throwaway Jellyfin in Docker with the
# locally-built plugin staged in, lets you build/reload/inspect/teardown
# without touching your real Jellyfin install.
#
# Usage: scripts/dev-test.sh                  (interactive menu)
#        scripts/dev-test.sh build            (run a single action)
#        scripts/dev-test.sh start
#        scripts/dev-test.sh status
#        scripts/dev-test.sh reload
#        scripts/dev-test.sh logs
#        scripts/dev-test.sh stop
#        scripts/dev-test.sh teardown
#
# Notes on what / why:
#   • State lives under $HOME/jellytel-dev/ (Docker Desktop shares $HOME by
#     default; /tmp is NOT shared on Mac and silently mounts as empty).
#   • Plugin DLL list mirrors the CI workflow (.github/workflows/publish.yaml).
#     If you bundle a new dependency, update both files.
#   • Verify-on-start parses container logs for "Loaded plugin: Jellytel" and
#     surfaces per-subsystem (logs / metrics / local buffer) state so a load
#     regression is obvious without grepping by hand.
#   • Dev container default admin: root / admin (set during onboarding,
#     persisted under config/).
#
# See CLAUDE.md for the lessons-learned that drove this script's shape.
set -euo pipefail

# ─── Config ────────────────────────────────────────────────────────────
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/Jellyfin.Plugin.Jellytel/Jellyfin.Plugin.Jellytel.csproj"
PLUGIN_NAME="Jellyfin.Plugin.Jellytel"
PLUGIN_DIR_NAME="Jellytel"
PLUGIN_VERSION="${JELLYTEL_DEV_VERSION:-1.0.99.1}"
JELLYFIN_IMAGE="${JELLYTEL_JELLYFIN_IMAGE:-jellyfin/jellyfin:10.11.8}"
CONTAINER="${JELLYTEL_CONTAINER:-jellytel-test}"
PORT="${JELLYTEL_PORT:-8096}"

DEV_ROOT="${JELLYTEL_DEV_ROOT:-$HOME/jellytel-dev}"
CONFIG_DIR="$DEV_ROOT/config"
CACHE_DIR="$DEV_ROOT/cache"
MEDIA_DIR="$DEV_ROOT/media"
PUBLISH_DIR="$DEV_ROOT/publish"
PLUGIN_DIR="$CONFIG_DIR/plugins/${PLUGIN_DIR_NAME}_${PLUGIN_VERSION}"

# DLLs the CI workflow ships (mirror Package zip step in publish.yaml).
PLUGIN_DLLS=(
    "${PLUGIN_NAME}.dll"
    "Serilog.Sinks.OpenTelemetry.dll"
    "Google.Protobuf.dll"
)
# Grpc.*.dll is best-effort — copied with a glob.

TEST_VIDEO_URL="https://download.blender.org/peach/bigbuckbunny_movies/BigBuckBunny_320x180.mp4"
TEST_VIDEO_FILE="$MEDIA_DIR/BigBuckBunny.mp4"

# ─── Colors ────────────────────────────────────────────────────────────
if [[ -t 1 ]]; then
    C_HEAD=$'\033[1;36m'; C_OK=$'\033[1;32m'; C_WARN=$'\033[1;33m'
    C_ERR=$'\033[1;31m';  C_DIM=$'\033[2m';    C_RESET=$'\033[0m'
else
    C_HEAD=""; C_OK=""; C_WARN=""; C_ERR=""; C_DIM=""; C_RESET=""
fi

say()  { printf '%s%s%s\n' "$C_HEAD" "$1" "$C_RESET"; }
ok()   { printf '%s✓ %s%s\n' "$C_OK"  "$1" "$C_RESET"; }
warn() { printf '%s! %s%s\n' "$C_WARN" "$1" "$C_RESET"; }
err()  { printf '%s✗ %s%s\n' "$C_ERR" "$1" "$C_RESET" >&2; }
dim()  { printf '%s%s%s\n' "$C_DIM" "$1" "$C_RESET"; }

require() {
    command -v "$1" >/dev/null 2>&1 || { err "$1 not found in PATH"; exit 1; }
}

# ─── Actions ───────────────────────────────────────────────────────────

action_build() {
    say "Publishing plugin to $PUBLISH_DIR"
    require dotnet
    rm -rf "$PUBLISH_DIR"
    dotnet publish "$PROJECT" \
        -c Release \
        -p:Version="$PLUGIN_VERSION" \
        -p:AssemblyVersion="$PLUGIN_VERSION" \
        -p:FileVersion="$PLUGIN_VERSION" \
        -o "$PUBLISH_DIR" > /tmp/jellytel-publish.log 2>&1 \
        || { err "dotnet publish failed (see /tmp/jellytel-publish.log)"; tail -30 /tmp/jellytel-publish.log; return 1; }
    ok "Build succeeded"

    say "Staging plugin → $PLUGIN_DIR"
    # Wipe any older versioned dirs for the same plugin to avoid duplicates.
    find "$CONFIG_DIR/plugins" -maxdepth 1 -type d -name "${PLUGIN_DIR_NAME}_*" -exec rm -rf {} + 2>/dev/null || true
    mkdir -p "$PLUGIN_DIR"

    for dll in "${PLUGIN_DLLS[@]}"; do
        if [[ -f "$PUBLISH_DIR/$dll" ]]; then
            cp "$PUBLISH_DIR/$dll" "$PLUGIN_DIR/"
        else
            warn "Missing expected DLL: $dll"
        fi
    done
    # Grpc.*.dll is optional — only present when the protobuf chain pulls it.
    cp "$PUBLISH_DIR"/Grpc.*.dll "$PLUGIN_DIR/" 2>/dev/null || true

    ok "Staged: $(ls "$PLUGIN_DIR" | wc -l | tr -d ' ') files"
    ls "$PLUGIN_DIR" | sed 's/^/    /'
}

action_start() {
    require docker
    mkdir -p "$CONFIG_DIR" "$CACHE_DIR" "$MEDIA_DIR"

    if docker ps -a --format '{{.Names}}' | grep -qx "$CONTAINER"; then
        warn "Container $CONTAINER already exists. Use 'restart' or 'teardown' first."
        return 1
    fi

    say "Starting Jellyfin ($JELLYFIN_IMAGE) on :$PORT"
    docker run -d \
        --name "$CONTAINER" \
        -p "$PORT:8096" \
        -v "$CONFIG_DIR:/config" \
        -v "$CACHE_DIR:/cache" \
        -v "$MEDIA_DIR:/media" \
        "$JELLYFIN_IMAGE" > /dev/null
    ok "Container started"

    say "Waiting for /health (max 60s)…"
    local i=0
    while ! curl -sf "http://localhost:$PORT/health" > /dev/null 2>&1; do
        sleep 2
        i=$((i+1))
        if [[ $i -gt 30 ]]; then
            err "Server didn't become healthy. Check 'logs'."
            return 1
        fi
    done
    ok "Jellyfin is up: http://localhost:$PORT"

    # Give the plugin loader a moment to finish then report status.
    sleep 2
    verify_plugin_load
}

verify_plugin_load() {
    local logs
    logs=$(docker logs "$CONTAINER" 2>&1)

    if printf '%s' "$logs" | grep -q "Loaded plugin: Jellytel"; then
        ok "Plugin loaded (PluginManager confirmed)"
    elif printf '%s' "$logs" | grep -qiE "failed to load assembly.*$PLUGIN_NAME"; then
        err "Plugin load FAILED"
        printf '%s\n' "$logs" | grep -A 8 "Failed to load assembly.*$PLUGIN_NAME" | head -20 | sed 's/^/    /'
        return 1
    else
        warn "No load confirmation yet — try 'logs' to investigate."
    fi

    # Sub-bootstrappers
    if printf '%s' "$logs" | grep -q "Jellytel: OTLP endpoint not configured"; then
        dim "    Logs subsystem: idle (no OTLP endpoint configured)"
    elif printf '%s' "$logs" | grep -qE "Jellytel:.*OTLP.*enabled"; then
        ok  "    Logs subsystem: exporting"
    fi
    if printf '%s' "$logs" | grep -q "Jellytel metrics: disabled by configuration"; then
        dim "    Metrics subsystem: disabled in config"
    elif printf '%s' "$logs" | grep -q "Jellytel metrics: enabled"; then
        ok  "    Metrics subsystem: running"
    fi
    if printf '%s' "$logs" | grep -q "Jellytel buffer: disabled by configuration"; then
        dim "    Local buffer:      disabled in config"
    elif printf '%s' "$logs" | grep -q "Jellytel buffer: enabled"; then
        ok  "    Local buffer:      running"
    fi
}

action_stop() {
    require docker
    if docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
        docker stop "$CONTAINER" > /dev/null
        ok "Container stopped (state preserved)"
    else
        dim "Container not running"
    fi
}

action_remove_container() {
    require docker
    if docker ps -a --format '{{.Names}}' | grep -qx "$CONTAINER"; then
        docker rm -f "$CONTAINER" > /dev/null
        ok "Container removed (Jellyfin config preserved in $CONFIG_DIR)"
    else
        dim "Container does not exist"
    fi
}

action_restart() {
    action_remove_container
    action_start
}

action_reload() {
    # Rebuild + restage + restart container (Jellyfin re-discovers plugins on boot)
    action_build || return 1
    if docker ps -a --format '{{.Names}}' | grep -qx "$CONTAINER"; then
        action_remove_container
    fi
    action_start
}

action_status() {
    require docker
    say "─── Container ───"
    if docker ps --format '{{.Names}}\t{{.Status}}' | grep -E "^${CONTAINER}\b" || true; then
        :
    fi
    if ! docker ps -a --format '{{.Names}}' | grep -qx "$CONTAINER"; then
        dim "    (no container)"
    fi

    say "─── HTTP ───"
    if curl -sf "http://localhost:$PORT/health" > /dev/null 2>&1; then
        ok "http://localhost:$PORT — healthy"
    else
        dim "    Not reachable on :$PORT"
    fi

    say "─── Plugin ───"
    if [[ -d "$PLUGIN_DIR" ]]; then
        ok "Staged at $PLUGIN_DIR"
        ls "$PLUGIN_DIR" | sed 's/^/    /'
    else
        dim "    Not staged. Run 'build'."
    fi

    if docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
        say "─── Plugin runtime ───"
        verify_plugin_load
    fi

    say "─── Media ───"
    if [[ -f "$TEST_VIDEO_FILE" ]]; then
        ok "Test video present: $(basename "$TEST_VIDEO_FILE") ($(du -h "$TEST_VIDEO_FILE" | cut -f1))"
    else
        dim "    No test video. Run 'video'."
    fi
}

action_logs() {
    require docker
    if ! docker ps -a --format '{{.Names}}' | grep -qx "$CONTAINER"; then
        err "No container — start it first."
        return 1
    fi
    echo
    dim "Press Ctrl-C to stop streaming."
    echo
    docker logs -f "$CONTAINER" 2>&1 | grep --color=always -iE 'jellytel|^$' || true
}

action_logs_all() {
    require docker
    docker logs -f "$CONTAINER" 2>&1
}

action_open() {
    if command -v open > /dev/null 2>&1; then
        open "http://localhost:$PORT"
    else
        echo "http://localhost:$PORT"
    fi
}

action_video() {
    mkdir -p "$MEDIA_DIR"
    if [[ -f "$TEST_VIDEO_FILE" ]]; then
        ok "Test video already present: $TEST_VIDEO_FILE"
        return 0
    fi
    say "Downloading Big Buck Bunny (320x180, public-domain Blender Foundation clip)"
    require curl
    if curl -fL --progress-bar "$TEST_VIDEO_URL" -o "$TEST_VIDEO_FILE"; then
        ok "Saved to $TEST_VIDEO_FILE"
        dim "    Add /media as a library in Jellyfin to see it."
    else
        rm -f "$TEST_VIDEO_FILE"
        err "Download failed"
        return 1
    fi
}

action_reset_jellyfin() {
    # Wipe Jellyfin's config (forces re-onboarding) but keep media and the plugin staging.
    require docker
    if docker ps -a --format '{{.Names}}' | grep -qx "$CONTAINER"; then
        action_remove_container
    fi
    say "Wiping Jellyfin config at $CONFIG_DIR"
    rm -rf "$CONFIG_DIR" "$CACHE_DIR"
    ok "Config reset. Plugin will need to be rebuilt+restaged because plugin dir lives under config/."
}

action_teardown() {
    require docker
    if docker ps -a --format '{{.Names}}' | grep -qx "$CONTAINER"; then
        action_remove_container
    fi
    say "Removing $DEV_ROOT"
    rm -rf "$DEV_ROOT"
    ok "Full teardown done"
}

# ─── Menu ──────────────────────────────────────────────────────────────

menu() {
    while true; do
        echo
        say "──── Jellytel dev-test ────"
        echo "  Container: $CONTAINER  •  Image: $JELLYFIN_IMAGE  •  Port: $PORT"
        echo "  Plugin:    $PLUGIN_NAME v$PLUGIN_VERSION"
        echo "  Dev root:  $DEV_ROOT"
        echo
        echo "  1) Build + stage plugin"
        echo "  2) Start Jellyfin (verify plugin load)"
        echo "  3) Stop Jellyfin (keep config)"
        echo "  4) Restart container"
        echo "  5) Reload: rebuild + restage + restart"
        echo "  6) Status"
        echo "  7) Tail logs (filtered to Jellytel)"
        echo "  8) Tail ALL logs"
        echo "  9) Open in browser"
        echo " 10) Drop test video into media/"
        echo " 11) Reset Jellyfin config (keep dev root)"
        echo " 12) Teardown everything"
        echo "  q) Quit"
        echo
        read -rp "  > " choice
        case "$choice" in
            1)  action_build ;;
            2)  action_start ;;
            3)  action_stop ;;
            4)  action_restart ;;
            5)  action_reload ;;
            6)  action_status ;;
            7)  action_logs ;;
            8)  action_logs_all ;;
            9)  action_open ;;
            10) action_video ;;
            11) action_reset_jellyfin ;;
            12) action_teardown ;;
            q|Q) exit 0 ;;
            *)  warn "Unknown choice: $choice" ;;
        esac
    done
}

# ─── Dispatch ──────────────────────────────────────────────────────────

if [[ $# -eq 0 ]]; then
    menu
else
    case "$1" in
        build)         action_build ;;
        start)         action_start ;;
        stop)          action_stop ;;
        restart)       action_restart ;;
        reload)        action_reload ;;
        status)        action_status ;;
        logs)          action_logs ;;
        logs-all)      action_logs_all ;;
        open)          action_open ;;
        video)         action_video ;;
        reset)         action_reset_jellyfin ;;
        teardown)      action_teardown ;;
        *)             err "Unknown action: $1"; exit 1 ;;
    esac
fi
