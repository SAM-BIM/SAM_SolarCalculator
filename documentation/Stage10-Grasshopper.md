# Stage 10 implementation — the Grasshopper workflow

Companion to `Stages0-4-Method.md`, `Stages5-8-Method.md` and `Stage9-Method.md`. Those describe the
physics. This describes what an engineer sees on the canvas, what each input does, and which
behaviours are guaranteed.

Stage 10 adds **no physics**. Every number it shows is produced by the Stage 0–9 engine, unchanged.

---

## 1. The chain

```
AnalyticalModel
      │
      ├─────────────────────────────► SAMAnalytical.AnalysisPeriod ──┐  (which hours)
      ▼                                                              │
SAMAnalytical.ApertureSolarTargets ───────────────────────────────┐  │
      │  targets, azimuths, gridSize                              │  │
      ├──► SAMAnalytical.ApertureIrradiance  ◄────────────────────┼──┤  kWh on each window
      │                                                           │  │
      ├──► SAMAnalytical.ShadingPotentialField ◄──────────────────┴──┤  where shading helps / harms
      │            │                                                 │
      │            ├──► SAMAnalytical.IdealShadingShape              │  the shape worth filling
      │            │                                                 │
      │            └──► SAMAnalytical.RationaliseShading ◄───────────┘  a buildable device
      │                          │
      └──────────────────────────┴──► SAMAnalytical.VerifyShading        the ground truth
```

Category **SAM**, sub-category **Solar**. All seven derive from `GH_SAMVariableOutputParameterComponent`
with fixed `ComponentGuid`s and `LatestComponentVersion` `1.0.0`.

| component | Guid |
|---|---|
| `SAMAnalytical.AnalysisPeriod` | `7f3c9a10-…-1101` |
| `SAMAnalytical.ApertureSolarTargets` | `7f3c9a10-…-1102` |
| `SAMAnalytical.ApertureIrradiance` | `7f3c9a10-…-1103` |
| `SAMAnalytical.ShadingPotentialField` | `7f3c9a10-…-1104` |
| `SAMAnalytical.IdealShadingShape` | `7f3c9a10-…-1105` |
| `SAMAnalytical.RationaliseShading` | `7f3c9a10-…-1106` |
| `SAMAnalytical.VerifyShading` | `7f3c9a10-…-1107` |

---

## 2. Precedence — nothing connected is ever discarded silently

| decision | order | when overridden |
|---|---|---|
| **Weather** | supplied `_weatherData_` → the model's `WeatherData` → actionable error | — |
| **Hours** | explicit `_HOYs_` → `_analysisPeriod_` → full year | `Remark`: *Explicit HOYs override the connected AnalysisPeriod.* |
| **Brief** | `_desirability_` → `_unwantedPeriod_` / `_wantedPeriod_` → summer unwanted, winter wanted | `Remark`: *_desirability_ overrides the connected unwanted/wanted periods.* |
| **Period on AnalysisPeriod** | `_HOYs_` → custom date range → `_preset_` → full year | `Remark` naming what lost |
| **Apertures** | `_apertures_` (Apertures or Guids) → every external sun-exposed aperture | requested-but-excluded apertures are reported by count and first Guid |

The default brief is **hemisphere-aware**: a southern-hemisphere `Location` swaps summer and winter.
It is a default, not a recommendation, and the node says it is using it.

---

## 3. Defaults and units

| input | default | unit |
|---|---|---|
| `_gridSize_` | 0.5 | m |
| `_sunAngleStep_` | 2.0 (**unchanged from Stage 9**) | ° |
| `_skyModel_` | PerezAnisotropic | — |
| `_albedo_` | 0.2 | — |
| `_sunTimeConvention_` | IntervalStart (+30 min, the SAM/EPW timeline) | — |
| `_maxDepth_` | 1.0 | m |
| `_voxelSize_` | 0.1 | m |
| `_marginAbove_` / `_marginBelow_` / `_marginSides_` | 0.5 / 0.0 / 0.3 | m |
| `_wantedSolarPenalty_` (λ) | 1.0 | dimensionless |
| `_materialPenalty_` (μ) | 0.1 | dimensionless |
| `_threshold_` | 0.9 (CumulativeCapture) | fraction |
| `_maximumEvaluations_` | 400 | candidates per family |
| `_recalculate_` / `_run` | false | — |

Energies are **kWh**, densities **kWh/m²**, areas **m²**, lengths **m**, angles and orientations **°**,
percentages **%**. Terminology is fixed: **GridSize** (not CellSize), **SunAngleStep** (not BinSize),
**Recalculate** (not RebuildCache). The word *cache* appears in no component name or description.

