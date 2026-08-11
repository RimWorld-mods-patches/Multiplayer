#!/usr/bin/env bash
#
# Launch RimWorld host + client on macOS with separated logs.
#
# macOS counterpart of RimWorldWin64-HostClient.ps1. Creates one timestamped run
# folder holding Player-Host / Player-Client / arbiter_log.txt and starts two
# instances from the app bundle's binary directly, which is what lets a second
# copy run at all (`open` hands the launch to LaunchServices, which just
# forwards to the instance already running).
#
# The Windows script renames each window to "... HOST" / "... CLIENT". macOS has
# no equivalent: a window's accessibility title is read-only, and even reading it
# needs Automation consent. Instead the two windows are tiled — client on the left
# half of the screen, host on the right — which is the visual tell.
#
# That works by writing Unity's window keys into ~/Library/Preferences/
# ludeon.rimworld.plist just before each launch; Unity reads them at startup, so
# staggering the two launches places them independently. Note this plist is keyed
# by bundle id and therefore SHARED by both instances (RimWorld's own Prefs.xml
# does not hold window position), so whichever instance quits last writes its
# geometry back over it. Pass --no-tile to leave your window prefs alone.
#
# Usage:
#   ./RimWorldMac-HostClient.sh
#   ./RimWorldMac-HostClient.sh --host-delay 3
#   ./RimWorldMac-HostClient.sh --isolate-savedata
#   ./RimWorldMac-HostClient.sh --no-tile
#   ./RimWorldMac-HostClient.sh --dry-run

set -euo pipefail

GAME_ROOT="$HOME/Library/Application Support/Steam/steamapps/common/RimWorld"
CONFIG_SOURCE="$HOME/Library/Application Support/RimWorld"
RUNS_ROOT=""
RUN_ID=""
HOST_DELAY_SEC=2
ISOLATE_SAVEDATA=0
SEED_CONFIG=1
STARTUP_TIMEOUT_SEC=120
DRY_RUN=0
TILE=1
UNITY_PREFS_DOMAIN="ludeon.rimworld"

usage() {
    # The header block above, minus the shebang and the leading "# ".
    awk 'NR==1 && /^#!/ {next} /^#/ {sub(/^# ?/,""); print; next} {exit}' "$0"
    cat <<'EOF'

Options:
  --game-root PATH        RimWorld install dir (contains RimWorldMac.app)
  --config-source PATH    Save-data dir to seed mod config from
  --runs-root PATH        Where run folders are created  [<game-root>/MpTestRuns]
  --run-id ID             Name of this run folder        [timestamp]
  --host-delay SECONDS    Pause between client and host  [2]
  --isolate-savedata      Give each role its own save-data folder
  --no-seed-config        With --isolate-savedata, do NOT copy the real mod list
  --no-tile               Don't place the windows; leave window prefs untouched
  --startup-timeout SECS  How long to wait for each log to appear  [120]
  --dry-run               Print what would happen, launch nothing
  -h, --help              This text
EOF
}

die() { printf 'error: %s\n' "$*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --game-root)       GAME_ROOT="${2:?--game-root needs a path}"; shift 2 ;;
        --config-source)   CONFIG_SOURCE="${2:?--config-source needs a path}"; shift 2 ;;
        --runs-root)       RUNS_ROOT="${2:?--runs-root needs a path}"; shift 2 ;;
        --run-id)          RUN_ID="${2:?--run-id needs a value}"; shift 2 ;;
        --host-delay)      HOST_DELAY_SEC="${2:?--host-delay needs a number}"; shift 2 ;;
        --startup-timeout) STARTUP_TIMEOUT_SEC="${2:?--startup-timeout needs a number}"; shift 2 ;;
        --isolate-savedata) ISOLATE_SAVEDATA=1; shift ;;
        --no-seed-config)  SEED_CONFIG=0; shift ;;
        --no-tile)         TILE=0; shift ;;
        --dry-run)         DRY_RUN=1; shift ;;
        -h|--help)         usage; exit 0 ;;
        *)                 die "unknown option: $1 (try --help)" ;;
    esac
done

app="$GAME_ROOT/RimWorldMac.app"
[[ -d "$app" ]] || die "RimWorld app bundle not found: $app"

# The executable is named from Info.plist, not from the bundle — on 1.6 it is
# "RimWorld by Ludeon Studios", spaces and all.
exe_name="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Contents/Info.plist" 2>/dev/null || true)"
[[ -n "$exe_name" ]] || die "could not read CFBundleExecutable from $app/Contents/Info.plist"
exe="$app/Contents/MacOS/$exe_name"
[[ -x "$exe" ]] || die "RimWorld executable not found or not executable: $exe"

