# Project Progress - SAM_SolarCalculator (2026-Q4)

## Branch

`sow/2026-Q4` - bootstrapped 2026-10-06 from `master` `9f50c91f`. Frozen Q3 record: `sow/2026-Q3` @ `9c70f7a7` (not modified).

## Last updated

2026-10-06 (Q4 bootstrap).

## Current status

Q4 branch cut from `master` `9f50c91f`, which is the exact commit pinned in SAM_Deploy's frozen Q3 baseline (`v20261006.1`). Bootstrap added only internal docs (this file, `AGENTS.md`). No product source changed. No Q4 product work has started.

## Q4 priorities

Not yet set by the owner. Record them here at the first Q4 planning pass. Known carry-over work is listed below.

## Known carry-over work

- **SAM Grasshopper icon redesign - PR #28** (`feature/sam-gh-icon-redesign`, open, base `sow/2026-Q3`). Known Q4 carry-over. Preserved untouched at bootstrap: not merged, not retargeted. The branch is based on Q3 history (5 commits unique to it vs `sow/2026-Q3`), and `sow/2026-Q3` is not an ancestor of `master`/`sow/2026-Q4`; retargeting needs an explicit rebase-onto decision (carry only its own commits, do not pull Q3 history into Q4).

## Repository-specific next steps

- Await Q4 planning. Open PRs for Q4 work against `sow/2026-Q4`.
- Follow the continuity convention in `AGENTS.md` for every PR and closeout.

## Decisions / assumptions

- Q4 base is `master` `9f50c91f`; the internal files were recovered from `sow/2026-Q3` into this branch only, never onto `master`.
- Q4 history intentionally does not contain the Q3 branch history (the maintained `master` is the promoted Q3 line, which is not a descendant of `sow/2026-Q3`); the frozen `sow/2026-Q3` branch is the permanent record.
- Historical Q2/Q3 content below is kept as evidence; its branch names, SHAs and next steps describe Q3 and are not current instructions.

## Validation

- Bootstrap verified 2026-10-06: `sow/2026-Q4` was created at exactly `9f50c91f` and the push was a normal (non-forced) branch creation.

## Issues / blockers

- None at bootstrap.

## Next step

- Owner to set Q4 priorities; then start the first Q4 task from this branch.

---

# Historical record - 2026-Q3 (frozen)

Source: last revision of the file on `sow/2026-Q3`, commit `9d2c131` (the file was removed from the Q3 tip by `235589b`; `sow/2026-Q3` tip is `9c70f7a7`). Preserved verbatim except that heading levels are shifted down one. Everything below describes Q3 and is not a current instruction.

## Project Progress

### Branch
`sow/2026-Q3`

### Last updated
2026-09-22 - app.config cleanup merged

### Current status
Part of the repo-family .NET Framework `app.config` cleanup: base [SAM#126](https://github.com/SAM-BIM/SAM/pull/126) plus 17 sibling PRs, all merged into `sow/2026-Q3` on 2026-09-22 (SAM first), with their branches deleted.

### Completed
- [SAM_SolarCalculator#26](https://github.com/SAM-BIM/SAM_SolarCalculator/pull/26) merged as `cc95230d`: removed dead .NET Framework `app.config` files.

### Decisions / assumptions
- Every project here targets `netstandard2.0` or `net8.0(-windows)` and is an `OutputType Library`. Library `.dll.config` files are never read at runtime (only the host `Rhino.exe`/`Revit.exe` config is), so the net472-era binding redirects, `<supportedRuntime>` and `loadFromRemoteSources` were inert. They only emitted stale `.dll.config` files into `build/` and `%APPDATA%\SAM`.
- No `ConfigurationManager`/`AppSettings` use in the repo; deleted files held binding/runtime config only.

### Files changed
- `Grasshopper/SAM.Analytical.Grasshopper.SolarCalculator/app.config` (deleted)
- `SAM_SolarCalculator/SAM.Analytical.SolarCalculator/app.config` (deleted)

### Validation
- Before merge, full `BuildAlls_v4.bat` (Debug Restore;Clean;Rebuild of every repo, starting from an emptied `%APPDATA%\SAM`): exit 0, 0 errors. The redeployed `%APPDATA%\SAM` has no `SAM.*.dll.config`.
- CI on the PR: build + spdx pass.

### Issues / blockers
- None known for this cleanup.

### Next step
- None for this cleanup. Continue with the next planned task on `sow/2026-Q3`.