Every long calculation is gated by `_run`, and returns immediately while it is false.
`IdealShadingShape` has no `_run`: it is arithmetic over an existing map plus one iso-surface, and a
gate there would be an extra wire for nothing.

---

## 4. Reuse and recalculation

The expensive part is the geometric pass — casting rays from every analysis cell at every sun group.
It is stored on the model's `SolarModel` and reused whenever the identity of the analysis matches:
context geometry, target geometry, grid size, sun-angle step, horizon cutoff, tolerances, site
latitude/longitude and time zone, the timeline offset, the year, and the cell count.

Stage 10 routes **both** the Stage 4 irradiance path and the Stage 6–9 shading path through one
factory (`Create.ApertureSolarContext`), so:

* the calculation paid for by `ApertureIrradiance` is reused by `ShadingPotentialField`,
  `RationaliseShading` and `VerifyShading`, and vice versa — provided `_gridSize_` and
  `_sunAngleStep_` match;
* changing the analysis period, the weather, the sky model or the brief costs **no** geometric work;
* changing the model, the grid or the sun-angle step rebuilds automatically and the node emits
  `Geometry changed; solar calculation was updated.`;
* `_recalculate_ = true` forces a rebuild;
* `reusedPreviousCalculation` is an output, so a user can always tell which happened.

Stale results cannot be returned: identity is checked before reuse, never after.

To keep the calculation shareable, the shading components build their context over the model's
**whole** default aperture set, then read the requested aperture's rows out of it. That is why
`_gridSize_` must be kept the same across the chain — a different grid is a different analysis, not a
different view of the same one.

---

## 5. Preview

`GooApertureSolarTarget` draws the opening and an arrow along the **outward** normal — the one thing
worth checking by eye before running anything.

`GooShadingPotentialField` draws one dot per map point at its own centre:

