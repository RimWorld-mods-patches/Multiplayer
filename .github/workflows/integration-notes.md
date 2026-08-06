# Integration build (work in progress)

This is an automatically generated snapshot from the `integration` branch. It updates often and **may be broken,
incomplete, or incompatible.**

**Assets on this page**

| Zip                                                  | What it is                                                                              |
|------------------------------------------------------|-----------------------------------------------------------------------------------------|
| `Multiplayer-mod-integration.<date>+<commit>.zip`    | **Mod** — install into RimWorld. Enough to **host** or **join** in-game.                |
| `Multiplayer-server-integration.<date>+<commit>.zip` | **Standalone server** (optional) — headless process only. Not a substitute for the mod. |

Use matching `<date>+<commit>` on both when you use the server zip. Most groups only need the **mod**.

---

## Mod

### Before you start

Ensure that you have installed:

- [ ] RimWorld **1.6** (build ≥ 1.6.4491)
- [ ] **Prepatcher** mod from [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=2934420800) (or
  follow the [manual install](https://github.com/Zetrith/Prepatcher#installation) on GitHub)

### Install the mod (5 steps)

1. Quit RimWorld if it is running (required before replacing mod files).
2. Download `Multiplayer-mod-integration.<date>+<commit>.zip` from **Assets** below  
   (example: `Multiplayer-mod-integration.20260806+3542307.zip`).  
   If you do not see zip files, expand **Assets** on this release page. Share that exact filename with your group.
3. Open your RimWorld **Mods** folder (create it if missing):
    * **Steam (any OS):** Right-click RimWorld → **Manage** → **Browse local files** → open `Mods`.
    * Optional path shortcuts if Browse local files is unavailable (skip if your Steam library is elsewhere):
        - Windows: `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods`.
        - macOS: `~/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods` (right-click
          `RimWorldMac.app` → **Show Package Contents**).
        - Linux: `~/.steam/steam/steamapps/common/RimWorld/Mods`.
4. If `Mods/Multiplayer` already exists, delete it first. Extract the zip, then put the resulting `Multiplayer` folder
   into `Mods` so the path is `Mods/Multiplayer/` (not `Mods/<something>/Multiplayer/`). If you get an extra wrapper
   folder, move the inner `Multiplayer` up into `Mods`.
    - Windows: right-click zip → **Extract All…**, then move `Multiplayer` into `Mods` if needed.
    - macOS: double-click extracts next to the zip (often Downloads) — then move `Multiplayer` into `Mods`.
    - Linux: `unzip` or your archive app, then move `Multiplayer` into `Mods` if needed.
5. Start RimWorld. In **Mods**:
    - Enable **Prepatcher** and **Multiplayer [Integration]**.
    - Do not also enable Workshop **Multiplayer** (the one with Steam icon).
    - Load order (top → bottom): Core → enabled expansions → **Prepatcher** → **Multiplayer [Integration]** → other
      mods.

### Verify the mod

- [ ] Folder path is `Mods/Multiplayer/` (not `Mods/<wrapper>/Multiplayer/`).
- [ ] **Prepatcher** and **Multiplayer [Integration]** are enabled; Workshop **Multiplayer** is off.
- [ ] Load order matches step 5.
- [ ] On the Mods screen those two mods show no yellow/red warnings; after **Continue**, the main menu loads normally.

If those hold, the mod install is done. You can host or join from the in-game Multiplayer menu ("Multiplayer" button in
main menu).

### If something goes wrong (mod)

| What you see                                 | Fix                                                                                                                                                                                                                                 |
|----------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Two Multiplayer entries / Workshop conflicts | Disable Workshop Multiplayer; restart                                                                                                                                                                                               |
| Mod under a subfolder                        | Path must be `Mods/Multiplayer/` — move the inner folder up                                                                                                                                                                         |
| Wrong version / won’t load                   | Update RimWorld to 1.6 (≥ 1.6.4491)                                                                                                                                                                                                 |
| Prepatcher / Multiplayer launch errors       | Resubscribe or reinstall Prepatcher ([Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=2934420800) / [manual install](https://github.com/Zetrith/Prepatcher#installation)); keep it above Multiplayer [Integration] |
| Players can’t join each other                | Same mod zip filename (`<date>+<commit>`) for everyone                                                                                                                                                                              |

---

## Standalone server (optional)

Skip unless you want a **headless** (no RimWorld game) dedicated process. In-game hosting uses the **mod** above — no
server zip required. Put the server zip only on the machine that runs the headless process.

### Before you start

Ensure that you have installed:

- [ ] **.NET 8 Runtime** — [Download .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) (if you hesitate what to
  choose on this page then not the SDK, not ASP.NET); check with `dotnet --list-runtimes`
  for `Microsoft.NETCore.App 8.x`

### Install the server (2 steps)

1. Download `Multiplayer-server-integration.<date>+<commit>.zip` from **Assets** below  
   (example: `Multiplayer-server-integration.20260806+3542307.zip`).  
   If you do not see zip files, expand **Assets** on this release page.
2. If `Server/` already exists, delete it first. Extract the server zip somewhere outside RimWorld `Mods` (example: your
   `Documents` folder).

### First-time run (bootstrap)

After installation you should have `Server/Windows/` and `Server/Linux/` (no `macOS/` — on macOS, use `Linux/`). No
manual config files are required up front. A fresh server starts in **bootstrap mode** (first-time setup: no
`settings.toml` / world yet).

Every RimWorld that will connect still needs the **mod** install above. The **first** client configures server in-game.
Other players should wait until step 5.

1. On the **server machine**, open `Server/Windows/` or `Server/Linux/` and run the server **from that folder**:
    - Windows: double-click **Server.exe**.
    - macOS/Linux: `./Server.sh`.
2. Console should show bootstrap waiting (world / `settings.toml` missing). **Leave this process running.** Default port
   is **30502**.
3. On a **RimWorld PC** with that matching Multiplayer **mod** (same stamp as the server zip). Six in-game steps —
   complete in order:
    1. Main menu → **Multiplayer** → **Direct** tab (join by address) → connect with `IP:30502`
       (`127.0.0.1:30502` on the same PC as the server; otherwise the server’s LAN or public IP).
    2. **Server Bootstrap Configuration** opens. On the **Connecting** tab, set options (first run: leave **Direct**
       listen enabled — that is the server listen checkbox, not the join tab).
    3. On the **Gameplay** tab, set options → **Next** (uploads `settings.toml`). Wait until the next screen appears.
    4. Read the faction-ownership warning (**creates the map = owns the main colony**) → **Create game and upload
       save**.
    5. Finish the normal new-game screens (scenario → world → starting site → characters → **Start**).
    6. When the bootstrap UI shows upload progress (and/or the console on the server machine mentions save upload), wait
       until it finishes.
4. **Upload complete = bootstrap done.** The headless server **exits on purpose** and the configuring client disconnects
   for good — that is normal success, not a crash. Do **not** use **Reconnect** yet. Start the server again the same way
   as step 1 (from `Server/Windows/` or `Server/Linux/`).
5. On the **server machine**, console should show it loaded world state from `Saved/` (after seeding from `save.zip`).
   Leave the process running. **Invite others now:** bootstrap player and others reconnect/join via **Multiplayer** with
   the matching mod.

### If something goes wrong (server)

| What you see                                          | Fix                                                                                      |
|-------------------------------------------------------|------------------------------------------------------------------------------------------|
| Headless server won’t start                           | Install .NET 8 Runtime; `dotnet --list-runtimes` shows `Microsoft.NETCore.App 8.x`       |
| Can’t connect / bootstrap UI never opens              | Matching mod zip + stamp; address is `IP:30502`; server still running from the OS folder |
| Stamp / version mismatch errors                       | Same `<date>+<commit>` on mod zip, server zip, and `modVersion` suffix                   |
| LAN works, internet friends cannot                    | Forward / allow **UDP 30502** to the server PC (TCP 30502 if the firewall asks)          |
| Server exited / client disconnected after save upload | Expected — rerun server (step 4), then reconnect (step 5)                                |
| Players join too early                                | Wait until step 5 (loaded from `Saved/`) before others connect                           |
| Upload stuck                                          | Keep RimWorld open until upload finishes; watch bootstrap UI / server console            |