[[ -n "$RUNS_ROOT" ]] || RUNS_ROOT="$GAME_ROOT/MpTestRuns"
[[ -n "$RUN_ID" ]] || RUN_ID="$(date +%Y%m%d-%H%M%S)"

run_dir="$RUNS_ROOT/$RUN_ID"
host_dir="$run_dir/host"
client_dir="$run_dir/client"
host_log="$host_dir/Player-Host.log"
client_log="$client_dir/Player-Client.log"
host_arbiter_log="$host_dir/arbiter_log.txt"
host_save="$host_dir/SaveData"
client_save="$client_dir/SaveData"

if (( ISOLATE_SAVEDATA )); then
    if (( SEED_CONFIG )); then
        save_note="isolated per role, mod list seeded from $CONFIG_SOURCE"
    else
        save_note="ISOLATED (fresh ModsConfig — not your Steam list)"
    fi
else
    save_note="default ~/Library/Application Support/RimWorld (shared mods/config)"
fi

# Copy just enough config that an isolated instance still loads the same mods —
# without this the Multiplayer mod itself is missing and the run is pointless.
seed_savedata() {
    local src_cfg="$CONFIG_SOURCE/Config" dest_cfg="$1/Config" f
    mkdir -p "$dest_cfg"
    [[ -d "$src_cfg" ]] || { printf 'warning: no config to seed at %s\n' "$src_cfg" >&2; return 0; }
    for f in ModsConfig.xml Prefs.xml KeyPrefs.xml; do
        if [[ -f "$src_cfg/$f" ]]; then cp "$src_cfg/$f" "$dest_cfg/$f"; fi
    done
    # Per-mod settings live beside them and some mods refuse to load without theirs.
    for f in "$src_cfg"/Mod_*.xml; do
        if [[ -f "$f" ]]; then cp "$f" "$dest_cfg/"; fi
    done
    # RimWorld re-applies its own screen size from Prefs.xml on top of Unity's
    # window keys, so the copied one has to agree with the tile or it undoes it.
    if (( TILE )) && [[ -f "$dest_cfg/Prefs.xml" ]]; then
        sed -i '' \
            -e "s|<screenWidth>[0-9]*</screenWidth>|<screenWidth>$tile_w</screenWidth>|" \
            -e "s|<screenHeight>[0-9]*</screenHeight>|<screenHeight>$tile_h</screenHeight>|" \
            -e "s|<fullscreen>[^<]*</fullscreen>|<fullscreen>False</fullscreen>|" \
            "$dest_cfg/Prefs.xml"
    fi
    return 0
}

# Logical screen size in points, which is what Unity's window coordinates use.
detect_screen() {
    local line w h
    line="$(system_profiler SPDisplaysDataType 2>/dev/null \
            | awk -F': ' '/UI Looks like:/ {print $2; exit}')"
    [[ -n "$line" ]] || line="$(system_profiler SPDisplaysDataType 2>/dev/null \
            | awk -F': ' '/Resolution:/ {print $2; exit}')"
    w="$(awk '{print $1}' <<<"$line")"
    h="$(awk '{print $3}' <<<"$line")"
    if [[ "$w" =~ ^[0-9]+$ ]] && [[ "$h" =~ ^[0-9]+$ ]]; then
        SCREEN_W="$w"; SCREEN_H="$h"
    else
        SCREEN_W=1680; SCREEN_H=1050
        printf 'warning: could not read display size, assuming %sx%s\n' "$SCREEN_W" "$SCREEN_H" >&2
    fi
}

# Unity reads these at startup, so writing them immediately before each launch
# places that instance. Both instances share the domain, hence the staggering.
place_window() {
    local role="$1" x="$2" y="$3" w="$4" h="$5"
    printf '[%s] window %sx%s at %s,%s\n' "$role" "$w" "$h" "$x" "$y"
    if (( DRY_RUN )); then return 0; fi
    defaults write "$UNITY_PREFS_DOMAIN" "Screenmanager Window Position X" -int "$x"
    defaults write "$UNITY_PREFS_DOMAIN" "Screenmanager Window Position Y" -int "$y"
    defaults write "$UNITY_PREFS_DOMAIN" "Screenmanager Resolution Width" -int "$w"
    defaults write "$UNITY_PREFS_DOMAIN" "Screenmanager Resolution Height" -int "$h"
    defaults write "$UNITY_PREFS_DOMAIN" "Screenmanager Resolution Use Native" -int 0
    defaults write "$UNITY_PREFS_DOMAIN" "Screenmanager Fullscreen mode" -int 3
    return 0
}

