# Stage 10 implementation — the Grasshopper workflow

Companion to `Stages0-4-Method.md`, `Stages5-8-Method.md` and `Stage9-Method.md`. Those describe the
physics. This describes what an engineer sees on the canvas, what each input does, and which
behaviours are guaranteed.

Stage 10 adds **no physics**. Every number it shows is produced by the Stage 0–9 engine, unchanged.

> **Stage 10.1** (this revision) is the result of the first real manual test. It fixes a defect that
> made the whole shading chain unusable on any model with more than one window, separates "no shading
> is worth building" from "nothing could be measured", gives every device the identity of the window
> it was designed for, and rewrites the parts of the interface that were misread. §15 records what
> the manual test found and what changed.

---

## 1. The chain

The main line is five nodes. Read it left to right; it is the whole workflow.

```
AnalyticalModel
      │
      ▼
SAMAnalytical.ApertureSolarTargets        which windows, which way they face
      │
      ▼
SAMAnalytical.ShadingPotentialField       WHERE shading would help, and where it would harm
      │
      ▼
SAMAnalytical.RationaliseShading          WHAT to build
      │
      ▼
SAMAnalytical.VerifyShading               what it ACTUALLY achieves
```

Two branches hang off it. Neither belongs in the main line, and neither is a step you are skipping.

```
SAMAnalytical.ApertureSolarTargets
      └──► SAMAnalytical.ApertureIrradiance      "how much solar reaches this window as it is?"

SAMAnalytical.ShadingPotentialField
      └──► SAMAnalytical.IdealShadingShape       "what does the physics ideally want?"
```

* **ApertureIrradiance** is a diagnostic about the EXISTING window. It answers *how much solar
  arrives*. It is not an input to shading design and is never required by it.
* **IdealShadingShape** is the free-form shape the field would fill if buildability were no object.
  It answers *what the physics wants*. **RationaliseShading answers what can actually be built**, and
  it works from the field directly — it does not take the ideal shape as an input, and wiring one
  into the other would only make the canvas look linear at the cost of implying a dependency that
  does not exist.

`SAMAnalytical.AnalysisPeriod` feeds the hours into any of them.

Category **SAM**, sub-category **Solar**. All seven derive from
`GH_SAMVariableOutputParameterComponent` with **fixed** `ComponentGuid`s.

| component | Guid | version |
|---|---|---|
| `SAMAnalytical.AnalysisPeriod` | `7f3c9a10-…-1101` | 1.0.0 |
| `SAMAnalytical.ApertureSolarTargets` | `7f3c9a10-…-1102` | **1.0.1** |
| `SAMAnalytical.ApertureIrradiance` | `7f3c9a10-…-1103` | 1.0.0 |
| `SAMAnalytical.ShadingPotentialField` | `7f3c9a10-…-1104` | **1.0.1** |
| `SAMAnalytical.IdealShadingShape` | `7f3c9a10-…-1105` | 1.0.0 |
| `SAMAnalytical.RationaliseShading` | `7f3c9a10-…-1106` | **1.0.1** |
| `SAMAnalytical.VerifyShading` | `7f3c9a10-…-1107` | **1.0.1** |

A component placed before a version bump keeps the parameters stored in the file and shows an
advisory. Right-click → the version menu item replaces it with the current one. Guids never change,
so a script never loses its wires to an unknown component.

---

## 2. Several windows at once

This is the ordinary case, and it needs no expertise.

`ApertureSolarTargets` emits a **list** of targets. Every downstream Solar node takes **one** target.
Grasshopper therefore runs each node once per target and puts each aperture's results in its **own
branch**. You do not need to graft anything, and nothing cross-products: the model is a single item
and is replicated against the list.

What makes that safe rather than merely conventional:

* **a device carries the aperture it was designed for.** `RationaliseShading` emits a `ShadingDevice`,
  not a bare typology. `VerifyShading` compares that aperture against the target on its own wire and
  **refuses to measure** on a mismatch, naming both apertures. Verifying a south device on a north
  window would otherwise produce a number that is arithmetically correct and about the wrong design —
  the worst failure an engineering tool can have, because nothing looks wrong;
* **every result says which window it is about.** `apertureGuid` and `azimuth` are outputs of both
  `RationaliseShading` and `VerifyShading`;
* **ordering is deterministic.** Targets come out in model order and stay in it;
* **nothing is flattened silently.** A device with no identity at all — one built by hand rather than
  by `RationaliseShading` — is accepted, but says in a Remark that it is being taken on trust.

