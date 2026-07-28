---
name: docker-build
description: Build and test the Multiplayer mod in a Docker .NET SDK container, mounting the repo for build caching and retrievable artifacts. Use when asked to build, test, or compile this project without a local dotnet toolchain.
---

# Docker build & test

This machine has no local `dotnet`/`msbuild`/`mono`. Build and run the tests inside a
.NET SDK container instead. The repo is bind-mounted read-write so `obj`/`bin` persist
between runs (incremental caching) and the built artifacts are retrievable on the host.

## Key facts (learned the hard way)

- **Use the .NET 10 SDK image** (`mcr.microsoft.com/dotnet/sdk:10.0`), NOT 9.0.
  `Source/SourceGen` references Roslyn `5.3.0`; SDK 9 ships compiler 4.14, so its source
  generator is refused (`CS9057`) and `ChatCommandRegistry.Register` never gets generated
  (`CS8795`). SDK 10 ships Roslyn 5.x and builds fine.
- **Set `DOTNET_ROLL_FORWARD=Major`.** `Source/Tests` targets `net8.0`; the SDK 10 image
  only carries the 10.0 runtime, so the net8 test host needs roll-forward to run.
- **Keep `.git`.** `Common.csproj` runs `MSBuildGitHash` (`git describe`); building a copy
  without `.git` fails with exit code 128. The bind mount includes `.git`, so this is only
  a concern if you copy the source elsewhere.
- **Cache NuGet in a named volume** (`-v mpnuget:/root/.nuget/packages`). First restore
  pulls `Krafs.Rimworld.Ref` (large); later runs reuse it.
- `bin`/`obj` created in the mount don't show in `git status` (nested `.gitignore` covers
  them), so building in place is safe.

## Build + test (one shot)

Run from the repo root (`/Users/roman_gr/Repositories/Multiplayer`):

```bash
docker run --rm \
  -e DOTNET_ROLL_FORWARD=Major -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -v "$PWD":/repo \
  -v mpnuget:/root/.nuget/packages \
  -w /repo \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -lc '
    set -e
    dotnet build Source/Multiplayer.sln -c Release
    dotnet test Source/Tests/Tests.csproj -c Release --no-build
  '
```

## Variations

- **Build only:** drop the `dotnet test` line.
- **Filter to one test class/method:** append
  `--filter FullyQualifiedName~AssemblyVersionTest` to the `dotnet test` command.
- **Quieter build output:** pipe build through
  `grep -E "error|Build succeeded|Build FAILED"`.
- **Fresh caches:** `docker volume rm mpnuget` (NuGet) and/or delete `obj`/`bin` in the
  repo to force a clean build.

## Where the artifacts land (on the host)

- `Source/Client/bin/Multiplayer.dll` — the mod assembly (`AssemblyName` is `Multiplayer`,
  custom `OutputPath=bin`, no framework subfolder).
- `AssembliesCustom/Multiplayer.dll` + `MultiplayerCommon.dll` — copied by the Client
  post-build step (`ModOutputPath=..\..\`).
- `Assemblies/` — loader and third-party DLLs copied by the post-build step.
- `Source/Server/bin/Release/net8.0/` — standalone server output.

## Deploying to the local RimWorld install

The RimWorld mod dir is a **separate copy**, not a symlink to the repo:
`~/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods/Multiplayer`.
After a build, copy `AssembliesCustom/Multiplayer.dll` and `AssembliesCustom/MultiplayerCommon.dll`
(and anything under `Assemblies/` that changed) into that mod's matching folders, then
restart RimWorld to load the new build.
