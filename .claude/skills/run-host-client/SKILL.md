---
name: run-host-client
description: Launch a local RimWorld host + client pair on macOS to test the Multiplayer mod end-to-end. Use when asked to run, test, or try out the mod with a host and a client, or to reproduce a multiplayer desync/bug locally.
---

# Run host + client (macOS)

Wraps `RimWorldMac-HostClient.sh`, which starts two RimWorld instances directly from the
app bundle binary — `open` would just focus the already-running instance instead of
launching a second one. Each run gets its own timestamped folder holding `Player-Host.log`,
`Player-Client.log`, and `arbiter_log.txt`, then the script returns while both games keep
running.

The copy of that script inside this skill directory is the single source of truth — don't
recreate one at the repo root, or the two will drift again.

## 1. Build

No local `dotnet` on this machine — use the `docker-build` skill.

**Correction to that skill:** it currently says to use the .NET 10 SDK image because
`Source/SourceGen` needs Roslyn 5.x. That's stale. `global.json` now pins SDK `9.0.100`
and `SourceGen` references `Microsoft.CodeAnalysis.CSharp` 4.13.0, so
`mcr.microsoft.com/dotnet/sdk:9.0` is the correct image — SDK 10 fails outright with
"Install the [9.0.100] .NET SDK". Don't edit `docker-build/SKILL.md` for this; just use 9.0
when following it.

## 2. Deploy

The RimWorld mod folder is a **separate copy**, not a symlink to the repo:

```
~/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods/Multiplayer
```

After a build, copy `AssembliesCustom/Multiplayer.dll` and
`AssembliesCustom/MultiplayerCommon.dll` from the repo into that mod's matching folder,
then launch. Host and client must run **identical builds** — the debug/command wire format
has changed between builds before, and mismatched builds desync or silently drop synced
commands.

## 3. Launch

```bash
./.claude/skills/run-host-client/RimWorldMac-HostClient.sh
```

No flags needed — the useful behaviour is all default. The script:

- creates one timestamped run folder with `Player-Host.log`, `Player-Client.log`, and
  `arbiter_log.txt`;
- launches client, then host (after a short delay) from
  `Contents/MacOS/RimWorld by Ludeon Studios` inside the app bundle;
- returns immediately — both instances keep running in the background.

### Flags

| Flag | Effect |
| --- | --- |
| `--no-tile` | Don't place the windows; leave window prefs untouched. |
| `--dry-run` | Print what would happen, launch nothing. |
| `--game-root PATH` | RimWorld install dir (contains `RimWorldMac.app`). |
| `--config-source PATH` | Save-data dir to seed mod config from. |
| `--runs-root PATH` | Where run folders are created (default `<game-root>/MpTestRuns`). |
| `--run-id ID` | Name of the run folder (default timestamp). |
| `--host-delay SECONDS` | Pause between client and host launch (default 2). |
| `--startup-timeout SECS` | How long to wait for each log to appear (default 120). |
| `-h, --help` | Usage text. |

## Telling the two windows apart

macOS can't rename another app's window — the accessibility title is read-only and even
reading it needs Automation consent, so there's no equivalent of the Windows script's
`" HOST"`/`" CLIENT"` window-title suffixes. Instead the script **tiles** the windows:
client on the left half of the screen, host on the right.

- It does this by writing Unity's window keys into
  `~/Library/Preferences/ludeon.rimworld.plist` immediately before each launch; Unity reads
  them at startup, so staggering the two launches places each instance independently.
- That plist is keyed by bundle id and therefore **shared** by both instances, so whichever
  instance quits last writes its geometry back over it. A later solo launch may open at the
  tile size — just resize once. `--no-tile` leaves window prefs untouched entirely.
- Window *position* comes from that shared plist, but *size* does not: RimWorld re-applies
  its own `screenWidth`/`screenHeight` from the `Prefs.xml` it loads, overriding Unity's
  resolution keys. The per-role config below is what lets the script patch each `Prefs.xml`
  to half-screen width, so the two windows tile instead of overlapping.

## What each role gets

Every run gives host and client their own save-data folder (`-savedatafolder=`), holding a
copy of your config from `~/Library/Application Support/RimWorld` — `ModsConfig.xml`,
`Prefs.xml`, `KeyPrefs.xml` and all `Mod_*.xml` — so both instances load the same mods,
including Multiplayer itself.

Only the config is per-role. `Saves`, `MpDesyncs` and `MpReplays` are symlinked back to the
real folder, so both instances open the same saved games you see when launching from Steam,
and desync captures land where you normally look for them rather than in a temporary run
folder. Keeping `Prefs.xml` per-role is what stops the two live instances overwriting each
other's settings, and what makes the window tiling possible.

## Windows

`RimWorldWin64-HostClient.ps1` at the repo root is the Windows counterpart. It's untracked
local scratch, not part of this skill.