Reading ten windows at once: panel `status` and `designSummary` side by side.

```
270°  OK        Overhang | Depth 2.08 m | 40.4% unwanted blocked | 93.3% wanted retained | 130.3 kWh benefit
180°  OK        Overhang | Depth 1.16 m | 55.0% unwanted blocked | 90.5% wanted retained | 267.6 kWh benefit
 90°  OK        Overhang | Depth 2.27 m | 44.6% unwanted blocked | 91.8% wanted retained | 191.1 kWh benefit
  0°  OK        Overhang | Depth 0.24 m |  2.4% unwanted blocked | n/a wanted retained   |   1.1 kWh benefit
```

`status` is one of **OK**, **NO SHADE**, **WARNING**, **NOT EVALUATED**. Runtime warnings and errors
still appear on the component — the structured status is *additional*, so a batch stays readable
without losing the balloon that makes a problem impossible to miss.

---

## 3. Reading the potential field — red and blue

The single most misread thing in the workflow. The colour is **not** "how much sun is here". It is

> **the value of putting shading material at that location.**

| | meaning |
|---|---|
| **RED** | **SHADE HERE.** Material here intercepts solar you asked to block. Stronger red, bigger gain. Positive shading benefit. |
| **BLUE** | **KEEP OPEN.** Material here would intercept solar you asked to KEEP. Stronger blue, worse the loss. Negative benefit — jeopardy. |
| grey | Neither. Almost no beam passes through, so material here does nothing either way. Near-zero locations are omitted entirely rather than drawn grey. |

So the map is a set of **instructions**, not a heat map: *fill the red, stay out of the blue.*

The `legend` output says exactly this next to the geometry, with the two totals:

```
RED   Shade here — material here blocks unwanted solar (positive shading benefit)
BLUE  Keep open — material here would destroy wanted solar (negative benefit)
grey  Neither — almost no beam passes through, so material here does nothing

Positive potential total 412.6 kWh   Negative / jeopardy total -38.1 kWh
```

`_previewMode_` chooses how it is drawn — **Points** (default, cheap), **Voxels** (shaded cells,
easier to read as a volume), **None**. Display only: the numbers are identical whichever you pick.
The voxel mesh is built once and cached, and above 20 000 drawn cells it falls back to points rather
than making the viewport unusable. A preview failure has never been allowed to take the numerical
result down with it, and still is not.

---

## 4. The four numbers a design is judged on

`RationaliseShading` and `VerifyShading` both report many things. These four are the engineering
answer; everything else is supporting evidence.

| output | plain language |
|---|---|
| **Unwanted Solar Blocked [%]** | Of the summer (or whatever you called unwanted) beam this window would have let in, how much the device stops. Higher is better. |
| **Wanted Solar Retained [%]** | Of the winter (or whatever you called wanted) beam, how much still gets through. Higher is better. These two pull against each other; the design is the trade between them. |
| **Benefit [kWh]** | The unwanted solar blocked, as energy. What the device earns. |
| **Harm [kWh]** | The wanted solar destroyed, as energy — a positive loss. What the device costs in daylight and free winter heat. |

`objectiveScore` is **not** the headline. It folds benefit, harm and material together through
weightings the reader may not have chosen, and two designs a fraction of a percent apart in score can
be physically very different (Stage 9 Case 3: two families within 0.2 % on score with roughly half
the material between them). It remains available as a comparison aid.

`designSummary` puts the story on one line:

```
180° | Overhang | Depth 1.16 m, RiseAboveHead 0.12 m, ExtensionBeyondJambs 0.25 m
    | 55% unwanted blocked | 90.5% wanted retained | 267.6 kWh benefit | 32.4 kWh wanted solar lost
```

`verificationSummary` does the same for the measured result:

```
180° | Overhang | 1821.2 kWh baseline direct solar | 616.6 kWh intercepted
    | 59.8% unwanted blocked | 91.1% wanted retained | 33.9% direct shading efficiency
```

**Unavailable is not zero.** Where a percentage has no denominator — no unwanted solar in the brief,
no wanted solar, nothing admitted at all — it is `NaN` in the outputs and reads `n/a` in the
summaries. A London north facade genuinely has no wanted winter beam, and reporting "100 % wanted
solar retained" for it would be a fabrication.

---

## 5. wantedSolarPenalty and materialPenalty