* **red** — shading here blocks unwanted solar (stronger red = more);
* **blue** — shading here destroys wanted solar;
* near-zero points (below 2 % of the field's own extreme) are **not drawn**, because a full grid of
  grey dots hides the answer.

SAM has no shared diverging colour ramp — its colour queries are keyed to panel and aperture types —
so the ramp is defined in the Goo and stays local to this preview. `ShadingPotentialField` also emits
`previewPoints` and `previewColours` for a custom display, and the preview is generated inside a
guard: **a preview failure warns and never removes the numerical field.**

`GooIdealShadingResult` draws the display mesh (see §7).

---

## 6. Resolution warning

Stage 9 found that a device finer than the analysis grid produces numbers that look like a triumph:
eleven blades over a 1 m opening sampled at 0.5 m can sit so every sample is shaded and none of the
gaps are, reporting *100 % of unwanted solar blocked and 100 % of wanted solar retained* at once.

Stage 9's parameter cap keeps an **optimised** device at or above one grid spacing. Stage 10 adds the
reporting side, which also covers devices a user typed in by hand:

| element pitch | state | Grasshopper |
|---|---|---|
| ≤ `GridSize` | `BelowResolutionLimit` | **Warning**: *…below the reliable analysis resolution… Reduce GridSize and recalculate…* |
| < 2 × `GridSize` | `NearResolutionLimit` | **Remark**: *…close to the solar-analysis grid resolution. Reduce GridSize for more reliable element-level results.* |
| otherwise | `Resolved` | nothing |

Pitch is `span / (count − 1)`, the spacing the typologies actually build to, measured **up** the
opening for louvres and **across** it for fins. A single element has no pitch. The rule itself is
Stage 9's and was not redesigned here.

---

## 7. Display geometry is not performance geometry

`IdealShadingShape` returns a mesh. It is for **viewing and take-off only**. The Stage 7/8 finding
stands: the mesh intercepts materially less solar than the voxel solid it represents (28.2 % less
interception and 21.5 points more apparent wanted-solar retention in the Gate 0 Review A case).

The component therefore:

* names the output `mesh` and describes it as DISPLAY geometry in both the component description and
  the output description;
* keeps every number — captured fraction, projected area, volume, depth, region sizes — from the
  **field**, not the mesh;
* reports `meshNote` and warns when no mesh could be produced, while still returning the numbers;
* points the user at `VerifyShading` on a real device for any performance judgement.

This is a known open research question, not a Stage 10 defect, and Stage 10 does not attempt to solve
it.

---

## 8. Optimisation metrics

`RationaliseShading` reports, per family, best first:

`objectiveScore` [kWh] · `benefit` [kWh] · `harm` [kWh] · `cost` [kWh] · `materialFraction` ·
`unwantedSolarBlocked` [%] · `wantedSolarRetained` [%] · `directSolarIntercepted` [kWh] ·
`evaluations` · `iterations` · `termination` · `recommendsNoShading`.

The score alone cannot distinguish a device that blocks a lot of unwanted sun from one that simply
uses no material, so the physical components are exposed alongside it — Stage 9 Case 3 has two
families within 0.2 % of each other on score with roughly half the material between them, and which
one is right is a judgement about the brief.

`objectiveScore = 0` is exactly "build nothing". A negative score means the device is worse than no
device; when that is the best available, `recommendsNoShading` is true and the geometry returned is
the least-bad candidate, shown so the recommendation can be checked.

`_optimise_ = false` runs the Stage 8 seeded candidate sweep instead (and then requires
`_shadingPotentialField_`, which seeds it). Both paths report the same metric names, computed with
the same objective, so the two are directly comparable.

---

## 9. Verification

`VerifyShading` is the ground truth: the device is built as real geometry and traced through the same
first-hit engine as everything else.

* `baselineDirectSolar` — direct solar admitted with the surroundings in place and **no** device.
  Only this can be credited to a device, so solar already blocked by a neighbouring building is never
  the device's to claim.
* `directSolarIntercepted`, `directShadingEfficiency` [%], `unwantedSolarBlocked` [%],
  `wantedSolarRetained` [%], with `admittedUnwantedSolar` / `admittedWantedSolar` as the denominators.
* `elementNames` / `elementGuids` / `elementEnergy` — per-element attribution, credited to the element
  the sun reaches **first**, so overlapping parts never double-count.
* `unattributedEnergy` — the residual. It should be zero; anything else means the baseline and the
  traced geometry disagree, and the node warns rather than folding it into a total.

**Zero denominators stay unavailable.** Every percentage is `NaN` when its denominator is zero — never
0 % or 100 % — in the result object, in the component output, and in the ratio-to-percentage
conversion between them.

---

## 10. Error and warning quality

Actionable, in the engineer's terms:

* `Please supply a valid SAM AnalyticalModel.`
* `No valid external sun-exposed apertures were found in this model.`
* `WeatherData is required. Supply WeatherData or attach it to the AnalyticalModel.`
* `Explicit HOYs override the connected AnalysisPeriod.`
* `The proposed shading spacing (0.1 m) is below the reliable analysis resolution (GridSize 0.25 m). Reduce GridSize and recalculate before trusting the element-level results.`
* `This aperture could not be prepared for shading analysis. Check that it is an external sun-exposed aperture of THIS model, that _gridSize_ matches the one used for the targets, and that the site location resolves to a time zone.`
* `_preset_ was not recognised. Use one of: Full Year, Summer, Winter, …`

Enum inputs accept the value, its name (case- and space-insensitive) or its index, and an
unrecognised one is an error naming the accepted values rather than a silent default.

---

## 11. Goo / parameter types

| type | why |
|---|---|
| `GooApertureSolarTarget` (+ `Param`) | previews the opening and its outward normal; casts to Brep/Mesh |
| `GooShadingPotentialField` (+ `Param`) | the red/blue/grey preview and its shared colour rule |
| `GooIdealShadingResult` (+ `Param`) | previews the display mesh; casts to Mesh |

Everything else — `IShadingTypology`, `OptimisedShadingResult`, `ShadingPerformance`, `AnalysisPeriod`
— travels as `GooSAMObject`, and results as `GooResult`, because SAM's existing wrappers already
handle them and a new class would only add serialization behaviour to maintain. All three new Goos
derive from `GooJSAMObject<T>`, so they save and reload through the standard SAM JSON path; the
round-trip is tested.

---

## 12. Automated tests

`Stage10ComponentLogicTests` — 16 tests. The components are deliberately thin (read wires → call one
method → write wires) precisely so the behaviour is testable outside Rhino, which cannot host a
Grasshopper component in a unit-test process.

| area | covered |
|---|---|
| hour precedence | HOYs override a period and report it; a period is used alone; an empty HOY list does not override; full year is the default |
| resolution warning | below the grid, near the grid, single element, coarse array, fins measured across vs louvres up |
| the brief | default is summer-unwanted / winter-wanted and flips in the southern hemisphere |
| context | default = every aperture; cell offsets address the shared space; reuse on identical inputs; `_recalculate_` forces a rebuild; a different grid rebuilds |
| invalidation | a different model never reuses another model's calculation |
| weather precedence | supplied wins; the model's own is used otherwise; no weather anywhere returns nothing rather than guessing |
| invalid input | an aperture outside the model cannot be prepared |
| optimisation metrics | a device rebuilt from the optimiser's parameters reproduces its numbers exactly — the RationaliseShading → VerifyShading contract |
| zero denominators | no wanted solar leaves `wantedSolarRetained` unavailable, through the reporting conversion |
| durability | target, field, ideal result and device all survive a save/reload round-trip |

**Not covered automatically** — parameter visibility, the viewport preview, Grasshopper's own casting
and the canvas experience. Those are the manual checklist.

---

## 13. Known limitations that matter to a user

Inherited from Stages 0–9 and unchanged: no inter-reflection; no perforated or translucent screens;
four device families only; single scalar objective (no Pareto front); local optimality on the search
lattice; device fineness capped by the analysis grid; diffuse solar is not part of shading
desirability; each aperture is optimised independently; hourly time resolution; `MaterialFraction` is
area only, with no thickness, weight, fixing or money.

Added by Stage 10 and worth knowing:

* the shading components build their context over the model's **whole** default aperture set, so the
  first run on a large model pays for every aperture even when studying one — the trade for making
  the calculation shareable with `ApertureIrradiance`;
* `_gridSize_` must be kept the same across the chain or the work is redone;
* one component call studies **one** aperture for the shading stages; use a graft to sweep several.

---

## 14. Manual test checklist

The automated tests cover the logic. These cover the canvas, and need 2–3 real projects — at least
one with a neighbouring building or a deep soffit.

### Test A — solar analysis only

`AnalyticalModel` → `ApertureSolarTargets` → `ApertureIrradiance`

- [ ] every expected window appears; `count` matches the model
- [ ] `azimuths` agree with the model's orientation; the preview arrow points OUT of the room on
      every target (an inward arrow means the model's aperture is wrong, not the analysis)
- [ ] internal-wall apertures are absent; asking for one by Guid produces a clear warning, not silence
- [ ] `_run = false` does nothing at all
- [ ] full year vs Summer vs Winter: totals change, and the second run reports
      `reusedPreviousCalculation = true`
- [ ] `_HOYs_` connected alongside `_analysisPeriod_` → the Remark about the override appears and the
      hour count matches the HOY list
- [ ] `_weatherData_` connected → those results differ from the model-weather run
- [ ] `_recalculate_ = true` → `reusedPreviousCalculation = false` and it takes as long as the first run
- [ ] south exceeds north (northern hemisphere); a UK vertical facade lands in the low hundreds of
      kWh/m² annually

### Test B — potential and ideal shape

`ApertureSolarTargets` → `ShadingPotentialField` → `IdealShadingShape`

- [ ] the map sits in FRONT of the glass, not behind it
- [ ] red concentrates where high summer sun arrives; blue appears where the winter sun would be lost
- [ ] swapping `_unwantedPeriod_` and `_wantedPeriod_` inverts the colours
- [ ] `_threshold_` 0.5 vs 0.9 vs 0.99 → the shape grows; `capturedFraction` tracks the request
- [ ] `_maxDepth_` and `_voxelSize_` behave (finer = slower, same story)
- [ ] a window with no summer sun problem reports "nothing here is worth shading" rather than failing
- [ ] `ShadingPotentialField` reuses the calculation `ApertureIrradiance` already paid for, when
      `_gridSize_` and `_sunAngleStep_` match

### Test C — buildable shading

`ShadingPotentialField` → `RationaliseShading` → `VerifyShading`

- [ ] all four families run: Overhang, HorizontalLouvres, VerticalFins, EggCrate
- [ ] leaving `_typologies_` empty gives a sensible ranked list
- [ ] the winning geometry looks buildable and its dimensions match `parameterValues`
- [ ] `VerifyShading` on the winner reproduces `RationaliseShading`'s benefit, harm and
      `directSolarIntercepted`
- [ ] `unwantedSolarBlocked` and `wantedSolarRetained` move in opposite directions as depth grows
- [ ] force a fine device (many louvres on a short window, or a coarse `_gridSize_`) → the resolution
      warning appears and names both numbers
- [ ] a window with no unwanted solar → `recommendsNoShading` and a score at or below zero
- [ ] `_optimise_ = false` returns a comparable, quicker answer

### Test D — context

An aperture partly shaded by another building or a deep soffit.

- [ ] `baselineDirectSolar` is visibly lower than an equivalent unobstructed window
- [ ] the optimiser does NOT buy material for solar the context already blocks — expect a minimal
      device and a small benefit
- [ ] `unattributedEnergy` is zero (a non-zero value is a real finding: report it)
