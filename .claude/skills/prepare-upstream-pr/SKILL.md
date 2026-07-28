---
name: prepare-upstream-pr
description: Take the current feature branch's commits, check whether they depend on fork-only changes, and produce a squashed, upstream-ready branch off dev pushed to the fork. Use when asked to "prepare a branch for the mainstream", "prepare a PR for upstream", "upstream this branch", or "make a PR branch".
---

# Prepare an upstream-ready branch

Turns the current feature branch into a single squashed commit on top of the mainstream
`dev`, pushed to the fork, plus either a PR link (no fork-only dependencies) or a tracking
issue (dependencies found).

## Repository model

| Ref | Meaning |
| --- | --- |
| `origin` | **Upstream**, `rwmt/Multiplayer`. Not the fork — the name is historical. |
| `fork` | **The fork**, `romangr/Multiplayer`. |
| `dev` | Mirror of mainstream `origin/dev`. Never commit here directly. |
| `integration` | Latest integrated state of the fork: everything we run, including changes not yet upstreamed. Feature branches start here. |
| feature branches | Branched from `integration`, merged back into `integration`. |

`dev..integration` is the **fork-only backlog**: changes upstream does not have yet. A
feature branch is upstreamable as-is only if it does not depend on that backlog.

Do not name a branch `continuous` — upstream publishes a rolling release *tag* with that
name and the two collide, which is why the integration branch is called `integration`.

`gh` is not installed on this machine. Never try to create PRs or issues via the API or
`gh`; produce prefilled URLs and let the user click them.

## Procedure

### 1. Preconditions

```bash
git rev-parse --abbrev-ref HEAD          # feature branch
git status --porcelain                   # working tree
git fetch fork && git fetch origin
```

- Abort if `HEAD` is `dev`, `integration`, or `master` — there is nothing to prepare.
- The main working tree is **never** touched: all branch construction happens in a scratch
  worktree (step 4), so the user stays on their feature branch and their uncommitted work,
  dirty `Languages` submodule pointer, and untracked scratch dirs (`Desync-*/`, `docs/`,
  `session.txt`) are irrelevant. Do not ask the user to stash.
- Uncommitted tracked changes on the feature branch are **not** included — only commits are.
  If `git status --porcelain` shows modified tracked files, say which, and confirm the user
  meant to leave them out.
- Verify the fork's `dev` still mirrors upstream:
  ```bash
  git rev-list --left-right --count origin/dev...fork/dev
  ```
  Anything other than `0	0` means the fork's `dev` drifted. Offer
  `git push fork origin/dev:dev` (or `--force-with-lease` if it diverged) before continuing.

### 2. Collect the change set

```bash
BASE=$(git merge-base fork/integration HEAD)
git log --oneline $BASE..HEAD            # commits to squash
git diff --stat $BASE..HEAD              # files touched
git log --oneline fork/dev..fork/integration   # fork-only backlog
```

If `$BASE..HEAD` is empty, stop and say the branch has no commits over `integration`.

Read the full `git diff $BASE..HEAD` — you need it for both the dependency review and the
commit message.

### 3. Dependency review

The branch depends on the fork-only backlog if **either** check fires.

**a. Textual.** Replay the commits onto `dev` in a scratch worktree under the session
scratchpad, so the user's checkout is never touched. This same worktree becomes the PR
branch in step 4 — cherry-pick once, not twice.

```bash
WT="<scratchpad>/wt-upstream"
git worktree add -f --detach "$WT" fork/dev
git -C "$WT" cherry-pick -n $BASE..<feature-branch>
git -C "$WT" diff --cached --stat
```

Conflicts mean the change sits on lines the backlog introduced or moved — a dependency.
Record the conflicting files and the conflicting hunks, then `git cherry-pick --abort` to
get back to a clean scratch branch. Never resolve such a conflict by hand and ship the
result as an upstream PR: the resolution would be inventing a version of the change that
was never tested.

**b. Semantic.** A change can apply cleanly and still be broken on `dev`. For each backlog
commit, list the symbols and files it introduces:

```bash
git show --stat <backlog-commit>
git diff fork/dev...fork/integration -- '*.cs' | grep -E '^\+' | grep -E '(class|interface|enum|void|public|internal|static)'
```

Then grep the feature diff's **added and context** lines for those symbols. Flag anything
the feature branch references that only exists in `dev..integration`: new methods, fields,
types, sync handlers, files, or renamed members.

**c. Confirm by building.** The strongest check — the squashed branch must compile against
`dev` alone. Use the `docker-build` skill. A build failure naming a missing symbol is a
confirmed dependency, not a flake. Skip only if the user declines the build time, and say
so in the report.

A scratch worktree's `.git` is a *file* containing `gitdir: …/Multiplayer/.git/worktrees/…`,
so a container that mounts only the worktree cannot resolve it and `MSBuildGitHash`
(`git describe`) fails with exit 128. Mount the main repo at its real path alongside the
worktree, or run the build against `pr/<topic>` checked out in the main tree if it is clean.

Report each finding as: *what* the branch uses → *which* backlog commit added it.

### 4. Build the squashed branch

The changes are already staged in `$WT` from step 3a. Name the branch `pr/<topic>` — short,
kebab-case, describing the change (`pr/join-data-mod-diffs`). `checkout -b` keeps the
staged index:

```bash
git -C "$WT" checkout -b pr/<topic>
git -C "$WT" commit
git worktree remove --force "$WT"     # the branch survives the worktree
```

Two things must never reach an upstream PR — unstage them before committing if the step 3a
stat listed either:

- `Languages` — the submodule pointer got swept in. `git -C "$WT" restore --staged Languages`,
  unless the change is genuinely about translations. Upstream does not want our submodule ref.
- `.claude/` — fork-only tooling, tracked on `integration` and nowhere upstream.
  `git -C "$WT" restore --staged .claude`.

Commit message: a single imperative subject line under ~72 chars matching the upstream log
style (`Sync EndCurrentJob in FloatMenuOptionProvider_DraftedMove.PawnGotoAction`,
`Filter map player home incident tags`). Add a short body only when the *why* is not
obvious from the subject. No `Co-Authored-By` trailer, no `Generated with` footer — these
go to a third-party repo.

Push:

```bash
git push -u fork pr/<topic>
```

No `checkout` is needed afterwards — the main tree never left the feature branch.

Never push to `origin`. Never force-push over an existing `pr/*` branch without checking
whether a PR already points at it.

### 5. Report

**No dependencies** — give the cross-fork PR link:

```
https://github.com/rwmt/Multiplayer/compare/dev...romangr:Multiplayer:pr/<topic>?expand=1
```

State the commit subject, files touched, and that it builds against `dev`.

**Dependencies found** — do *not* suggest the PR link as ready. Give a prefilled issue URL
on the **fork** (`romangr/Multiplayer`), since this is our own tracking, and print the title
and body as plain text too so the user can paste them if the URL is unwieldy.

Build the URL with proper encoding:

```bash
python3 - <<'EOF'
import urllib.parse
title = "..."
body  = "..."
print("https://github.com/romangr/Multiplayer/issues/new?"
      + urllib.parse.urlencode({"title": title, "body": body}))
EOF
```

Issue title: `Upstream <topic>` — concise, names the change, not the branch.

Issue body must contain:

- **Branch:** `pr/<topic>` (pushed to fork) — so the prepared work is not lost.
- What the change does, in two or three sentences.
- **Depends on:** a bullet per blocking change — the backlog commit (short SHA + subject)
  and *why* it blocks: the symbol used, the conflicting file, or the build error.
- **Unblocked when:** the upstream PRs carrying those dependencies are merged and `dev`
  is refreshed.

Say plainly that the branch is not ready to open against upstream yet, and that it becomes
ready once the listed dependencies land in `dev`.

## Keeping the model healthy

- After an upstream PR merges: `git fetch origin && git push fork origin/dev:dev`, then
  `git checkout dev && git pull`, then rebase or merge `dev` into `integration`.
- `dev..integration` shrinking to nothing means the fork is fully upstreamed.
- Feature branches always start from `integration`, never from `dev`.