start_role() {
    local role="$1" work_dir="$2" log_path="$3" save_path="$4"
    local -a args=(-logfile "$log_path")

    if (( ISOLATE_SAVEDATA )); then
        mkdir -p "$save_path"
        if (( SEED_CONFIG )); then seed_savedata "$save_path"; fi
        args+=("-savedatafolder=$save_path")
    fi

    printf '[%s] cwd=%s\n' "$role" "$work_dir"
    printf '[%s] log=%s\n' "$role" "$log_path"

    if (( DRY_RUN )); then
        printf '[%s] would run: %q' "$role" "$exe"
        printf ' %q' "${args[@]}"
        printf '\n'
        return 0
    fi

    # nohup execs the game in place, so the backgrounded PID is RimWorld's own.
    (
        cd "$work_dir" || exit 1
        nohup "$exe" "${args[@]}" >/dev/null 2>&1 &
        echo $! > "$work_dir/.pid"
    )
    local pid; pid="$(cat "$work_dir/.pid")"; rm -f "$work_dir/.pid"
    printf '[%s] PID %s — waiting for startup...\n' "$role" "$pid"

    local waited=0 limit=$(( STARTUP_TIMEOUT_SEC * 2 ))
    while (( waited < limit )); do
        if ! kill -0 "$pid" 2>/dev/null; then
            printf 'warning: [%s] process exited before it finished starting\n' "$role" >&2
            return 0
        fi
        if [[ -s "$log_path" ]]; then
            printf '[%s] up (log is being written)\n' "$role"
            return 0
        fi
        sleep 0.5
        waited=$(( waited + 1 ))
    done
    printf 'warning: [%s] timed out waiting for %s\n' "$role" "$log_path" >&2
}

mkdir -p "$host_dir" "$client_dir"

generated_at="$(date '+%Y-%m-%d %H:%M:%S')"
cat > "$run_dir/README.txt" <<EOF
Multiplayer test run $RUN_ID
Generated $generated_at

Host Player.log:     $host_log
Host arbiter log:    $host_arbiter_log
Client Player.log:   $client_log
Save/config:         $save_note

macOS cannot rename another app's window, so both instances look alike in the
Dock and Cmd-Tab. Tell them apart by position: CLIENT is the left half of the
screen, HOST is the right half. Unity's window prefs are shared between the two
instances, so whichever quits last writes its geometry back over them.
EOF

printf 'Run id:  %s\n' "$RUN_ID"
printf 'Run dir: %s\n' "$run_dir"
printf 'Save/config: %s\n' "$save_note"
if (( ISOLATE_SAVEDATA )) && (( ! SEED_CONFIG )); then
    printf 'warning: isolated save data without seeding — the Multiplayer mod will NOT be loaded\n' >&2
fi
printf '\n'

if (( TILE )); then
    detect_screen
    tile_w=$(( SCREEN_W / 2 ))
    tile_h=$(( SCREEN_H - 80 ))
    tile_y=40
    printf 'Layout: %sx%s screen — CLIENT left half, HOST right half\n' "$SCREEN_W" "$SCREEN_H"
    # Only the isolated seed lets us set the size too: RimWorld re-applies the
    # width/height from whichever Prefs.xml it loads, overriding Unity's keys.
    if (( ! ISOLATE_SAVEDATA )); then
        printf 'note: without --isolate-savedata the windows are offset but keep your normal\n'
        printf '      size, so they will overlap. Add --isolate-savedata for a clean split.\n'
    fi
    printf '\n'
    place_window client 0 "$tile_y" "$tile_w" "$tile_h"
fi

start_role client "$client_dir" "$client_log" "$client_save"
printf 'Client launched.\n\n'

if (( HOST_DELAY_SEC > 0 )) && (( ! DRY_RUN )); then
    printf 'Waiting %s s before host...\n' "$HOST_DELAY_SEC"
    sleep "$HOST_DELAY_SEC"
fi

if (( TILE )); then
    place_window host "$tile_w" "$tile_y" "$tile_w" "$tile_h"
fi

start_role host "$host_dir" "$host_log" "$host_save"
printf 'Host launched. Arbiter log (after enable in UI): %s\n\n' "$host_arbiter_log"

printf 'Tail logs:\n'
printf "  tail -f %q\n" "$host_log"
printf "  tail -f %q\n" "$client_log"
printf "  tail -f %q\n" "$host_arbiter_log"