Both are **dimensionless**, and neither changes a measured energy. They change which design the
search picks.

### `_wantedSolarPenalty_` — how much you care about keeping wanted solar

> Importance of preserving wanted solar relative to blocking unwanted solar.
> 1.0 = equal importance; >1 protects wanted solar more; <1 prioritises blocking unwanted solar.

It is an **exchange rate in kWh**:

| value | reading | effect |
|---|---|---|
| **1.0** *(default)* | Equal energy weighting. Losing 1 kWh of wanted solar costs exactly what gaining 1 kWh of blocked unwanted solar earns. | The neutral position. |
| **2.0** | Strongly protect wanted / winter solar. Losing 1 kWh of wanted solar now needs about **2 kWh** of unwanted solar blocked to justify it. | The search buys **shallower** devices. Use where winter gain or daylight matters. |
| **0.5** | Prioritise blocking unwanted solar. Losing 1 kWh of wanted solar costs only **0.5 kWh** in the objective. | The search buys **deeper** devices. Use where overheating dominates. |

On the potential field it moves where the map turns from red to blue.

### `_materialPenalty_` — how reluctant you are to buy more device

> How much device is too much: how reluctant the search is to buy extra shading area for a small
> further gain.

| value | effect |
|---|---|
| **0** | Size on energy alone. Tends to return the largest device that still helps at all. |
| **0.1** *(default)* | A mild preference for the leaner of two near-equal designs. |
| higher | Favours smaller devices. Use where buildability or cost matters more than the last few kWh. |

---

## 6. "No shading needed" is an answer, not a failure

The optimiser scores building nothing at exactly **zero**. When no candidate beats that, the honest
recommendation is to leave the window alone, and the workflow now says so as a **success**:

* `status` = **NO SHADE**, `recommendsNoShading` = true;
* `shadingDevice` carries the **null device**, so `VerifyShading` measures it like any other and
  reports the truth — 0 % of unwanted solar blocked, 100 % of wanted solar retained, and `NaN`
  wherever a denominator is genuinely absent. Nothing is fabricated and nothing errors;
* `shadingGeometry` is **empty**, because there is nothing to build;
* the design that lost moves to `bestCandidateDevice` — always supplied, clearly a **diagnostic**, so
  the recommendation can be checked rather than taken on trust. It is never handed out as "the
  device"; that is how a rejected design ends up built.

Distinct from it, and never dressed as it:

* `status` = **NOT EVALUATED**, termination `EvaluationFailed`. Nothing could be **measured**. This is
  a fault in the setup, and it reports as an error with what to check.

---

## 7. Precedence — nothing connected is ever discarded silently

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

## 8. Defaults, units and naming

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
| `_previewMode_` | Points | — |
| `_wantedSolarPenalty_` (λ) | 1.0 | dimensionless |
| `_materialPenalty_` (μ) | 0.1 | dimensionless |
| `_threshold_` | 0.9 (CumulativeCapture) | fraction |
| `_maximumEvaluations_` | 400 | candidates per family |
| `_recalculate_` / `_run` | false | — |

**GridSize is a distance; samplePointCount is a count.**

| | is | unit |
|---|---|---|
| `GridSize` | the **spacing between** analysis sample points | m |
| `samplePointCount` | the **number of** analysis sample locations on the opening | — |

Halving `GridSize` roughly quadruples `samplePointCount`, and the run slows in proportion. The output
was called `cellCounts` before Stage 10.1; "cell" is an internal word that meant nothing to the
engineers who tried the node. It is **not** `gridCount`, which reads as a count of grids. Scripts
placed before the rename keep working — the old output name is still honoured — but replacing the
component through the version menu is the tidy fix.

Energies are **kWh**, densities **kWh/m²**, areas **m²**, lengths **m**, angles and orientations **°**,
percentages **%**. Terminology is fixed: **GridSize** (not CellSize), **SunAngleStep** (not BinSize),
**Recalculate** (not RebuildCache). The word *cache* appears in no component name or description.

Every long calculation is gated by `_run`, and returns immediately while it is false.
`IdealShadingShape` has no `_run`: it is arithmetic over an existing map plus one iso-surface, and a
gate there would be an extra wire for nothing.

---

## 9. Reuse, recalculation and the shared cell space

The expensive part is the geometric pass — casting rays from every analysis sample at every sun
group. It is stored on the model's `SolarModel` and reused whenever the identity of the analysis
matches: context geometry, target geometry, grid size, sun-angle step, horizon cutoff, tolerances,
site latitude/longitude and time zone, the timeline offset, the year, and the sample count.

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

