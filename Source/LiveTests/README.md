# API-driven Multiplayer live tests

This is a separate test mod and Python runner for native RimWorld **1.7**.
It is not part of the distributed Multiplayer mod or the ordinary solution build.
The current experimental RIMAPI and debug-actions builds are supplied as inputs;
these tools do not rebuild or port them. Docker and Python 3 are required.

## Test plan and acceptance

`cases.json` records the planned coverage. The first seven cases are implemented:
host/join, synchronized fixture setup, commands from either peer, the real stockpile
hidden-filter cache, the production stockpile filter sync handler, and time control.
The remaining cases stay **NOT RUN**, including rituals and save/rejoin.

Actions exposed to RIMAPI are parameterless static debug actions. Hosting/joining
and snapshots are local. Fixture mutations go through registered Multiplayer sync
methods, with a guard that rejects execution outside synchronized commands.
Filter changes invoke Multiplayer's existing patched handler and wrapper serializer.
The fixture is a forbidden steel stack in a dedicated stockpile, with IDs and
sequence state stored in a scribed GameComponent.

The runner freshly discovers each exact action using the shared skill's checked
runner. It does not retry an uncertain mutation. Invocation success alone is
insufficient: fresh log events and matching state on both connected peers establish
acceptance. The time test also requires the client's successful sync-opinion tick
to advance. JSON reports retain the full evidence. A failing case stops the batch;
subsequent cases remain NOT RUN. Any ERROR/FATAL log message fails the run.

The cache probe makes 101 calls to the game's real cached hidden-filter method
and verifies object reuse. This covers the cache path behind the stockpile failure,
but does not draw the complete storage tab or prove exception-path UI cleanup.

## Build and launch

First build Multiplayer using the normal repository build process (this source targets 1.7 only).
Stop any previous test pair before replacing its test DLL. Then build only this mod:

```sh
sdk=$(python3 -c 'import json; print(json.load(open("global.json"))["sdk"]["version"])')
docker run --rm -v "$PWD":/repo -v mpnuget:/root/.nuget/packages -w /repo \
  "mcr.microsoft.com/dotnet/sdk:$sdk" dotnet build Source/LiveTests/LiveTests.csproj \
  -c Release

python3 Source/LiveTests/session.py launch \
  --game "/path/to/RimWorld.app" \
  --api-mod "/path/to/experimental/RIMAPI17" \
  --companion-mod "/path/to/experimental/DebugActions17"
```

Each API mod input must contain its metadata and existing `1.7/Assemblies` DLLs.
The launcher reuses existing links to these inputs, or adds test-only links under
the game's Mods directory. It refuses to replace another folder. Profiles enable
only Harmony, Core, Multiplayer and the three test mods. Existing installed Harmony
and Multiplayer must be discoverable by the game. Normal configs/saves are not used.

Both profiles start **muted** (`volumeMaster=0` and all volume channels zero),
with background execution enabled. The host quick-generates a colony; the client
starts at the main menu. HTTP ports are 8767/8768, and the Multiplayer server binds
only loopback UDP 30512. Every launch creates a fresh ignored artifact directory
containing `.runtime` profiles, logs and `session.json` with exact process IDs.

## Run, report and stop

Use the session path printed by the launcher:

```sh
python3 Source/LiveTests/run_suite.py "/path/to/session.json" \
  --skills-root "/path/to/rimworld-dev-skills"

python3 Source/LiveTests/session.py stop "/path/to/session.json"
```

The API driver bypasses proxies and uses localhost. It writes `report.md` and
`report.json` beside the session. The games remain paused after the run for
inspection. Stop checks that each PID still belongs to the recorded executable
and savedata profile before signaling it. It never kills games by name.

Run acceptance checks without starting the game:

```sh
python3 -m unittest discover -s Source/LiveTests -p 'test_*.py'
```

This suite needs native game processes and is intentionally separate from
`run-local-tests.sh`, which runs assembly probes without starting RimWorld.
