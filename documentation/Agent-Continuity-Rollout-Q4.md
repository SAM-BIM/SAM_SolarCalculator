# Agent instructions rollout — Q4 (2026-10-12)

Status: implemented on `docs/agent-instructions-rollout-q4`; owner review required. Not merged.
Kind: Rollout of the shared baseline.
Base: `sow/2026-Q4` at `0e4a5ec8102d955370e21c53f9bdfa4a7efa8f00`.
PR: the live PR for this branch in `SAM-BIM/SAM_SolarCalculator` (its number is not committed here).
Tested content SHA: `82d6875b44a29648da2e0fd9e5d18c3ee86734a1` (changes only AGENTS.md, CLAUDE.md).
Final PR head and hosted check results: recorded in the live PR description. A record
cannot contain its own commit SHA, so this file is the only change after the tested SHA.
Baseline: SAM-BIM/SAM#191 (merge `02bbafb8`, closeout `46e96b72`) and SAM-BIM/SAM_OCCT#74
(merge `3d0a4725`, closeout `c825a40`).

## Scope

Documentation only. No product code, build, workflow, dependency pin or tool changed.
`PROJECT_PROGRESS.md` is not changed on this branch; its closeout follows merge.

## Changes

- `AGENTS.md`: moves from the 2026-10-06 bootstrap template to the SAM#191 / SAM_OCCT#74 baseline (task-specific progress reads, SAM-BIM-only operations, quarter transition, evidence rules) plus merge conditions, guarded post-merge branch cleanup and the model/effort handoff rule. The repository's own branch-context line is kept.
- `CLAUDE.md` (new): `@AGENTS.md` import, same convention as the pilots.

Shared rules added or confirmed in `AGENTS.md`: Git and committed records are authoritative; SAM-BIM-only
operations with validated URLs; heading-first, task-specific progress reads; feature branch + owner-reviewed PR
for everything except the post-merge `[skip ci]` progress closeout; tested-SHA, local-versus-hosted,
dependency-order and remaining-gap evidence; merge only after owner approval, final-head checks and resolved
findings; guarded post-merge cleanup of the task's branches (expected-SHA check, full-merge check, no open PR,
lease-guarded delete, no worktree deletion); frozen quarters; internal agent files stay off master promotions;
model/effort/session/mode on every recommendation.

## Preserved repository-specific rules

- No repository-specific rule existed beyond the shared template; the repository's own `master` base SHA in Branch context is kept.

## Validation

Local, documentation checks only (no product build or test was run, none applies):

```bash
base=0e4a5ec8102d955370e21c53f9bdfa4a7efa8f00
tested=82d6875b44a29648da2e0fd9e5d18c3ee86734a1
final=$(git rev-parse HEAD)   # must equal the final head in the PR description
for revision in "$tested" "$final"; do
  git diff --check "$base" "$revision"; echo "check $revision exit=$?"
  git diff --name-only "$base" "$revision"
  git diff --quiet "$base" "$revision" -- PROJECT_PROGRESS.md; echo "progress $revision exit=$?"
done
git diff --name-only "$tested" "$final"
git show "$final":CLAUDE.md | grep -n '^@AGENTS.md'
```

Expected: every `exit=` is 0; the full-PR file lists contain only AGENTS.md, CLAUDE.md and this record
(at the tested SHA only AGENTS.md, CLAUDE.md); the tested-to-final list contains only this record; the import
line is found. PowerShell works with `git rev-parse HEAD` and `if ($LASTEXITCODE -ne 0)` checks.

Result at the tested SHA (2026-10-12, Windows, Git for Windows): `git diff --check` exit=0;
`PROJECT_PROGRESS.md` unchanged exit=0; changed files: AGENTS.md, CLAUDE.md; no CR, no BOM, final newline;
no unreplaced template tokens; `sow/2026-Q4` active, `sow/2026-Q3` frozen, no `sow/2026-Q2` reference.
The same checks at the final head and the hosted PR checks are reported in the PR description;
they are not asserted here. No agent-client token/behaviour benchmark was run for this rollout.

## Remaining decisions and next step

- Merge needs owner approval, passing final-head checks and resolved/accepted review findings. Code-owner review is required only for merges to `master` (owner decision recorded in the pilot closeouts).
- After merge: a docs-only `[skip ci]` closeout in `PROJECT_PROGRESS.md` on `sow/2026-Q4` with the merge SHA, then guarded cleanup of `docs/agent-instructions-rollout-q4` per `AGENTS.md`. Stale bootstrap wording already in `PROJECT_PROGRESS.md` is not edited by this PR; labelling it is a closeout matter that needs explicit owner authorization.
- Machine-local: the 29-repository rollout inventory, worktrees and cross-repository status table are in the
  local parent `PROJECT_PROGRESS.md` checkpoint (not Git-tracked). The committed cross-repository table is in
  SAM-BIM/SAM's `documentation/Agent-Continuity-Rollout-Q4.md` once that PR lists all rollout PRs.