**The shared cell space.** To keep the calculation shareable, that factory flattens **every**
analysable aperture's sample points into one visibility result, and each aperture reads its own rows
out of it at its own offset. That is why `_gridSize_` must be kept the same across the chain — a
different grid is a different analysis, not a different view of the same one.

It is also where the Stage 10.1 defect lived. See §15.

---

## 10. Resolution warning

Stage 9 found that a device finer than the analysis grid produces numbers that look like a triumph:
eleven blades over a 1 m opening sampled at 0.5 m can sit so every sample is shaded and none of the
gaps are, reporting *100 % of unwanted solar blocked and 100 % of wanted solar retained* at once.

Stage 9's parameter cap keeps an **optimised** device at or above one grid spacing. Stage 10 adds the
reporting side, which also covers devices a user typed in by hand:

| element pitch | state | Grasshopper | `status` |
|---|---|---|---|
| ≤ `GridSize` | `BelowResolutionLimit` | **Warning**: *…below the reliable analysis resolution… Reduce GridSize and recalculate…* | WARNING |
| < 2 × `GridSize` | `NearResolutionLimit` | **Remark**: *…close to the solar-analysis grid resolution…* | WARNING |
| otherwise | `Resolved` | nothing | OK |

Pitch is `span / (count − 1)`, the spacing the typologies actually build to, measured **up** the
opening for louvres and **across** it for fins. A single element has no pitch. The rule itself is
Stage 9's and was not redesigned here.

---

## 11. Display geometry is not performance geometry

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

**Suggested preview convention** for showing the story — potential field → ideal intent → buildable
device → verified performance — in one viewport:

| geometry | suggested display |
|---|---|
| aperture / target | neutral outline plus the outward-normal arrow (the Goo draws this) |
| potential field | the red/blue map, Points or Voxels |
| ideal shape | translucent warm / orange — **intent**, never performance truth |
| selected buildable device | a distinct solid colour |

Keeping the ideal translucent and the device solid is the point: it stops the ideal mesh being read
as a result.

---

## 12. Verification

`VerifyShading` is the ground truth: the device is built as real geometry and traced through the same
first-hit engine as everything else.

* `baselineDirectSolar` — direct solar admitted with the surroundings in place and **no** device.
  Only this can be credited to a device, so solar already blocked by a neighbouring building is never
  the device's to claim.
* `directSolarIntercepted`, `directShadingEfficiency` [%], `unwantedSolarBlocked` [%],
  `wantedSolarRetained` [%], with `admittedUnwantedSolar` / `admittedWantedSolar` as the denominators.
* `elementNames` / `elementGuids` / `elementEnergy` — per-element attribution, credited to the element
  the sun reaches **first**, so overlapping parts never double-count.
* `unattributedEnergy` — the residual, reported rather than folded into a total.
* `verificationSummary`, `status`, `apertureGuid`, `azimuth` — the batch-readable form.

**Zero denominators stay unavailable.** Every percentage is `NaN` when its denominator is zero — never
0 % or 100 % — in the result object, in the component output, and in the ratio-to-percentage
conversion between them.

---

## 13. Error and warning quality

Actionable, in the engineer's terms:

* `Please supply a valid SAM AnalyticalModel.`
* `No valid external sun-exposed apertures were found in this model.`
* `WeatherData is required. Supply WeatherData or attach it to the AnalyticalModel.`
* `Explicit HOYs override the connected AnalysisPeriod.`
* `The proposed shading spacing (0.1 m) is below the reliable analysis resolution (GridSize 0.25 m). Reduce GridSize and recalculate before trusting the element-level results.`
* `This device was designed for aperture <a>, but it is being verified against aperture <b>. The result would be a correct measurement of the wrong design. Match the device to its own target…`
* `No candidate could be measured on this window: the aperture, the solar calculation and the candidate geometry do not describe the same analysis points. Check that _gridSize_ is the same value used for the targets and that the target came from THIS model.`
* `_preset_ was not recognised. Use one of: Full Year, Summer, Winter, …`

Enum inputs accept the value, its name (case- and space-insensitive) or its index, and an
unrecognised one is an error naming the accepted values rather than a silent default.

---

## 14. Goo / parameter types

