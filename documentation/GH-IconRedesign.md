# SAM Grasshopper icon redesign — SAM_SolarCalculator PR record

Branch `feature/sam-gh-icon-redesign`, based on `sow/2026-Q3` @ `9b83399`. PR: SAM-BIM/SAM_SolarCalculator#28.
Propagates the SAM icon design system from SAM-BIM/SAM#166 (head `cf4d924a`, open, not merged) to this repository.

## Current status
All **24** Grasshopper objects in this repo (21 components + 3 params) use redesigned icons: **24 / 24**.
Built and validated; ready for review. **Not merged.**

## Work completed
- `design/grasshopper-icons/`: the shared SAM-BIM icon kit. `icons.py`, `render.py`, `sam_classify.py` and `ICON_DESIGN_SYSTEM.md` are vendored **verbatim** from SAM#166 (hash-checked). `icons_ext.py` and `ICON_DESIGN_SYSTEM_EXT.md` are the frozen SAM-BIM extension v1 (identical in every SAM-BIM repo). `tools/repo_rules.py` holds this repo's explicit decisions.
- **Inventory**: `tools/inventory.py` parses C# source (every non-abstract class declaring `ComponentGuid`).
- **Manifest** (source of truth): `manifest.json` / `manifest.csv` — per object: GUID, class, source, project, object glyph, operation, modifiers, icon id, resource, glyph/badge origin.
- **Generation**: 23 canonical SVGs → 24×24 PNGs; review sheet `review/contact_sheet.png` (native 24 px on GH normal / orange-warning / dark bodies + 3×) and `review/REVIEW.md`.
- **Integration**: each project's existing mechanism; only the icon token inside each `Icon` getter changes.

| Project | Objects | Icon resources | Mechanism |
|---|---|---|---|
| `SAM.Analytical.Grasshopper.SolarCalculator` | 22 | 21 | resx / Bitmap |
| `SAM.Geometry.Grasshopper.SolarCalculator` | 2 | 2 | resx / Bitmap |

## Design reuse
- **Reused SAM object families (5)**: `aperture`, `clock`, `profile`, `result`, `shade`
- **New SAM-BIM ext v1 families used (2)**: `solar`, `sunPath`
- **Verbs**: `analyse`, `calculate`, `create`, `filter`, `get`, `inspect`, `merge`, `modify`, `run`, `validate`; new ext verb: `run`
- Distinct icons: **23** (15 on SAM glyphs, 8 on ext glyphs). Icon ids shared with SAM render pixel-identically to SAM's.

## Decisions and assumptions
- Grammar, palette, badge families and construction rules are unchanged (SAM#166). No text, no new colours.
- Qualifier variants (`…By<X>`) share an icon intentionally (see `review/REVIEW.md`).
- Interop direction: external → SAM = import ↓, SAM → external = export ↑.
- Solar irradiance/exposure/simulation use the ext `solar` glyph and sun direction uses ext `sunPath`; shading devices and schemes reuse SAM `shade` (compare = analyse, select = filter, rationalise = modify, verify = validate).
- `AnalysisPeriod` = SAM `clock` + create; `SolarControlProfile` = SAM `profile` + calculate; `ApertureSolarTargets` = apertures filter.
- Legacy icon resources are kept (still referenced by context menus / AssemblyInfo); no GUID, name, nickname, category, subcategory, parameter or behaviour change.

## Files changed
- New: `design/grasshopper-icons/**`, `<project>/Resources/Icons/SAM_GH_*.png`, `documentation/GH-IconRedesign.md`.
- Modified: 24 component/param `.cs` files (one icon token each), 2× `Resources.resx`, 2× `Resources.Designer.cs`. No csproj change.

## Validation
| Check | Result |
|---|---|
| `tools/classify.py` | 24 classified, 0 unclassified |
| `tools/build.py` identical-pixel collision check | 0 groups (23 distinct icons; 1 intentionally shared icon(s) for qualifier variants, listed in `review/REVIEW.md`) |
| Icon ids shared with SAM#166 vs SAM's `png/24` | 3 shared, 3 byte-identical |
| `tools/integrate.py` re-parse | 24/24 objects reference their `SAM_GH_*` resource; every PNG exists |
| `tools/check_source.py` vs `origin/sow/2026-Q3` | vendored files OK; icon-token swaps: 24, non-icon changes: 0; base 24, now 24 -> UNCHANGED |
| `dotnet build SAM_SolarCalculator.sln -c Debug` | Build succeeded, 0 errors |
| `tools/check_assemblies.py` | every assembly embeds every required 24×24 icon → OK |
| `tests/GhIconTest` (real Rhino 8 / Grasshopper, Rhino.Testing) | 24/24 objects verified: 24 load in real Rhino 8 / Grasshopper by GUID (name/category match); icon = manifest PNG (max diff 1 level, premultiplied-alpha rounding) |
| `SAM.SolarCalculator.Tests` | 564 passed, 1 skipped (18 min) |
| Visual review (`review/contact_sheet.png`, 24 px on normal / warning / dark bodies) | all icons legible; no collisions |

## Unresolved issues / risks
- Built against sibling repos as checked out locally (SAM on `feature/sam-gh-icon-redesign` = SAM#166); icon changes are API-neutral.

## Recommended next step
Review this PR (compare `review/contact_sheet.png`), then merge by the maintainer. After merge, add the `PROJECT_PROGRESS.md` closeout entry on `sow/2026-Q3` with the merge SHA. SAM#166 (the reference design system) remains open.

## SPDX header policy (CI `spdx-check`)
The repository SPDX check requires the LGPL-3.0-or-later SPDX line and the copyright line in every `.cs` file a PR changes. The icon-token swaps touched 2 older files that predated the policy (components, `Resources.Designer.cs`), and the kit test `IconTests.cs` had no header. The standard 2-line header was added to them; nothing else changed. `tools/check_source.py` accepts exactly this header as the only non-icon addition and compares against the merge base.
