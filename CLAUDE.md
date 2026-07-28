# Multiplayer — fork working notes

RimWorld Multiplayer mod. C#, solution at `Source/Multiplayer.sln`. Mod code targets
`net48`; `Source/Tests` targets `net8.0`; `Source/SourceGen` is `netstandard2.0`.

**This checkout is a maintained fork, not a scratch copy of upstream.** Changes live here
first and are upstreamed selectively.

## Remotes and branches

| Ref | Meaning |
| --- | --- |
| `origin` | **Upstream**, `rwmt/Multiplayer`. Not the fork — the name is historical and misleading. |
| `fork` | **The fork**, `romangr/Multiplayer`. |
| `dev` | Mirror of `origin/dev`. Never commit here. Upstream PRs target this, not `master`. |
| `integration` | The fork's integrated state — everything we actually run, including work not yet upstreamed. Feature branches start and land here. |
| `pr/*` | Squashed, upstream-ready branches cut from `dev`. Created by the `prepare-upstream-pr` skill; not merged back into `integration`. |
| `master` | Stale upstream default. Dormant on this fork. |

`dev..integration` is the **fork-only backlog**: what upstream does not have yet. It is the
central fact for any upstreaming decision — a change is upstreamable as-is only if it does
not depend on that backlog. Read it with `git log --oneline dev..integration`.

Refresh after an upstream merge: `git fetch origin && git push fork origin/dev:dev`, then
rebase or merge `dev` into `integration`.

**Never name a branch `continuous`.** Upstream publishes a rolling release *tag* with that
name and both remotes carry it, so a same-named branch makes every `git … continuous`
ambiguous and breaks plain `git push`. That collision is why the integration branch is
called `integration`.

## Fork-only files

Tracked on `integration`, must never reach an upstream PR:

- `CLAUDE.md` (this file)
- `.claude/skills/` — `docker-build`, `prepare-upstream-pr`

`.claude/settings.local.json` is ignored globally on this machine, not by the repo.

## Environment

- **No local `dotnet`/`msbuild`/`mono`.** Build and test through the `docker-build` skill.
  Do not suggest bare `dotnet build`.
- **No `gh` CLI and no GitHub token.** Never attempt PR or issue creation via `gh` or the
  API — produce prefilled GitHub URLs for the user to click.
- Shell is `fish`, not bash. Watch for `*` glob expansion differences in ad-hoc commands.

## Repo hygiene

- `Languages` is a **submodule** (`rwmt/Multiplayer-Locale`) and its pointer is routinely
  dirty in this checkout. Never stage it unless the change is genuinely about translations.
- The repo root carries untracked scratch that is normal and should be left alone:
  `Desync-*/`, `docs/`, `session.txt`, `ISSUE-*.md`, `*.log`, loose `.dll`/`.xml` captures.
  Do not offer to clean these up or add them to `.gitignore` unasked.
- Because the tree is persistently dirty, prefer a scratch `git worktree` over
  `checkout`/`stash` when building branches, so the user's working state is never disturbed.

## Upstreaming

Use the `prepare-upstream-pr` skill for "prepare a branch for the mainstream". It squashes
the current feature branch onto `dev`, checks it against the fork-only backlog, and returns
either a PR link or a tracking issue. Upstream commit messages get no `Co-Authored-By` or
`Generated with` trailers — those go to a third-party repo.