| type | why |
|---|---|
| `GooApertureSolarTarget` (+ `Param`) | previews the opening and its outward normal; casts to Brep/Mesh |
| `GooShadingPotentialField` (+ `Param`) | the red/blue/grey preview, Points or Voxels, and the shared colour rule |
| `GooIdealShadingResult` (+ `Param`) | previews the display mesh; casts to Mesh |

Everything else — `ShadingDevice`, `IShadingTypology`, `OptimisedShadingResult`, `ShadingPerformance`,
`AnalysisPeriod` — travels as `GooSAMObject`, and results as `GooResult`, because SAM's existing
wrappers already handle them. All three Goos derive from `GooJSAMObject<T>`, so they save and reload
through the standard SAM JSON path; the round-trip is tested, including a device's aperture identity.

---

## 15. What the first real manual test found

The single-south-window workflow worked and was useful. Opened on a controlled model with **ten**
apertures across four cardinal orientations, it failed:

* the potential field looked plausible on every orientation;
* `RationaliseShading` produced device families and warnings;
* several orientations reported "no shading beats leaving the aperture unshaded";
* `VerifyShading` failed with *"The device could not be measured on this window."*, and downstream
  values became null/NaN.

### The mechanism

Not data trees, not pairing, not orientation — those were all ruled out by running each aperture
individually, which failed too. **The defect was the shared cell space of §9.**

`ApertureSolarContext` flattens every aperture's sample points into one visibility result. The
attribution side — the part that decides which element of a device the sun hits first — required the
sample points handed to it to be **the whole** of that result, and it was handed **one aperture's**.
On a one-window model those are the same thing, which is why every earlier test passed: every Stage
6–9 test scenario built its cache from a single target.

With ten windows they differ, so attribution came back null for **every** aperture, every candidate
scored `NaN` — and because `NaN > 0` is false, the optimiser read that as *"nothing beats leaving the
window alone"*. The "no shading recommended" results were not physics. They were an unmeasured run
wearing the costume of an answer.

### What changed

1. **The cell dimension is a window, not a mirror.** Attribution covers one aperture's samples,
   indexed locally, starting at that aperture's offset in the shared space. The one place the two are
   paired now reads the baseline at `offset + c` and the attribution at `c`. Reading both at the same
   index — the old behaviour — scored an aperture against another aperture's admitted beam.
2. **A fault cannot wear the costume of an answer.** `EvaluationFailed` is distinct from
   `NoBeneficialCandidate` (§6).
3. **Devices carry their aperture** (§2).
4. **Context is not re-traced per candidate** (§16).

### Verified

Every one of the ten apertures now produces a field, a measured device and a verified result. One
aperture measured as one of ten, against a 150-sample shared calculation, gives numbers **identical
to nine decimal places** to that aperture measured alone against its own 10–20-sample calculation, on
all four orientations. That equivalence — batch equals individual — is what identifies the defect as
the cell space rather than anything on the canvas, and it is now a test.

The controlled model is kept as `Fixtures/MultiAzimuth.sam` (125 KB) and its per-orientation answers
are asserted on every run.

---

## 16. Performance at project scale

Measured on a real project of roughly 1 600 spaces, 8 800 panels and 2 200 apertures — of which 946
are external and sun-exposed, giving **12 540 analysis sample points** at `GridSize` 0.5 m and 683
sun groups at `SunAngleStep` 2°.

| operation | time | notes |
|---|---|---|
| load model | 1.3 s | |
| `ApertureSolarTargets`, all apertures | 0.4 s | 946 targets |
| **first** shading setup for one aperture | 12.8 s | builds the shared calculation over all 12 540 samples |
| `ShadingPotentialField` for that aperture | 0.01 s | 6 500 map locations |
| the same aperture again (reuse) | 0.9 s | no ray casting |
| change the brief only | 0.9 s | no ray casting — periods are arithmetic |
| change `GridSize` (forced rebuild) | 9.8 s | a different grid is a different analysis |
| `RationaliseShading`, one family | 0.35 s | 87 candidates |
| `RationaliseShading`, all eligible families | 1.3 s | 4 families |
| `VerifyShading` | 0.003 s | |

Whole workflow for one aperture on that project: **about 15 seconds cold, under 3 seconds warm.**

### What made it that fast

The first-run cost is honest and is the price of the shared calculation: analysing one window builds
visibility for all of them, and every subsequent window is then nearly free. The **rationalisation**
cost was not honest, and Stage 10.1 removed it.

Each attribution build projects every occluder onto a plane per sun group, and the optimiser
evaluates dozens of candidates — so the whole building was being re-projected 87 times to size one
overhang.

| on that project, one aperture | before | after |
|---|---|---|
| `RationaliseShading`, one family | **300.9 s** | **0.35 s** |
| `RationaliseShading`, all four families | **851.8 s** (14 min) | **1.3 s** |
| `VerifyShading` | **3.5 s** | **0.003 s** |

**No context was removed to get this.** The building still shades the window. Attribution is only
ever consulted where the base visibility calculation says the sample is **lit**, and "lit" means the
same primitive, with the same tolerances and the same ray-start offset, has already established that
no context face is on that ray. For those samples the first thing the sun meets is a candidate
element or nothing — the context faces cannot be first, because they are not on the ray at all.
Tracing them again could only reproduce an answer already paid for.

The argument is not left to stand on its own: the two routes are asserted equal to nine decimal
places across four device families, three orientations and every per-element credit, and on the real
project the optimiser returns the identical winning design, the identical score and the identical
evaluation count either way.

Storage, same project: the visibility result is ~1 MB (683 × 12 540 bits); a candidate's attribution
is ~42 KB (683 × 16 samples × 4 bytes). Whole-model attribution per candidate would have been ~32 MB.

---

## 17. Automated tests

`Stage10ComponentLogicTests` (16), `MultiApertureShadingTests` (13), `MultiAzimuthModelTests` (5).
The components are deliberately thin (read wires → call one method → write wires) precisely so the
behaviour is testable outside Rhino, which cannot host a Grasshopper component in a unit-test process.

| area | covered |
|---|---|
| hour precedence | HOYs override a period and report it; a period is used alone; an empty HOY list does not override; full year is the default |
| resolution warning | below the grid, near the grid, single element, coarse array, fins across vs louvres up |
| the brief | default is summer-unwanted / winter-wanted and flips in the southern hemisphere |
| context | default = every aperture; offsets address the shared space; reuse; `_recalculate_`; a different grid rebuilds |
| invalidation | a different model never reuses another model's calculation |
| weather precedence | supplied wins; the model's own otherwise; no weather returns nothing rather than guessing |
| **shared cell space** | attribution is a window into it; every aperture of many measures exactly as it would alone; reading at the wrong offset is provably a different answer |
| **candidate-only attribution** | identical to whole-model attribution, per family, per orientation, per element |
| **no-shading semantics** | an unmeasurable run is `EvaluationFailed` and recommends nothing; the null device measures as 0 % / 100 %; a percentage with no denominator stays unavailable even for it |
| **device identity** | a device carries its aperture through a save/reload; a device from one orientation is detectably not another's |
| **the controlled model** | ten targets across four orientations, all with valid outward frames; a field, a measured device and a verified result for every one; same orientation → same design, different orientation → different design |
| optimisation metrics | a device rebuilt from the optimiser's parameters reproduces its numbers exactly |
| zero denominators | no wanted solar leaves `wantedSolarRetained` unavailable, through the reporting conversion |
| durability | target, field, ideal result, device and shading device all survive a save/reload |

**Not covered automatically** — parameter visibility, the viewport preview, Grasshopper's own casting
and the canvas experience. Those are the manual checklist.

---

## 18. Known limitations that matter to a user

Inherited from Stages 0–9 and unchanged: no inter-reflection; no perforated or translucent screens;
four device families only; single scalar objective (no Pareto front); local optimality on the search
lattice; device fineness capped by the analysis grid; diffuse solar is not part of shading
desirability; each aperture is optimised independently — **a device here does not shade its
neighbour**; hourly time resolution; `MaterialFraction` is area only, with no thickness, weight,
fixing or money.

Added by Stage 10 and worth knowing:

* the shading components build their context over the model's **whole** default aperture set, so the
  first run on a large model pays for every aperture even when studying one — the trade for making
  the calculation shareable with `ApertureIrradiance`, and measured in §16 at about 13 s on a
  2 200-aperture project;
* `_gridSize_` must be kept the same across the chain or the work is redone;
* one component call studies **one** aperture; a list of targets is handled by Grasshopper's own
  per-item iteration and needs no grafting (§2);
* the ideal shape's mesh is display geometry, not performance truth (§11).

---

## 19. Manual test checklist

The automated tests cover the logic. These cover the canvas, and need 2–3 real projects — at least
one with a neighbouring building or a deep soffit, and at least one with **many windows**.

### Test A — solar analysis only

`AnalyticalModel` → `ApertureSolarTargets` → `ApertureIrradiance`

- [ ] every expected window appears; `count` matches the model
- [ ] `azimuths` agree with the model's orientation; the preview arrow points OUT of the room on
      every target (an inward arrow means the model's aperture is wrong, not the analysis)
- [ ] `samplePointCounts` is a COUNT and roughly quadruples when `_gridSize_` is halved
- [ ] internal-wall apertures are absent; asking for one by Guid produces a clear warning, not silence
- [ ] `_run = false` does nothing at all
- [ ] full year vs Summer vs Winter: totals change, and the second run reports
      `reusedPreviousCalculation = true`
- [ ] `_HOYs_` connected alongside `_analysisPeriod_` → the Remark about the override appears
- [ ] `_weatherData_` connected → those results differ from the model-weather run
- [ ] `_recalculate_ = true` → `reusedPreviousCalculation = false` and it takes as long as the first run
- [ ] south exceeds north (northern hemisphere); a UK vertical facade lands in the low hundreds of
      kWh/m² annually

### Test B — potential and ideal shape

`ApertureSolarTargets` → `ShadingPotentialField` → `IdealShadingShape`

- [ ] the map sits in FRONT of the glass, not behind it
- [ ] **red concentrates where shading material would earn its keep**, blue where it would destroy
      wanted solar — read the `legend` output next to it
- [ ] swapping `_unwantedPeriod_` and `_wantedPeriod_` inverts the colours
- [ ] `_previewMode_` Points / Voxels / None: the map looks different, the numbers do not change
- [ ] `_wantedSolarPenalty_` 0.5 vs 1.0 vs 2.0 moves the red/blue boundary
- [ ] `_threshold_` 0.5 vs 0.9 vs 0.99 → the shape grows; `capturedFraction` tracks the request
- [ ] a window with no summer sun problem reports "nothing here is worth shading" rather than failing
- [ ] `ShadingPotentialField` reuses the calculation `ApertureIrradiance` already paid for

### Test C — buildable shading

`ShadingPotentialField` → `RationaliseShading` → `VerifyShading`

- [ ] all four families run: Overhang, HorizontalLouvres, VerticalFins, EggCrate
- [ ] `designSummary` reads as a sentence an engineer would write; `status` is OK
- [ ] the winning geometry looks buildable and its dimensions match `parameterValues`
- [ ] `VerifyShading` on the winner reproduces `RationaliseShading`'s benefit, harm and
      `directSolarIntercepted`
- [ ] `unwantedSolarBlocked` and `wantedSolarRetained` move in opposite directions as depth grows
- [ ] `_wantedSolarPenalty_` 2.0 gives a shallower device than 0.5 on the same window
- [ ] force a fine device → the resolution warning appears, names both numbers, and `status` is WARNING
- [ ] a window with no unwanted solar → `status` NO SHADE, `shadingGeometry` empty, and
      `VerifyShading` on `shadingDevice` still SUCCEEDS, reporting 0 % blocked / 100 % retained
- [ ] `bestCandidateDevice` shows what the search would have built in that case
- [ ] `_optimise_ = false` returns a comparable, quicker answer

### Test D — many windows *(new in Stage 10.1)*

`ApertureSolarTargets` (all windows, no grafting) → the whole chain.

- [ ] every aperture produces a result; none reports NOT EVALUATED
- [ ] `status` / `apertureGuid` / `azimuth` panelled together read as a table
- [ ] windows of the same orientation reach the same design; different orientations do not
- [ ] running ONE of those windows alone gives the same numbers as running all of them
- [ ] deliberately cross-wire a device from one window onto another window's target →
      **VerifyShading refuses with an error naming both apertures**, rather than reporting a number

### Test E — context

An aperture partly shaded by another building or a deep soffit.

- [ ] `baselineDirectSolar` is visibly lower than an equivalent unobstructed window
- [ ] the optimiser does NOT buy material for solar the context already blocks — expect a minimal
      device and a small benefit
- [ ] `unattributedEnergy` is zero (a non-zero value is a real finding: report it)

### Test F — a real project

- [ ] the first shading run on a large model takes tens of seconds, not minutes, and says
      `reusedPreviousCalculation = false`
- [ ] the second window is nearly free
- [ ] comparing all four families on one window takes seconds, not minutes
