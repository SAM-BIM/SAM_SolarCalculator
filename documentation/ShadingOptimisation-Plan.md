# SAM_SolarCalculator — Aperture Shading Optimisation

**Staged implementation plan — SAM-native C#, no Ladybug Tools dependency**

---

## 0. Answer up front: is this possible?

**Yes, and more of it already exists than the previous plan assumed.**

SAM_SolarCalculator already contains a working **projective direct-beam shading engine**. It is
not a raytracer and it is not a sky-dome sampler — for each timestep it projects every model face
onto a plane perpendicular to the sun vector, clips the 2D polygons against each other
(NetTopologySuite), and returns the lit fragment of every surface. That is exactly the primitive
needed to build shading-mask generation, and it is *better suited* to this problem than the
Ladybug/Radiance cumulative-sky route the earlier plan proposed.

The previous plan's central recommendation — precompute a Radiance daylight-coefficient matrix so
any analysis period becomes a re-weighting — was the right *architectural instinct* applied to the
wrong engine. SAM does not need `rfluxmtx`/`rcontrib`. The SAM-native equivalent is simpler, has no
external binary dependency, and is described in **Stage 2** below.

**What must be built new** (in order of difficulty, all C#, all in this repo):

| # | Missing piece | Why nothing off-the-shelf gives it |
|---|---|---|
| 1 | Sun-grouped visibility calculation | Decouples geometry cost from period selection. No tool exposes this per-aperture. |
| 2 | Per-aperture (not per-panel) analysis target | `Simulate` currently drops apertures entirely; only `Simulate_Coverage` includes them. |
| 3 | Shading **potential field** (voxel scalar field) | Shaderade/`LB Shade Benefit` *evaluate* a supplied surface. Nothing *generates* the solid. |
| 4 | Ideal-shape → buildable-device rationaliser | No open-source tool fits overhang/fin/louvre/egg-crate to a target mask with a scored trade-off. |
| 5 | Anisotropic (Perez) sky | Current model is isotropic — see §2.3. Material for vertical façades. |
| 6 | Per-element interception attribution + energy-weighted shading metrics | Implemented as a separate `SolarAttributionCache` alongside the shared visibility bitset — see §2.5. |

**What is genuinely missing and cannot be fully solved in Phase 1:** a thermal load model. Shaderade's
definition of "unwanted sun" is *transmitted beam energy during hours when the zone has a net cooling
load*. SAM_SolarCalculator has no load model. Phase 1 must approximate this with a season/temperature
/irradiance filter (Stage 5), which is defensible and documented, but it is an approximation. The
honest upgrade path is to read loads back from the existing TAS link in Phase 2.

---

## 1. What is already in the repo (verified against source, not assumed)

Everything below was read from the current `master`. The previous plan flagged several of these as
"inferred from convention, confirm before committing" — they are now confirmed.

### 1.1 The solar engine

`SAM.Weather.SolarCalculator.Modify.Simulate(SolarModel, Dictionary<DateTime, Vector3D>, …)`
— `SAM.Weather.SolarCalculator/Modify/Simulate.cs`

- Sun vectors from `SAM.Geometry.SolarCalculator.Query.SunDirection(Location, DateTime, bool)`
  (NOAA, via the `SolarCalculator` 3.5.0 NuGet package). The vector points **sun → surface**, so
  `sunDirection.Z > 0` means the sun is below the horizon.
- Per-timestep occlusion, two modes:
  - **exact** — `Geometry.Object.Spatial.Query.VisibleLinkedFace3Ds(...)`, then plane-project and
    2D-clip each visible face back onto the contributing source faces.
  - **sampled** (`sampleSize > tolerance`) — `SampleCells(...)` builds a grid of `SampleCell` over
    every face, projects onto the sun-perpendicular `SunPlane(...)`, indexes the projected faces in
    an `STRtree`, and ray-tests each cell with `Query.IntersectionTuples(segment3D, candidates, …)`.
    A cell is lit only if its ray first hits its **own** merged face *and* that face is front-facing
    (`ProjectedFace.IsSolarCandidate`).
- Already parallel: `Parallel.For` over timesteps.
- `minHorizonAngle` culls near-horizon sun (default `Core.Tolerance.Angle` = **2°**, so near-horizon
  hours are skipped whole); the legacy `_timeShift_` input defaults to −30 min to match TAS EDSL.
  **See §2.4.1** — that −30 min is a TAS-compatibility convention, not the EPW weather convention,
  and the Stage 0–4 workflow defaults to +30 min instead.

**This is the reusable core.** Stages 2 and 6 are built by restructuring it, not replacing it.

### 1.2 Result and model types

- `SolarModel : SAMModel` — `Location` + `SolarRelationCluster` (GUID-keyed objects + relations),
  JSON round-trips. Attached to an `AnalyticalModel` under `AnalyticalModelParameter.SolarModel`.
- `LinkedFace3D` — GUID + `Face3D` + optional `Reference` string (used to back-reference a pane
  surface to its owning aperture).
- `SolarFaceSimulationResult` — per-timestep lit polygons + `Radiation`.
- `SolarCoverageSimulationResult` — per-timestep lit-area / total-area ratio. Lighter; this is the
  TAS-comparable format.
- `ISolarSimulationResult` / `ISolarObject` in `SAM.Core.SolarCalculator`.

### 1.3 Aperture handling — partial, and asymmetric

`Convert.ToSAM_SolarModel(AnalyticalModel, bool includeApertures)`:

- `includeApertures = false` (the **default**, used by the regular `Simulate` path) → **panels only,
  apertures are dropped**.
- `includeApertures = true` (coverage path only) → each aperture contributes **two** surfaces:
  the opening (`aperture.GetExternalEdge3D()`, keyed by `aperture.Guid`) and the pane(s)
  (`aperture.GetPaneFace3Ds()`, fresh GUIDs with `Reference = aperture.Guid`). Results are routed
  back as `"<name> -frame"` / `"<name> -pane"` in `Analytical.SolarCalculator.Modify.Simulate_Coverage`.

**Consequence for this project:** aperture-level irradiance is only reachable today through the
coverage path, and coverage is a *ratio*, not energy. Stage 0 fixes this.

### 1.4 Radiation model

`SAM.Weather.SolarCalculator.Create.Radiation(WeatherData, DateTime, Plane, …)` →
`SAM.Geometry.SolarCalculator.Create.Radiation(SolarTimes, tilt, azimuth, DNI, DHI, GHI, …)`

```csharp
directNormalRadiance     = DNI * max(0, cosThetaI);
diffuseHorizontalRadiance = DHI * skyViewFactor  * cos²(tilt/2);
globalHorizontalRadiance  = GHI * albedo * groundViewFactor * sin²(tilt/2);
```

This is the **isotropic (Liu–Jordan) sky**. See §2.3 — it is the single biggest accuracy limitation
for this workflow and it is cheap to fix.

### 1.5 Geometry primitives available

From `SAM.Geometry.Spatial` (SAM core repo):

- `Shell` with **boolean `Union` / `Difference` / `Cut`** (`Query/Union.cs`, `Query/Difference.cs`,
  `Query/Cut.cs`) — polygon-soup based, tolerance-sensitive.
- `Face3D` `Union` / `Difference` / `Intersection` (planar, NTS-backed — robust).
- `Extrusion`, `Mesh3D`, `Plane.Project(...)`, `BoundingBox3D`, `Transform3D`, `Rectangle3D`.

**Design decision:** Stage 6 deliberately does **not** use `Shell` booleans to build the ideal
shading solid. See §2.2.

### 1.6 Grasshopper component convention

`GH_SAMVariableOutputParameterComponent`, with `Inputs`/`Outputs` returning `GH_SAMParam[]`,
`ParamVisibility.Binding` vs `.Voluntary`, a frozen `ComponentGuid`, and a bumped
`LatestComponentVersion` on every signature change. Category `"SAM"`, sub-category `"Solar"`.
Follow `SAMAnalyticalSolarSimulation.cs` exactly.

### 1.7 Tests

xUnit, **.NET 8**, `SAM_SolarCalculator.Tests`, driven by real `.sam` fixtures in
`Tests/Fixtures/`. Runs in ~1 s. The SAM-vs-TAS coverage benchmark lives here and is the
regression gate.

### 1.8 Pre-existing defects found while reading (fix opportunistically)

| Location | Issue |
|---|---|
| `Weather.SolarCalculator/Modify/Simulate.cs` ~L55–102 | The `merge == true` branch builds a `result` list of merged results, adds them to the model, then **`return solarFaceSimulationResults;`** — the un-merged list. `result` is a dead store; callers asking for merged results get un-merged ones. |
| `Weather.SolarCalculator/Create/Radiation.cs` L31 | `System.Convert.ToInt32(Core.Query.Double(uTC))` truncates fractional time zones (UTC+5:30 → 5 or 6). Wrong sun position by up to 30 min for India, Nepal, parts of Australia. |
| `Weather.SolarCalculator/Modify/Simulate.cs` | `ComputeSunExposure` duplicates the whole body of `Simulate` and `Simulate_Sampled`. Three near-identical copies of the occlusion loop. Stage 2 collapses them. |
| `Geometry.SolarCalculator/Query/SunExposureFace3Ds.cs` L31 | `plane` may still be `null` after the loop; `plane.Coplanar(...)` then throws `NullReferenceException` instead of returning null. |
| Throughout | `calctulateRadiation` — misspelled parameter, public API. Fix behind an overload, do not rename in place. |

---

## 2. Architecture — the three decisions that shape everything

### 2.1 Decision: bin the sun, not the sky

The problem to solve is: *the user wants to switch between full year / a season / one day / a 24 h
window and see the answer immediately.* Recomputing occlusion per period is unacceptable — that is
the whole point of the previous plan's daylight-coefficient proposal.

But SAM's engine is a **direct-beam geometric** engine. The correct "compute once, re-weight cheaply"
decomposition for a direct-beam engine is **not** a Tregenza/Reinhart sky-patch matrix. It is a
**sun-group visibility cache**:

> Geometry (expensive) depends only on the **sun direction**.
> Radiation (cheap) depends on the **hour**.

Over a year the sun occupies a narrow 2-D band of the sky. Group sun positions at a step of, say,
2° in (altitude, azimuth). Roughly **600–900 groups** cover all daylight hours at a mid-latitude
site, against ~4 400 daylight hours. Then:

1. **Build once** (per geometry): for each sun group `g`, run the existing occlusion pass and store
   `firstHit[p, g]` over the analysis grid points of every selected aperture — the index of the
   element that intercepted the sun, or a sentinel for "visible" (§2.5).
2. **Evaluate any period** `H` (a set of HOYs) with pure arithmetic, no geometry:

```
E(cell) = Σ            [ lit(cell, bin(h)) · DNI(h) · cosθ(h)      ← direct beam
         h ∈ H
                       + SVF(cell) · D(h)                          ← diffuse sky
                       + GVF(cell) · ρ · GHI(h) ] · Δt             ← ground reflected
```

`SVF`/`GVF` (sky/ground view factors per cell, accounting for context obstruction) are computed
**once** by running the same occlusion pass against ~145 Tregenza sky-patch directions.

**Cost:** ~750 geometric passes instead of 4 400 for the year — and, far more importantly, **zero**
geometric passes when the user changes the period. Switching from "full year" to "June 21, 09:00–17:00"
becomes a sum over 9 numbers per cell. That is the interactive behaviour the brief asks for.

**Storage:** 20 apertures × 200 analysis points × 900 sun-angle steps ≈ 3.6 M bits ≈ 450 KB. Negligible.

**Accuracy:** the angular resolution is a user parameter (`SunAngleStep`, default 2° — see §2.4).
2° introduces a sub-degree sun-position error, well below the model's other approximations; 1°
doubles the build cost and is available for final runs. This must be validated against exact
per-hour simulation (Stage 11).

### 2.2 Decision: the ideal shading shape is a scalar field, not a boolean solid

The brief describes: *project the unwanted sun vectors from the window and subtract the swept volumes
to leave the ideal shade mass.* Geometrically correct, but building it with `Shell` booleans is the
wrong implementation:

- SAM's `Shell` booleans are polygon-soup, tolerance-sensitive, and fail on near-coplanar faces —
  and hundreds of swept volumes per aperture is precisely the worst case.
- A boolean gives a **binary** answer. What you actually want is a **graded** one: *how much is this
  bit of space worth?* — because the next step (rationalisation) needs a score to optimise against,
  and the user needs a threshold slider to trade material against performance.
- The graded formulation is the published method (Kaftan & Marsh 2005; Sargent, Niemasz & Reinhart
  2011). Both discretise into cells and score each cell. Neither uses solid booleans.

So: discretise the shading volume in front of each aperture into **voxels**, and score each voxel:

```
Benefit(v) =  Σ    Σ         w_i · Desirability(b)
              b   i : ray(a_i, b) passes through v
```

- `a_i` — an aperture analysis grid point, area weight `w_i`
- `g` — a sun group that lights `a_i` (straight out of the Stage 2 visibility calculation)
- `Desirability(g) = Σ_{h : group(h)=g} weight(h) · DNI(h) · cosθ(h) · Δt`,
  where `weight(h) > 0` means "blocking this hour is good" (overheating) and `weight(h) < 0` means
  "blocking this hour is bad" (wanted winter gain).

`Benefit > 0` → worth filling with material. `Benefit < 0` → must stay open.
The **ideal solid** is the isosurface `{ v : Benefit(v) > τ }`, and sweeping `τ` traces the
performance-vs-material trade-off curve directly.

The ray-marching pass that produces this field is the *same* pass that builds the Stage 2 cache —
they are computed together in one traversal.

`Shell`/`Mesh3D` are used **only** at Stage 7, to extract a viewable surface from the field. Nothing
downstream depends on a boolean succeeding.

### 2.3 Decision: fix the sky model before trusting the weighting

The isotropic sky (§1.4) systematically misestimates a vertical façade. Under clear skies most
diffuse arrives from the circumsolar region and the horizon band, neither of which an isotropic model
represents. For a vertical surface the error is typically **10–25 %** of the diffuse component, and
diffuse is a large fraction of annual irradiance on a UK/N-European façade.

This matters here specifically because Stage 5 decides which hours are "unwanted" partly on total
incident irradiance. A biased diffuse term biases the desirability field, which biases the generated
shape. **Perez 1990 anisotropic** is ~80 lines, needs no new inputs beyond what `WeatherHour` already
provides, and is independently testable. Do it early (Stage 3) rather than discovering the bias at
validation.

### 2.4 Decision: engineer-facing naming, implementation detail stays internal

Agreed on PR #12. The user-facing vocabulary is SAM/engineering language; the architecture of §2.1
is an implementation detail and must not leak into the Grasshopper surface.

| Use | Not | Why |
|---|---|---|
| `GridSize` | `CellSize` | Matches the existing `_gridSize_` in SAM's space-level `Simulate`. Engineers already know it. |
| `SunAngleStep` | `BinSize` / `binSizeDegrees` | Says what it controls — angular resolution of sun grouping — rather than naming the data structure. |
| `Recalculate` | `RebuildCache` | An action the engineer wants, not a mechanism they should have to reason about. |

**"Cache" is an internal word.** The type may stay `SolarVisibilityCache` — it is accurate and it is
not user-facing — but no Grasshopper input, output or description may use it, except where a
diagnostic genuinely needs it. Reuse and invalidation are **automatic**, driven by the geometry hash;
`_recalculate_` is a manual override for when the engineer wants to force the issue, not the normal
route.

**Standard input set** for the solar-analysis components (Stage 10 implements this verbatim):

| Input | Visibility | Default / empty behaviour |
|---|---|---|
| `_analyticalModel` | Binding | required |
| `_weatherData_` | Voluntary | supplied `WeatherData` wins; otherwise the one already on the `AnalyticalModel` |
| `_apertures_` | Voluntary | empty → **all** valid external sun-exposed apertures |
| `_analysisPeriod_` | Voluntary | empty → Full Year |
| `_HOYs_` | Voluntary (advanced) | explicit HOYs **override** `_analysisPeriod_` |
| `_gridSize_` | **Binding** | 0.5 m |
| `_sunAngleStep_` | Voluntary (advanced) | 2° |
| `_recalculate_` | Voluntary | false — reuse/invalidate automatically |
| `_run` | Binding | false |

Two consequences worth naming, because they are behaviour and not just wording:

- **`_weatherData_` fallback.** Every stage that touches irradiance needs weather. Resolve it once,
  in a shared helper, so the precedence rule (input → model → error) is identical everywhere. Do not
  reimplement it per component.
- **`_HOYs_` beats `_analysisPeriod_`.** When both are supplied the component must say so via
  `AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, …)` rather than silently discarding the period —
  a silently ignored input is how someone reports the wrong season for a year.

**Status after Stages 0–4.** The public Stage 0–4 API already speaks this vocabulary, so Stage 10
passes its inputs straight through rather than translating: `Modify.SimulateApertures` and
`Create.ApertureSolarTargets` take `gridSize` / `sunAngleStep` / `recalculate` and report
`reusedPreviousCalculation`; `ApertureIrradianceResult` exposes `GridSize`, `SunAngleStep`,
`TimeShiftInMinutes` and `SunTimeConvention`. The weather precedence rule is implemented **in
`SimulateApertures` itself** (supplied → model → null), so it cannot diverge between components.
`AnalysisPeriod(year, IEnumerable<int> hoursOfYear)` plus `ExplicitHoursOfYear` already models the
`_HOYs_` override; only the runtime remark remains for Stage 10.

### 2.4.1 Implementation record — where Stages 0–4 diverged from this plan

Recorded so the plan is not read as a description of the code. The full method reference is
`documentation/Stages0-4-Method.md`.

| Planned here | As built | Why |
|---|---|---|
| `SunGroup`, `Create.SunGroups`, `Query.AnalysisGrid` as **type** names | `SunBin`, `Create.SunBins`, `Query.AnalysisCells` | The §2.4 decision governs the **engineer-facing** surface, and it is honoured there. Renaming the internal types as well was judged not worth the churn once the public parameters, results and diagnostics all read `gridSize` / `sunAngleStep`. The mapping is fixed and documented; "bin" and "cell" stay out of the Grasshopper surface. |
| Stage 2 stores `firstHit[gridIndex, groupIndex]` as `ushort[]` | Stage 2 stores a packed **boolean** lit bitset (`ulong[][]`) | First-hit attribution is a Stage 8 requirement and Stages 0–4 do not consume it. Storing it now would have widened the cache and its serialised form for no current reader. §10 of `Stages0-4-Method.md` records exactly what the change costs (`IntersectionTuples(..., sort: true)` plus `bool[]` → `int[]`) and confirms nothing is discarded that would be expensive to recover. |
| `Create.SunGroups(Location, IEnumerable<DateTime>, double sunAngleStep)` | same shape, plus a `minHorizonAngle` and an explicit `timeShiftInMinutes` | The sun-sampling offset had to become part of the group definition and of the cache identity (correction B1), otherwise group construction and evaluation can be built on different timelines. |
| — | `SkyVisibilityCache` (Stage 3) as a **second**, separate cache | Perez needs three obstruction states (sky, horizon band, ground), not one. They are location- and weather-independent, so they invalidate on a different rule than the sun-group cache and belong in their own identity. |

Two statements elsewhere in this plan are superseded by the implementation and should be read with
§2 of `Stages0-4-Method.md`:

- §1.1's "`_timeShift_` defaults to −30 min to match TAS EDSL" describes the **legacy** Grasshopper
  input. The new workflow's default is `SunTimeConvention.IntervalStart` (**+30 min**), which is the
  SAM/EPW weather timeline; −30 min (`IntervalEnd`) is retained explicitly as the TAS EDSL
  compatibility case. These are one hour apart and must not be conflated.
- §1.4's radiation formula remains an accurate description of the **legacy** overload, which is
  frozen for compatibility. The corrected physical path is the new `Plane`/outward-normal overload.

### 2.4.2 Implementation record — where Stages 5–8 diverged from this plan

Recorded so the plan is not read as a description of the code. The full method reference is
`documentation/Stages5-8-Method.md`.

| Planned here | As built | Why |
|---|---|---|
| Stage 7 iso-surface by **marching cubes** | **Marching tetrahedra** | A cube has 256 sign configurations reducing to 15 base cases plus genuinely ambiguous ones (a face with two diagonally opposite corners inside can be joined either way), needing an extended table or an asymptotic decider; getting it wrong punches holes in the surface. A tetrahedron has 16 configurations, all unambiguous. The cost is roughly twice the triangles, which is irrelevant because the mesh is a display and take-off artefact and the scalar field remains the source of truth. |
| Stage 6 Case B: a vertical fin must beat an overhang on an east window | The test measures **lateral asymmetry** against a symmetric south-facing control | It does not, and forcing it would have been wrong. Most summer beam on an east facade arrives near normal incidence at 30–40° elevation, which is overhang territory; a fin only helps where `cos(incidence)` is already small. Measured 553.5 kWh fin vs 4231.1 kWh overhang, and the overhang wins per voxel too (0.74 vs 1.57 kWh/voxel), so it is not a region-size artefact. |
| Stage 6 profile-angle zero crossing within **~15 %** of the closed form | A **bracket** between two independently derived bounds, plus a grid-refinement convergence test | The gate compared a **continuum** quantity against a **cell-sampled** one. The field marches from analysis-cell centres, so just above the head its lever arm is 0.151 m against the closed form's 0.026 m — 6× in limiting profile angle. Continuous bound 0.125 m, cell-sampled bound 0.475 m, field 0.325 m, and the error halves as the grid halves. |
| Stage 8 fit score `Capture + λ·Harm − μ·MaterialFraction` | `UnwantedSolarIntercepted − wantedSolarPenalty × WantedSolarBlocked − materialPenalty × MaterialFraction × AdmittedUnwantedEnergy` | `Harm` is a positive penalty quantity, so **adding** it would reward a device for destroying wanted winter solar. All three terms are positive and both costs are subtracted. Names now state what quantities are rather than which way they point. |
| Stage 8 `PerforatedScreen` typology | **Omitted, and documented as unsupported** | The direct ray engine is binary. Faking porosity with an opaque face plus a scalar is wrong in exactly the metrics used to size a screen, because real effective transmission depends on incidence angle and perforation depth ratio and varies through the day by far more than the nominal open-area ratio. Preferred to plausible wrong physics. |
| Stage 8 first-hit stored by widening the Stage 2 cache | A **separate** `SolarAttributionCache` | The visibility cache is 1 bit per cell per sun group and is relied on upstream; attribution is 32 bits, a ~32× widening of a released on-disk format for one downstream reader. Built alongside, discardable, with its own identity — including a hash of the ordered occluder GUID table, because reordering leaves every stored index resolvable and silently pointing at the wrong element. |

Three defects were found and fixed in code these stages build on, all recorded in
`Stages5-8-Method.md`: the DDA start-voxel choice on lattice-aligned ray origins (§2.5), the
brute-force reference counting zero-length grazes and truncating rays (§2.5), and
`ThresholdForCumulativeCapture` returning a level the strict selector then excluded (§3.6).

### 2.4.3 Implementation record — Stage 9, and what its gate review changed in Stages 5–8

Full method reference: `documentation/Stage9-Method.md`.

Stage 9 was gated on an independent re-review of Stages 0–8 before any optimisation was written.
That review changed three things in the stages below it:

| Found | Change |
|---|---|
| The Stage 8 ideal-versus-rationalised comparison represented the Stage 7 selection as one thin plate per voxel. Measured against the voxel solid it stands in for: **28.2 % less** direct solar intercepted and **21.5 points more** wanted solar retained. A shallow ray crosses a voxel without ever meeting the single mid-height plate inside it. | `Query.VoxelSurfaceShadingElements` emits the boundary faces of the selected voxel set — the exact ray-traceable equivalent of the field's own "a ray entered this voxel". `Query.ShadingElements(IdealShadingResult)` converts the actual marching-tetrahedra mesh too. The Stage 8 comparison now uses the solid. The scalar field remains the source of truth. |
| Two further zero-length phantom visits in the Stage 6 DDA: staircase voxels at exact edge/corner crossings, and `LatticeTolerance` fixed at 1e-9 ceasing to fire beyond ~1e6 m of world offset (error in `q` reaches 4.8e-9 at 1e7 m on an oblique facade — reachable, UTM southern-hemisphere false northing is 1e7 m). | Every axis whose boundary falls at the same point along the ray now steps together; the tolerance is scale-aware, floored at the old 1e-9 so near-origin behaviour is unchanged. Traversal exposed as `Query.TraversedVoxels` so it can be probed directly. |
| Stage 8 scales the material cost by `AdmittedUnwantedEnergy`, which is **exactly zero** on an aperture with no unwanted solar — precisely the case where the right answer is "build nothing" and a cost term is needed to say so. | **Stage 8 unchanged.** Stage 9's `ShadingObjective` carries an explicit `MaterialCostReference` and defaults to `AdmittedDirectEnergy`. Scaling by *an energy* was confirmed correct: 0 % drift in cost/benefit over a 300× radiation change, against 300× for a bare fraction. |

Reviews of `EggCrate` Guid identity and of attribution-cache scale found no defect. Both are now
covered by regression tests rather than by argument.

Where Stage 9 itself diverged from §Stage 9 below:

| Planned here | As built | Why |
|---|---|---|
| `PatternSearch` **and** `NSGAII` **and** `ParetoFront` | Coarse lattice + compass refinement, **single objective** | Each evaluation is a full attribution rebuild (46–66 ms at 144 cells, provably not reusable between candidates). NSGA-II needs hundreds of evaluations to match what a compass search finds in tens, and introduces a random seed that must be stored and honoured to stay reproducible. A Pareto front is genuinely useful and is **deferred, not rejected**: `ShadingObjective` already exposes Benefit / Harm / Cost separately, which is the input a front needs. |
| `ShadingOptimisationProblem` holding typology + bounds + objectives + constraints | `ShadingObjective` + `List<ShadingParameter>`, passed separately | The "problem" was three separable things — what to optimise, over what range, and to what end. Splitting them lets the objective be stored with the result and swept without rebuilding the search. |
| Constraints satisfied by **rejection / resampling** | Constraints as **bounds plus granularity**, enforced by snapping | There are no non-box constraints in these four families. Snapping is cheaper, always feasible by construction, and exactly reproducible — resampling would need the random seed the design avoids. |
| Objectives including "minimise material area" as a third axis | Material as a **priced cost term** in kWh | Keeps the objective one comparable energy. The raw `MaterialFraction` is still reported on every result, so a caller can rank on it directly. |
| Tests on a convex synthetic 2-parameter objective and on ZDT1 | Tests on the **real** objective, including an exhaustively enumerated 1-D case | A synthetic convex bowl proves the search descends; it does not prove the search finds the optimum of *this* objective, which is not unimodal — the measured 1-D landscape has a local maximum at 0.35 m against the global one at 0.25 m. The optimiser reaches the enumerated global optimum in 10 evaluations of 30 lattice points. |
| — (not planned) | **Element counts capped by the analysis resolution** | Found in validation, and it matters because the failure looks like success. Blades pitched finer than the analysis grid shade *between* the sample points: an 11-blade array reported 100 % of unwanted solar blocked with 100 % of wanted solar retained. The accounting was correct; the geometry was finer than the analysis measuring it. |

### 2.5 Decision: keep the blocker's identity — in a *separate* structure

Agreed on PR #12. The engine must record **which element intercepted the sun**, not only that
something did, so shading performance can be reported per element, energy-weighted.

> **Superseded in implementation — and the implementation is right.** This section originally
> specified folding attribution into the shared visibility structure, replacing the `lit[p, g]` bit
> with a `firstHit[p, g]` blocker index. Stages 8–10 instead split it in two, and that is the design
> to follow:
>
> | Structure | Purpose |
> |---|---|
> | `SolarVisibilityCache` | Compact visible/blocked bitset (`ulong[][]`). Shared base and context visibility, reused across every aperture and every candidate device. |
> | `SolarAttributionCache` | First-hit element attribution, built **only** where needed — `RationaliseShading` and `VerifyShading`. |
>
> The original single-structure plan missed that attribution is needed only for *candidate devices*,
> while base visibility is reused across all apertures and all candidates. Merging them makes the
> common case pay for the rare one, and the ~450 KB → ~7.2 MB growth would have landed on the
> structure that is reused most. Stage 10.1's optimisation — tracing attribution only for the samples
> the base cache already reports lit — depends on the two staying separate.
>
> Do not retrofit `SolarVisibilityCache` to carry blocker indices.

**Getting the first hit.** `Query.IntersectionTuples(segment3D, candidates, sort, tolerance)` takes a
`sort` flag. The base visibility path passes `sort: false` deliberately — it needs only "was there any
hit", and skipping the ordering is the cheaper answer. Attribution needs the *nearest* hit, which does
not require sorting the whole list: a linear min-scan by distance along the segment is O(n) against
the sort's O(n log n), on a list that is typically a handful of candidates.

**Two constraints this imposes, both worth knowing before building:**

1. **Attribution requires the ray path, not the exact path.** The exact mode resolves all occluders
   simultaneously by polygon clipping — there is no "first" blocker to name. Attribution is therefore
   defined on the analysis grid and inherits its resolution. This is consistent (the Stage 2
   structure is grid-based anyway), but it must be stated rather than discovered: a fin narrower than
   `gridSize` may be under-attributed.
2. **Generated device elements need stable identity.** A Stage 8 device is fresh `Face3D` geometry
   that does not exist in the model. Each *element* — the overhang, the left fin, the right fin, each
   louvre blade — must be registered as context under its own stable GUID, or attribution returns a
   GUID naming nothing. Build this into `IShadingTypology` from the start; retrofitting identity onto
   generated geometry later is exactly the "expensive to recover" case.

**First-hit contribution is not removal-loss.** Each blocked ray has exactly one first hit, so
per-element intercepted energy sums to the system total by construction and the percentages are
internally consistent. But it is *order-dependent under overlap*: removing an overhang may simply
expose a fin behind it, so the fin's real marginal value is lower than its removal would suggest, and
the overhang's is lower than its first-hit share. Report first-hit as **direct contribution**.
**Marginal contribution** — recompute with one element removed — is the honest measure where elements
overlap, and is a later refinement, not first implementation.

**Vocabulary.** Intercepted energy is **intercepted**, never "reflected". SAM_SolarCalculator is a
direct-solar visibility and interception engine; it has no material optical properties and no
secondary rays. The supported chains are exactly:

```
sun → intercepted by <ShadeGuid>
sun → passes shade → aperture → first internal surface
```

Anything described as reflected solar would require inter-reflection modelling and is a separate
future capability (§7).

---

## 3. Precedent — what the literature actually gives us

The earlier plan cited the right evaluators but missed that **generative** algorithms exist. They are
old, they are geometric, and they map directly onto this architecture.

| Work | What it contributes here |
|---|---|
| **Shaviv (1975, 1999)** — computer generation of shading masks from the sun path | The original "generate the set of shadings that block direct radiation for each month". Precedent for generating rather than evaluating. |
| **Arumí-Noé (1996)**, *Algorithm for the geometric construction of an optimum shading device*, **Automation in Construction** | The closest published precedent to the brief's "solar collar". Two sequential steps: (1) construct a **winter solar funnel surface** guaranteeing full insolation; (2) **clip** it subject to summer shading conditions. This is exactly "ideal shape from wanted/unwanted sun vectors", solved analytically. Use it to seed and sanity-check Stage 6. |
| **Kaftan & Marsh (2005)** — cellular method | Divide the shading support surface into cells; score each by blocked direct gain over the shading period. Direct ancestor of Stage 6. |
| **Sargent, Niemasz & Reinhart (2011)**, *Shaderade*, IBPSA Building Simulation 2011, pp. 310–317 | Per-cell **optimal transmittance** from an annual calculation, tracing solar rays back from the window, weighting each timestep by cooling-minus-heating load. The canonical desirability formulation; Stage 5's weighting is a load-free approximation of it. Ported into Ladybug as `HB Load Shade Benefit`. |
| **PLOS One 10.1371/journal.pone.0203575** (curvilinear shading, cellular offices) and **10.1371/journal.pone.0212710** (optimal and near-optimal shapes, apartment buildings) | Both derive an optimal shape then explicitly report **near-optimal buildable simplifications** and the performance lost. This is the template for Stage 8's scoring. |
| **NSGA-II façade shading studies** — e.g. Nazari et al. 2023 (*Engineering Reports* e12726, Wallacei, 20 000 cases); Yao et al. 2024 (*Int. Comm. Heat Mass Transfer* 157:107697, egg-crate, 40–50 % annual energy reduction); Kunming dormitory study 2025 (*Scientific Reports* s41598-025-04465-8, optimum 0.35 m depth / 0.27 m spacing / 7° tilt) | Establishes that multi-objective optimisation over a small parameter set is the accepted final step, and gives target parameter ranges to validate against. |
| **Profile-angle / cutoff-angle sizing** (VSA → overhang depth, HSA → fin spacing; UN-Habitat *Sun shading catalogue*) | Textbook closed-form seeding for Stage 8, so the optimiser starts from a sane point. |

**The gap the literature confirms:** every one of these either (a) evaluates a shading surface the
designer supplied, or (b) generates a shape for a single idealised window analytically. None takes
*an arbitrary BIM model, per aperture, with real context obstruction, over a user-chosen period*, and
produces both an ideal mass and a rationalised buildable device. That is what this plan builds.

---

## 4. Staged plan

Each stage gives: **Goal · New code · Difficulty · Model + effort · a paste-ready AI prompt.**

Stages 0–4 are a vertical slice that is useful on its own (per-aperture irradiance, any period,
interactive). Stages 5–8 are the shading design work. Stages 9–11 are optimisation, UI and validation.

---

### Stage 0 — Aperture as a first-class analysis target

**Goal.** Make an `Aperture` something the solar engine can analyse directly, with its own cell grid,
local frame and stable identity — instead of reaching it through the coverage path's frame/pane split.

**Why.** `ToSAM_SolarModel(model, includeApertures: false)` — the default — drops apertures entirely.
Everything downstream needs per-aperture energy, not per-panel ratios.

**New code** — `SAM.Analytical.SolarCalculator`:
- `Classes/ApertureSolarTarget.cs` — aperture GUID, host panel GUID, `Face3D`, outward normal,
  azimuth/tilt, local `Plane` (origin at centroid, X horizontal, Y up-slope), area, analysis grid.
- `Create/ApertureSolarTargets.cs` — `Create.ApertureSolarTargets(AnalyticalModel, IEnumerable<Guid> apertureGuids = null, double gridSize = 0.5, …)`.
  **If `apertureGuids` is null or empty, select every aperture on a sun-exposed external panel.**
- `Query/AnalysisGrid.cs` — subdivide a `Face3D` into grid points in its own plane, reusing the
  `AddSampleCells` clipping logic (extract it, don't copy it — it is currently private in
  `Weather.SolarCalculator/Modify/Simulate.cs`).
- `Query/OutwardNormal.cs` — resolve normal direction against the host panel and its space, so a
  flipped `Face3D` does not silently invert the whole analysis.

**Difficulty.** Medium — the normal/winding fidelity risk the previous plan flagged is real.

**Model + effort.** **Opus, medium.** SAM's 2D-polygon-on-a-plane geometry model and the
panel/aperture/space relations need genuine reasoning; getting the normal wrong invalidates every
later stage silently.

> **Prompt —**
> In the SAM_SolarCalculator repo, add a new aperture-centric analysis target to
> `SAM_SolarCalculator/SAM.Analytical.SolarCalculator`.
>
> First read: `Convert/ToSAM/SolarModel.cs`, `Modify/Simulate.cs`, and the private `AddSampleCells` /
> `SampleCells` methods in `SAM.Weather.SolarCalculator/Modify/Simulate.cs`.
>
> Create `ApertureSolarTarget` holding: aperture Guid, host panel Guid, the aperture's `Face3D`, its
> outward `Vector3D` normal, azimuth and tilt (degrees, reuse `Geometry.Spatial.Query.Azimuth` and
> `Query.Tilt`), gross area, and a `List<Face3D>` of analysis grid faces with their areas and centroids.
>
> Add `Create.ApertureSolarTargets(AnalyticalModel analyticalModel, IEnumerable<Guid> apertureGuids,
> double gridSize, double tolerance_Area, double tolerance_Distance)`. When `apertureGuids` is null or
> empty it must return a target for EVERY aperture whose host panel satisfies `panel.IsExposedToSun()`
> and is not shared by two spaces — mirroring the filtering already in `ToSAM_SolarModel`.
>
> Naming: use **gridSize**, not cellSize — it matches the existing `_gridSize_` parameter on SAM's
> space-level `Simulate`. See §2.4 of `documentation/ShadingOptimisation-Plan.md`.
>
> The outward normal must be resolved against the host panel's orientation and the space it bounds, not
> taken blindly from `Face3D.GetPlane().Normal` — a flipped face must not invert the result. Add an
> xUnit test using `Tests/Fixtures/ModelA.sam` asserting every returned normal has a non-negative dot
> product with the host panel's outward normal, and that passing null selects all apertures while
> passing a subset selects exactly that subset.
>
> Extract the subdivision logic from `AddSampleCells` into a reusable public
> `Query.AnalysisGrid(Face3D, double gridSize, …)` rather than duplicating it; update
> `AddSampleCells` to call it. Follow SAM's `Create`/`Query`/`Modify` static-partial-class convention
> and the existing SPDX + copyright header.

---

### Stage 1 — Analysis period / HOY control

**Goal.** One C# type that produces the HOY set for: full year, a named season, a date range, a single
day, an hour-of-day window across a date range, or an explicit HOY list.

**Why.** This is the user-facing control the whole brief hangs on ("select season / full year / 24 h").
It must be a plain type, not a Grasshopper concern, so it is testable and reusable.

**New code** — `SAM.Core.SolarCalculator`:
- `Classes/AnalysisPeriod.cs` — `int Year`, `(int month, int day)` start/end, `int StartHour`,
  `int EndHour`, `int Timestep` (1 = hourly), `IEnumerable<int> HoursOfYear()`,
  `IEnumerable<DateTime> DateTimes()`. Must handle a period that **wraps the year end**
  (e.g. 1 Nov → 28 Feb — the heating season). **As built:** `AnalysisPeriod` deliberately carries
  *no* time-shift concept at all — it produces weather-timeline hours only, and the sun-sampling
  offset is a separate, explicit `SunTimeConvention` owned by the visibility cache (§2.4.1). Mixing
  the two in one type is what made the old `_timeShift_ = −30` default unexplainable.
  `Timestep != 1` now throws at construction rather than being silently ignored.
- `Enums/AnalysisPeriodPreset.cs` — `FullYear`, `Summer`, `Winter`, `Equinox`, `CoolingSeason`,
  `HeatingSeason`, `PeakSummerDay`, `PeakWinterDay`, `Custom`.
- `Create/AnalysisPeriod.cs` — preset → period. Hemisphere-aware: "summer" must flip when
  `Location.Latitude < 0`.

**Difficulty.** Low.

**Model + effort.** **Sonnet, low.** Date arithmetic and presets — mechanical, but write the
wrap-around and southern-hemisphere tests.

> **Prompt —**
> Add an `AnalysisPeriod` type to `SAM_SolarCalculator/SAM.Core.SolarCalculator` following SAM
> conventions (SPDX header, `SAMObject`-style JSON round-trip via `ToJsonObject`/`FromJsonObject` —
> see `SAM.Geometry.SolarCalculator/Classes/SolarModel.cs` for the pattern).
>
> Fields: Year, start month/day, end month/day, start hour, end hour, timestep-per-hour.
> Methods: `HoursOfYear()` and `DateTimes()`.
>
> Requirements:
> - A period that wraps the year boundary (start 11/1, end 2/28) must yield Nov+Dec+Jan+Feb hours, not
>   an empty set.
> - Start hour 9, end hour 17 must yield only hours 9–17 of each day in the range.
> - Add `Create.AnalysisPeriod(AnalysisPeriodPreset preset, int year, Core.Location location)` with
>   presets FullYear, Summer, Winter, Equinox, CoolingSeason, HeatingSeason, PeakSummerDay,
>   PeakWinterDay. Seasons must be hemisphere-aware — flip when `location.Latitude < 0`.
> - Handle leap years correctly (8784 vs 8760 hours).
>
> Add xUnit tests for: full year hour count in leap and non-leap years, year-wrap, hour-of-day
> filtering, and southern-hemisphere summer returning Dec–Feb. Do not add any Grasshopper code in this
> stage.

---

### Stage 2 — Sun grouping and the visibility calculation ★ core

**Goal.** Build the structure described in §2.1: group sun positions, run occlusion once per group,
store a per-grid-point lit bitset, and make any analysis period a pure re-weighting.

**Why.** This is what makes period switching interactive, and it is the foundation Stage 6 marches
rays through. Get it wrong and everything above it is slow or incorrect.

**New code** — `SAM.Weather.SolarCalculator`:
- `Classes/SunGroup.cs` — group index, representative direction, the HOYs assigned to it.
- `Create/SunGroups.cs` — `Create.SunGroups(Location, IEnumerable<DateTime>, double sunAngleStep)`.
  Group on (altitude, azimuth); the representative direction is the **irradiance-weighted mean** of
  the member hours' directions, not the group centre — this keeps the bias near zero on high-energy
  groups.
- `Classes/SolarVisibilityCache.cs` — geometry hash, group list, grid-point index, an element table,
  and `firstHit[p, g]` as a packed `ushort[]` (sentinel = visible) per §2.5. JSON round-trip so it
  persists on the `SolarModel` and survives a file save. **Internal type — never surfaced in
  Grasshopper (§2.4).**
- `Create/SolarVisibilityCache.cs` — build it by calling the existing occlusion pass once per group.
- `Query/GeometryHash.cs` — stable hash over the context `Face3D` vertex set + the analysis grid, so
  reuse is invalidated automatically when geometry moves. Round coordinates to tolerance before hashing.

**Also in this stage:** collapse the three duplicated occlusion loops (`Simulate`,
`Simulate_Sampled`, `ComputeSunExposure`) into one private method. Do this *first*, as a pure
refactor with the existing tests green, then build the cache on top of it.

**Difficulty.** High — performance-critical, correctness-critical, and touches shipped code paths.

**Model + effort.** **Opus, high.** Bitset packing, cache invalidation, the refactor of a
load-bearing method, and a bias analysis of the binning approximation.

> **Prompt —**
> Two-part task in `SAM_SolarCalculator/SAM.Weather.SolarCalculator`. Do part A completely, with the
> existing `dotnet test SAM_SolarCalculator/SAM_SolarCalculator.Tests` suite green, before starting B.
>
> **Part A — de-duplicate.** `Modify/Simulate.cs` contains the same occlusion loop three times:
> in `Simulate(SolarModel, Dictionary<DateTime,Vector3D>, bool, …)`, in `Simulate_Sampled`, and in
> `ComputeSunExposure`. Extract ONE private method that takes the merged-face dictionary, a sun
> direction, and the tolerances, and returns the lit `List<LinkedFace3D>` for that direction. Both the
> exact and sampled paths must route through it. Pure refactor — no behaviour change, all existing
> tests still green.
>
> While you are in this file, also fix: the `merge == true` branch of `Simulate` builds a `result` list
> of merged results but then returns `solarFaceSimulationResults` (the un-merged list), so `result` is
> a dead store and callers get the wrong data. Fix it and add a regression test.
>
> **Part B — sun grouping and the visibility cache.**
>
> *(Superseded by the implementation — kept for the record. As built: the type is `SunBin` /
> `Create.SunBins` with the engineer-facing `sunAngleStep` on the public surface; the representative
> direction is the angular **group centre**, deliberately NOT DNI-weighted, so the cache is
> weather-independent and a weather swap cannot force a geometric rebuild; the cache stores a
> boolean lit bitset rather than `firstHit`, deferred to Stage 8 — see §2.4.1 and §10 of
> `documentation/Stages0-4-Method.md`; and the signature gained `minHorizonAngle` and an explicit
> `timeShiftInMinutes` that is part of the cache identity, per correction B1.)*
>
> `Create.SunGroups(Core.Location location, IEnumerable<DateTime> dateTimes, double sunAngleStep)`:
> compute each hour's sun direction via `Geometry.SolarCalculator.Query.SunDirection`, discard hours
> below the horizon, group the rest on (altitude, azimuth) at `sunAngleStep` degrees resolution, and
> return `SunGroup` objects each holding the member DateTimes and a representative direction computed
> as the **irradiance-weighted mean** of member directions (weight by DNI from the SolarModel's
> WeatherData when available, else unweighted).
>
> `SolarVisibilityCache`: for a given analysis grid (from Stage 0's `ApertureSolarTarget`) and a set of
> `SunGroup`, store `firstHit[gridIndex, groupIndex]` as a packed `ushort[]` — the index into an
> element table of the surface that intercepted the sun, with a reserved sentinel for "visible". Do
> NOT store a plain visible/blocked bit: §2.5 of the plan requires the blocker's identity, and the
> sampled path already computes it — `Query.IntersectionTuples(...)` returns hits sorted, so
> `tuples_Intersection[0].Item1.Guid` is the first-hit surface and is currently discarded. Build it by
> running the extracted Part-A occlusion method once per group, in a `Parallel.For`. Include a geometry
> hash (`Query.GeometryHash`, coordinates rounded to `Core.Tolerance.Distance` before hashing) so reuse
> is invalidated automatically when geometry changes. Give it `ToJsonObject`/`FromJsonObject`.
>
> Note that attribution is only available on the ray/sampled path — the exact polygon-clipping path
> resolves all occluders at once and has no "first" blocker. Build the cache on the ray path.
>
> Naming (see §2.4 of `documentation/ShadingOptimisation-Plan.md`): the parameter is **sunAngleStep**,
> not binSize. `SolarVisibilityCache` stays an internal type name — it is accurate and it is not
> user-facing — but no Grasshopper input, output or description may use the word "cache".
>
> Add xUnit tests using `Tests/Fixtures/ModelA-WithShade.sam`:
> 1. Cache-backed lit fractions for a full year match a direct per-hour `Simulate_Coverage` run to
>    within 2 % mean absolute error at a 2° sun-angle step — report the actual figure in test output.
> 2. Group count at 2° is at least 4× smaller than the daylight-hour count.
> 3. Changing any context face invalidates the geometry hash.
> 4. JSON round-trip preserves `firstHit` exactly, element table included.
> 5. With a single known shade in front of an aperture, every blocked grid point attributes to that
>    shade's Guid — not to a sentinel and not to a neighbouring surface.
>
> Report the measured build time and the bias from grouping. If a 2° step exceeds 2 % MAE, report that
> honestly and recommend the step that does not — do not tune the test to pass.

---

### Stage 3 — Anisotropic sky + per-grid-point view factors

**Goal.** Replace the isotropic diffuse/ground terms with Perez 1990, and compute per-grid-point sky
and ground view factors that account for context obstruction.

**Why.** §2.3. Without this, "unwanted hours" are chosen from a biased irradiance estimate, and the
generated shape inherits the bias.

**New code** — `SAM.Geometry.SolarCalculator` and `SAM.Weather.SolarCalculator`:
- `Create/Radiation.cs` — add a Perez overload alongside the existing isotropic one (**do not change
  the existing signature** — binary compatibility is explicitly protected in this repo, see the
  `Simulate_Coverage` overload comments).
- `Enums/SkyModel.cs` — `Isotropic`, `PerezAnisotropic`.
- `Query/SkyPatchDirections.cs` — Tregenza 145-patch (and Reinhart 577 for validation) direction set
  with solid angles.
- `Create/ViewFactors.cs` — per-grid-point `SVF` / `GVF` by running the Stage-2 occlusion pass
  against the patch directions once.

Also fix the `ToInt32` time-zone truncation (§1.8) here, since it is in the same file.

**Difficulty.** Medium — the maths is published and closed-form; the risk is units and conventions.

**Model + effort.** **Opus, medium.** Perez coefficient bands and the azimuth convention in the
existing code (`solarAzimuth = (rad + π/2) · 180/π`, `tilt_Temp = 180 − tilt`) are easy to get subtly
wrong, and the error is a plausible-looking number rather than a crash.

> **Prompt —**
> In `SAM_SolarCalculator`, add an anisotropic sky model alongside the existing isotropic one.
>
> Read `SAM.Geometry.SolarCalculator/Create/Radiation.cs` and
> `SAM.Weather.SolarCalculator/Create/Radiation.cs` first. Note the existing conventions carefully:
> `tilt_Temp = 180 - tilt`, and `solarAzimuth = (radians + π/2) * 180/π`. Your new code must use the
> SAME conventions or results will be silently wrong.
>
> 1. Add `Create.Radiation(...)` overloads taking a `SkyModel` enum (`Isotropic`,
>    `PerezAnisotropic`). **Keep the existing signatures byte-for-byte unchanged** and delegate to them
>    with `SkyModel.Isotropic` — this repo protects binary compatibility deliberately (see the
>    `Simulate_Coverage` overload remarks in `SAM.Analytical.SolarCalculator/Modify/Simulate.cs`).
> 2. Implement Perez et al. 1990 all-weather: sky clearness ε, brightness Δ, the F1/F2 circumsolar and
>    horizon-brightening coefficients from the standard 8-bin table, and the a/b circumsolar geometry
>    terms. Cite the paper in a comment.
> 3. Add `Query.SkyPatchDirections(SkyPatchSubdivision)` returning Tregenza-145 and Reinhart-577
>    direction sets with per-patch solid angles.
> 4. Add `Create.ViewFactors(...)` computing per-grid-point sky and ground view factors by testing
>    each sky-patch direction for obstruction using the Stage-2 occlusion pass. Unobstructed vertical
>    surface must give SVF ≈ 0.5.
> 5. Fix the fractional-time-zone bug in `SAM.Weather.SolarCalculator/Create/Radiation.cs`: it does
>    `System.Convert.ToInt32(Core.Query.Double(uTC))`, truncating UTC+5:30 to an integer. Carry the
>    fractional offset through.
>
> Tests: unobstructed vertical surface SVF ≈ 0.5 ± 0.01; Perez and isotropic agree within 5 % on a
> horizontal surface under overcast conditions (ε ≈ 1) and diverge by more than 10 % on a south-facing
> vertical surface under clear conditions (ε > 6); a surface fully enclosed by context has SVF ≈ 0;
> a location at UTC+5:30 produces a sun position ~30 min different from UTC+5:00.

---

### Stage 4 — Per-aperture irradiance over any period

**Goal.** The first end-to-end useful result: per-aperture, per-grid-point incident irradiance for any
`AnalysisPeriod`, computed from the stored visibility in milliseconds.

**New code** — `SAM.Analytical.SolarCalculator`:
- `Classes/ApertureIrradianceResult.cs : ISolarSimulationResult` — aperture GUID, period, per-grid-point
  kWh/m², aperture totals (kWh and kWh/m²), direct/diffuse/reflected split, sunlit-hours count.
- `Modify/SimulateApertures.cs` — orchestration: resolve weather → build or reuse visibility →
  apply period weights → emit results → attach via `AddResult<Aperture>`.
- `Query/WeatherData.cs` — the shared resolution helper mandated by §2.4: supplied `WeatherData`
  wins, else the one on the `AnalyticalModel`, else a clear error. Every stage that needs weather
  calls this; none reimplements the precedence.
- `Query/CachedIrradiance.cs` — the weighted sum from §2.1.

**Difficulty.** Medium.

**Model + effort.** **Opus, medium.** Mostly bookkeeping, but the unit handling (W/m² → kWh/m²,
timestep scaling, area weighting) is where silent errors live.

> **Prompt —**
> Add per-aperture irradiance to `SAM_SolarCalculator/SAM.Analytical.SolarCalculator`, combining
> Stage 0's `ApertureSolarTarget`, Stage 1's `AnalysisPeriod`, Stage 2's `SolarVisibilityCache` and
> Stage 3's view factors and Perez sky.
>
> `ApertureIrradianceResult : ISolarSimulationResult` — follow `SolarCoverageSimulationResult` for the
> constructor/JSON/`Reference` pattern. Hold: aperture Guid, the `AnalysisPeriod`, per-grid-point
> kWh/m², area-weighted aperture total in kWh and kWh/m², the direct/diffuse/ground-reflected split,
> and sunlit-hour count per grid point.
>
> `Modify.SimulateApertures(AnalyticalModel analyticalModel, WeatherData weatherData, AnalysisPeriod
> analysisPeriod, IEnumerable<int> hoursOfYear, IEnumerable<Guid> apertureGuids, double gridSize,
> SkyModel skyModel, double sunAngleStep, bool recalculate)`:
> resolve weather, build or reuse the visibility calculation (keyed by geometry hash, stored on the
> `SolarModel` under a new `SolarModelParameter`), evaluate the period, attach results with
> `analyticalModel.AddResult<Aperture>`, and return them.
>
> Default/empty behaviour is fixed by §2.4 of `documentation/ShadingOptimisation-Plan.md` — implement
> it exactly:
> - `weatherData` null → use the `WeatherData` on the `AnalyticalModel`; neither present → error.
>   Put this precedence in ONE shared `Query.WeatherData(AnalyticalModel, WeatherData)` helper and
>   call it from every stage; do not reimplement it.
> - `apertureGuids` null/empty → all valid external sun-exposed apertures.
> - `analysisPeriod` null AND `hoursOfYear` null/empty → Full Year.
> - `hoursOfYear` supplied → it **overrides** `analysisPeriod`. When both are given, the caller must be
>   told, not silently ignored.
> - `gridSize` default 0.5 m, `sunAngleStep` default 2°, `recalculate` default false.
>
> Units are the main risk — be explicit. Weather data is W/m²; results are kWh/m². Account for the
> timestep and for per-grid-point area weighting when aggregating to the aperture total.
>
> Tests using `Tests/Fixtures/ModelA-NoShade.sam` and `ModelA-WithShade.sam`:
> - A south-facing aperture receives more annual irradiance than a north-facing one (northern hemisphere).
> - The shaded model yields strictly less than the unshaded one on the same aperture.
> - Summer-period + winter-period totals are within 1 % of the full-year total for the same aperture
>   (conservation — no double counting, no gaps).
> - Two different `AnalysisPeriod`s reusing the SAME visibility calculation produce different results
>   without rebuilding it (assert the geometry pass runs once — count calls).
> - Halving `gridSize` changes the aperture total by less than 2 % (grid convergence).
> - Explicit `hoursOfYear` wins over a conflicting `analysisPeriod`.
> - A model with WeatherData attached and a null `weatherData` argument resolves to the model's.

---

### Stage 5 — Desirability weighting: which sun is unwanted?

**Goal.** Turn each hour into a signed weight: positive = blocking is beneficial, negative = blocking
is harmful.

**Why.** This is the single most consequential modelling choice in the whole workflow, and it is where
Phase 1 is knowingly approximate. It deserves its own stage with its own tests, and the strategy must
be swappable so Phase 2 can plug in TAS loads without touching anything else.

**New code** — `SAM.Analytical.SolarCalculator`:
- `Interfaces/IDesirabilityStrategy.cs` — `double Weight(DateTime, WeatherHour, ApertureSolarTarget)`.
- `Classes/SeasonalDesirability.cs` — user-defined wanted/unwanted periods. Simplest, most explainable.
- `Classes/TemperatureDesirability.cs` — weight by `dryBulbTemperature − balanceTemperature`,
  clamped. A genuine proxy for cooling vs heating demand, needs only the EPW.
- `Classes/IrradianceThresholdDesirability.cs` — unwanted above a threshold (e.g. glare/overheating
  risk), reproducing the common practitioner rule.
- `Classes/CompositeDesirability.cs` — weighted blend.
- **Phase 2 hook:** `LoadDesirability` reading TAS cooling/heating loads — the true Shaderade
  formulation. Define the interface now so this drops in later.

**Difficulty.** Low–Medium in code; high in judgement.

**Model + effort.** **Opus, medium.** The code is easy; the reasoning about what "unwanted" means,
and documenting the approximation honestly, is the valuable part.

> **Prompt —**
> Add a pluggable desirability-weighting strategy to
> `SAM_SolarCalculator/SAM.Analytical.SolarCalculator`.
>
> Background: the Shaderade method (Sargent, Niemasz & Reinhart, IBPSA Building Simulation 2011)
> weights each timestep by the zone's cooling-minus-heating load, so blocking sun during a
> cooling-dominated hour scores positive and blocking during a heating-dominated hour scores negative.
> SAM_SolarCalculator has NO thermal load model, so Phase 1 must approximate this. Design the interface
> so a load-based strategy can be added later without changing callers.
>
> `IDesirabilityStrategy` with `double Weight(DateTime dateTime, WeatherHour weatherHour,
> ApertureSolarTarget target)`, returning positive when blocking direct sun at that hour is beneficial
> and negative when it is harmful. Implement:
> - `SeasonalDesirability` — explicit wanted and unwanted `AnalysisPeriod`s.
> - `TemperatureDesirability` — `(dryBulbTemperature - balanceTemperature)` clamped to [-1, 1],
>   balance temperature default 15.5 °C, configurable.
> - `IrradianceThresholdDesirability` — positive above a configurable incident-irradiance threshold.
> - `CompositeDesirability` — weighted blend of the above.
>
> Every implementation must carry an XML-doc comment stating plainly what it approximates and how it
> differs from load-based Shaderade weighting. This is a documented approximation, not a silent one.
>
> Tests: for a London EPW, `TemperatureDesirability` returns negative weights for the majority of
> January daylight hours and positive for the majority of July afternoon hours; `SeasonalDesirability`
> returns exactly zero outside both defined periods; `CompositeDesirability` with a single component at
> weight 1.0 equals that component alone.

---

### Stage 6 — The shading potential field ★ the custom core

**Goal.** For each aperture, produce the scalar voxel field of §2.2 — the graded "ideal shading mass".

**Why.** This is the piece that does not exist anywhere. It is the answer to *"show me the optimal
shape."*

**New code** — `SAM.Analytical.SolarCalculator`:
- `Classes/ShadingVolume.cs` — the design envelope in front of an aperture: origin at the aperture
  plane, extents in the local frame (out, up, down, left, right), voxel size. Constructible from
  simple limits, or clipped to a user-supplied `Shell` (site boundary, oversail limit).
- `Classes/ShadingPotentialField.cs` — the voxel grid + `double[] Benefit`, plus
  `PositiveTotal`, `NegativeTotal`, `Percentile(double)` for threshold selection.
- `Create/ShadingPotentialField.cs` — the ray-march:

```
for each sun group g (parallel):
    d  = desirability weight of group g          // Stage 5, energy-weighted
    if |d| < epsilon: continue
    for each aperture grid point a_i lit at g:   // Stage 2 bitset — O(1) lookup
        march a ray from centroid(a_i) along -direction(g) through the voxel grid
        for each voxel v entered:
            Benefit[v] += area(a_i) * d
```

Use a 3-D DDA (Amanatides–Woo) traversal, not per-voxel intersection tests.
Accumulate per-thread and reduce, to avoid contention on `Benefit`.

- `Query/IdealShadingVoxels.cs` — threshold the field; largest-connected-component filter to drop
  floating specks; optional "must touch the façade" constraint for buildability.

**Validation anchor.** For a simple south-facing window with no context, the thresholded field must
reproduce the Arumí-Noé construction: a winter solar funnel clipped by the summer shading condition.
That is a real, checkable geometric prediction, and it is how you know the field is right rather than
merely plausible.

**Difficulty.** High.

**Model + effort.** **Opus, high.** Novel algorithm, 3-D traversal correctness, parallel reduction,
and a non-obvious validation strategy. This is the hardest reasoning in the project.

> **Prompt —**
> Implement the shading potential field in `SAM_SolarCalculator/SAM.Analytical.SolarCalculator`. This
> is the core novel algorithm — read `documentation/ShadingOptimisation-Plan.md` §2.2 and §3 first.
>
> Method (a volumetric generalisation of Kaftan & Marsh 2005 and Shaderade / Sargent, Niemasz &
> Reinhart 2011): discretise the space in front of an aperture into voxels and score each voxel by how
> much *desirability-weighted* direct beam it would intercept on its way to the aperture.
>
> `ShadingVolume` — a voxel grid in the aperture's local frame (Stage 0 gives the frame): extents
> out/up/down/left/right from the aperture plane, plus voxel size. Support clipping to a user-supplied
> `SAM.Geometry.Spatial.Shell` so site/oversail limits are respected.
>
> `Create.ShadingPotentialField(ApertureSolarTarget target, SolarVisibilityCache cache,
> IEnumerable<SunGroup> sunGroups, IDesirabilityStrategy desirability, WeatherData weatherData,
> ShadingVolume volume)`:
>
> ```
> for each sun group g, in parallel:
>     d = Σ over hours h in group g of  desirability.Weight(h) · DNI(h) · cos θ(h) · Δt
>     if |d| < epsilon: skip
>     for each aperture grid point a_i marked lit at group g:
>         march a ray from centroid(a_i) along -direction(g) through the voxel grid
>         for each voxel v the ray enters:
>             Benefit[v] += area(a_i) · d
> ```
>
> Requirements:
> - Use a 3-D DDA voxel traversal (Amanatides & Woo). Do NOT test every voxel for ray intersection.
> - Accumulate into per-thread buffers and reduce at the end — do not lock or use Interlocked per voxel.
> - Positive `Benefit` = material here blocks unwanted sun. Negative = material here blocks WANTED sun.
> - Expose `Percentile(double)` so a threshold can be chosen by "keep the top N % of benefit".
> - Follow the §2.4 naming decisions: grid points come from `gridSize`, sun groups from `sunAngleStep`.
> - `Query.IdealShadingVoxels(field, threshold, bool requireFacadeContact)` — threshold, keep the
>   largest connected component, optionally require connection to the aperture plane.
>
> Validation test — this is the important one. For a synthetic south-facing window at 51.5° N with no
> surrounding context, a seasonal desirability (summer unwanted / winter wanted), and a shallow
> shading volume, assert that:
> - the thresholded voxel set lies predominantly ABOVE the window head (a horizontal overhang emerges
>   naturally, not by construction);
> - voxels in the low-winter-sun path directly in front of the window have NEGATIVE benefit;
> - the depth at which benefit crosses zero along the window's centre horizontal agrees within 15 %
>   with the closed-form profile-angle prediction `D = H / tan(VSA)`, where VSA is the vertical shadow
>   angle at the summer/winter cutoff date.
>
> That third assertion is the real check — it ties the numerical field to the analytical
> Arumí-Noé/profile-angle result. If it does not hold, the field is wrong; report the discrepancy
> rather than loosening the tolerance.

---

### Stage 7 — Extract the viewable ideal shape

**Goal.** Turn the voxel field into geometry the engineer can look at, rotate, and judge.

**New code** — `SAM.Geometry.SolarCalculator`:
- `Create/IsoSurface.cs` — marching cubes over `ShadingPotentialField` → `Mesh3D`.
- `Convert/ToShell.cs` — optional `Mesh3D` → `Shell` for downstream SAM interop. **Must be allowed to
  fail gracefully** — nothing depends on it (§2.2).
- `Query/ShadingMetrics.cs` — projected area, volume, max projection depth, benefit captured.

**Difficulty.** Medium. Marching cubes is standard; the mesh cleanup is the fiddly part.

**Model + effort.** **Sonnet, medium.** Well-known algorithm with published lookup tables. Escalate to
Opus only if the `Shell` conversion proves troublesome — and if it does, ship without it.

> **Prompt —**
> Add isosurface extraction to `SAM_SolarCalculator/SAM.Geometry.SolarCalculator`.
>
> `Create.IsoSurface(ShadingPotentialField field, double threshold)` → `SAM.Geometry.Spatial.Mesh3D`,
> using marching cubes with the standard 256-entry edge/triangle tables. Include vertex interpolation
> along edges (not mid-point snapping) so the surface is smooth, and weld duplicate vertices within
> `Core.Tolerance.Distance`.
>
> Add `Convert.ToShell(Mesh3D, tolerance)` producing a `SAM.Geometry.Spatial.Shell`, and
> `Query.ShadingMetrics(...)` returning projected area onto the aperture plane, enclosed volume, maximum
> projection depth from the aperture plane, and the fraction of the field's total positive benefit that
> the thresholded region captures.
>
> `ToShell` MUST return null rather than throwing when the mesh is not cleanly convertible — SAM's
> `Shell` booleans are tolerance-sensitive and no caller may depend on this succeeding. Log a warning
> and carry on.
>
> Tests: a field that is uniformly above threshold inside a box produces a closed mesh whose volume
> matches the box within 5 %; a field entirely below threshold produces an empty mesh, not null and not
> an exception; the extracted mesh has no naked edges for a closed region.

---

### Stage 8 — Rationalise to a buildable device ★ the second custom core

**Goal.** The brief's "seeing this shape we could run second option, convert to practical solution."
Fit real, manufacturable typologies to the ideal field and score the trade-off honestly.

**New code** — `SAM.Analytical.SolarCalculator`:
- `Interfaces/IShadingTypology.cs` — parameter vector ⟷ `List<Face3D>` geometry, plus bounds.
- `Classes/` — `Overhang` (depth, tilt, offset above head, side extension), `VerticalFins`
  (count, depth, angle, spacing), `HorizontalLouvres` (pitch, depth, angle, offset),
  `EggCrate` (overhang + fins), `PerforatedScreen` (offset, porosity, depth).
- `Query/SeedParameters.cs` — closed-form profile-angle seeding: read the zero-crossing depth from
  the field, compute VSA/HSA, size the first candidate with `D = H / tan(VSA)`. Start the optimiser
  from a sane point, not a random one.
- `Query/FitScore.cs`:

```
Capture = Σ_{v ∈ device} max(Benefit(v), 0) / Σ_v max(Benefit(v), 0)
Harm    = Σ_{v ∈ device} min(Benefit(v), 0)          // negative
Material= device surface area
Score   = Capture + λ·Harm − μ·Material              // λ, μ user-exposed
```

- `Modify/VerifyDevice.cs` — **the honest closer.** Add the device's `Face3D`s to the model as
  context, re-run Stage 4 twice (with and without the device), and report the *actual* change. The
  field is a heuristic; this is the truth. Always report both.
- `Classes/ShadingPerformanceResult.cs` — the reported metrics (below).

**The metrics** (agreed on PR #12). All **energy-weighted**, never sun-vector counts — a low-energy
December morning must not weigh the same as a July noon. Over a period `P`, with `U` = direct energy
reaching the aperture **unshaded** and `S` = direct energy still admitted **with** the device:

| Metric | Definition |
|---|---|
| Direct solar intercepted [kWh] | `U − S` |
| Direct Shading Efficiency [%] | `(U − S) / U` |
| Unwanted Solar Blocked [%] | `(U − S) / U` over the **unwanted** period |
| Wanted Solar Retained [%] | `S / U` over the **wanted** period |
| Direct contribution of element `e` [%] | `Intercepted(e) / U`, from `firstHit` attribution (§2.5) |

Per-element contributions sum to Direct Shading Efficiency by construction — each blocked ray has
exactly one first hit. Worked example, matching the review:

```
Unshaded direct       420 kWh
Intercepted           310 kWh    Direct Shading Efficiency  73.8 %
  Overhang            180 kWh    42.9 %
  Left fin             61 kWh    14.5 %
  Right fin            54 kWh    12.9 %
  Louvres              15 kWh     3.6 %
```

Report these as **direct contribution**. They are *not* removal-loss — see §2.5 on overlap. Marginal
contribution (recompute with one element removed) is a later refinement; the API should leave room
for it without implementing it now.

**Difficulty.** High.

**Model + effort.** **Opus, medium-high.** The trig is textbook; the typology abstraction, the
per-element identity plumbing and the scoring design need care, and the verify-loop must not be
allowed to become optional.

> **Prompt —**
> Implement shading rationalisation in `SAM_SolarCalculator/SAM.Analytical.SolarCalculator` — fitting
> buildable devices to the Stage 6 potential field.
>
> `IShadingTypology`: a parameter vector (`double[]`) with named bounds, and
> `List<ShadingElement> Geometry(ApertureSolarTarget target, double[] parameters)`. Implement `Overhang`
> (depth, tilt angle, offset above window head, side extension), `VerticalFins` (count, depth, angle,
> spacing), `HorizontalLouvres` (pitch, depth, blade angle, offset), `EggCrate` (overhang + fins), and
> `PerforatedScreen` (offset from façade, porosity, depth).
>
> **Each element carries a stable Guid and a name** ("Overhang", "Left fin", "Louvre 3") — a
> `ShadingElement` is `{ Guid, Name, List<Face3D> }`, NOT a bare `List<Face3D>`. Per §2.5 of
> `documentation/ShadingOptimisation-Plan.md`, per-element performance reporting depends on
> first-hit attribution naming a real element; generated geometry with no identity cannot be
> attributed, and retrofitting identity later is the expensive case this is meant to avoid. The Guid
> must be stable across re-evaluations with the same parameters so results are comparable.
>
> `Query.SeedParameters(IShadingTypology, ShadingPotentialField, ApertureSolarTarget)`: derive a sane
> starting parameter set in closed form rather than starting from random. Find the depth at which
> benefit crosses zero along the window's centre line, convert to a vertical shadow angle, and size an
> overhang as `D = windowHeight / tan(VSA)`; use the horizontal shadow angle equivalently for fins.
> Cite the profile-angle method in comments.
>
> `Query.FitScore(IShadingTypology, double[] parameters, ShadingPotentialField)`: voxelise the device
> geometry into the field's grid and compute
> `Capture` (fraction of total positive benefit covered), `Harm` (sum of negative benefit covered,
> negative), and `Material` (device surface area). Return all three separately AND a combined
> `Score = Capture + lambda*Harm - mu*Material` with caller-supplied lambda and mu.
>
> `Modify.VerifyDevice(AnalyticalModel, ApertureSolarTarget, IShadingTypology, double[] parameters,
> AnalysisPeriod wanted, AnalysisPeriod unwanted)` → `ShadingPerformanceResult`: register each
> `ShadingElement` as context under its own Guid, run the Stage 4 aperture simulation **twice** — once
> without the device (the unshaded baseline `U`) and once with it (`S`) — and compute, energy-weighted
> from WeatherData, for each period:
>
> - direct solar intercepted [kWh] = `U - S`
> - Direct Shading Efficiency [%] = `(U - S) / U`
> - Unwanted Solar Blocked [%] = `(U - S) / U` over the unwanted period
> - Wanted Solar Retained [%] = `S / U` over the wanted period
> - per-element direct contribution [kWh and %] = `Intercepted(e) / U`, read from the `firstHit`
>   attribution in the Stage 2 structure
>
> Everything is weighted by solar energy per timestep, NOT by counting sun directions — a December
> morning must not weigh the same as a July noon. Per-element contributions must sum to Direct Shading
> Efficiency; assert this in a test.
>
> Call the per-element figure **direct contribution**. Do NOT call it the loss from removing the
> element — it is order-dependent under overlap (§2.5). Leave a documented extension point for
> marginal contribution (recompute with one element removed) but do not implement it now.
>
> Never describe intercepted energy as "reflected" — this engine has no material optical properties
> and no secondary rays (§2.5).
>
> `VerifyDevice` is the ground truth — `FitScore` is only a heuristic over the voxel field, and the two
> can disagree.
>
> Tests: on a south-facing window at 51.5° N, the seeded overhang depth is within 25 % of the
> optimiser's converged depth (so seeding genuinely helps); `VerifyDevice` on a 1 m overhang shows
> reduced summer and reduced winter irradiance, with the summer reduction larger; a zero-depth device
> scores `Capture == 0`, `Material == 0` and Direct Shading Efficiency 0 %; `FitScore` ranks a deep
> overhang above a shallow one for a summer-unwanted weighting; for an egg-crate, per-element
> contributions sum to the system total within floating-point tolerance; an element narrower than
> `gridSize` produces a warning about attribution resolution rather than a silently low contribution.
>
> Do NOT let `VerifyDevice` become optional or skippable in the API — every reported result must be
> verifiable against a real simulation.

---

### Stage 8.1 — Direct solar penetration into the space — **DEFERRED**

> **Deferred: not part of Phase 1 / Stage 11 scope.** Recorded here as a planned capability, not as
> work to schedule now. Phase 1 validates the aperture-level shading and direct-solar workflow only.
>
> When it is built, keep **geometric penetration as the fundamental quantity** and add transmitted
> solar as a **separate, construction-aware** figure — do not silently replace one with the other. The
> geometric/transmittance decision belongs to the dedicated Stage 8.1 / Phase-2 work, not to the
> Phase-1 plan. Glazing angular transmittance and SHGC treatment are recorded as a known Phase-1
> limitation in §7.

**Goal.** Follow the rays that *get through*. For each unblocked sun path, continue through the
aperture and find the first internal surface it lands on, so shading can be judged by how much direct
sun it keeps off the floor — not only by irradiance at the glass line.

**Why.** Irradiance at the aperture plane is the physics; direct sun on the floor at 15:00 in July is
what the client actually complains about. This is the metric that makes the tool legible to a design
team, and it is a natural extension of §2.5 — the same ray, one segment further.

**New code** — `SAM.Analytical.SolarCalculator`:
- `Query/InternalFace3Ds.cs` — the bounding faces of the space behind an aperture, classified
  floor / wall / ceiling. Note `ToSAM_SolarModel` deliberately *excludes* these (it skips panels
  shared by two spaces and keeps only sun-exposed ones), so this is a new query, not a filter on the
  existing set.
- `Classes/SolarPenetrationResult.cs` — per internal surface and per surface type: direct energy
  received [kWh], with and without the device, and the reduction [%].
- `Modify/SimulatePenetration.cs` — for each grid point `p` and sun group `g` where
  `firstHit[p, g] == visible`, cast a segment from `p` along `direction(g)` into the space and take
  the first internal hit, energy-weighted exactly as Stage 8.

**Reported as:**

```
Without shading   direct solar reaching floor   185 kWh
With shading      direct solar reaching floor    46 kWh
Floor Solar Reduction                           75.1 %
```

**One thing to be explicit about.** This is *geometric* penetration — the direct beam incident on the
aperture, projected onward. It does not apply glazing transmittance. Either state that plainly in the
result, or multiply by the `ApertureConstruction`'s solar transmittance where one is available. Do
not leave it ambiguous: a number that looks like transmitted solar gain but is actually incident
energy is the kind of thing that ends up in a report.

**Difficulty.** Medium — the machinery all exists; the work is the internal-face query and honest
labelling.

**Model + effort.** **Opus, medium.** Mostly reuse, but the space/aperture adjacency and the
transmittance boundary are both easy to get quietly wrong.

> **Prompt —**
> Add direct solar penetration reporting to `SAM_SolarCalculator/SAM.Analytical.SolarCalculator`,
> extending Stage 8. Read §2.5 and Stage 8.1 of `documentation/ShadingOptimisation-Plan.md` first.
>
> `Query.InternalFace3Ds(AnalyticalModel, Aperture)`: the bounding faces of the space the aperture
> serves, classified floor / wall / ceiling via `PanelType` and surface tilt. Note that
> `Convert.ToSAM_SolarModel` deliberately excludes internal panels (it skips panels shared by two
> spaces), so build this from the `AdjacencyCluster` — do not try to filter the SolarModel's set.
>
> `Modify.SimulatePenetration(AnalyticalModel, ApertureSolarTarget, SolarVisibilityCache, AnalysisPeriod,
> WeatherData)`: for every analysis grid point and sun group where the cache says the point is VISIBLE
> (not intercepted), cast a segment from the grid point along the sun direction into the space and take
> the first internal surface hit using `Geometry.Object.Spatial.Query.IntersectionTuples`. Accumulate
> energy-weighted direct solar per internal surface and per surface type, using the same per-timestep
> DNI·cosθ·Δt weighting as Stage 8 — not sun-vector counts.
>
> Return a `SolarPenetrationResult` giving, per surface and per type, direct energy received [kWh] and
> — when run against a model with and without the device — the reduction [%].
>
> Be explicit in the API and XML docs that this is GEOMETRIC penetration of the beam incident on the
> aperture: it does not apply glazing transmittance. Either state that in the result type's
> documentation or multiply by the `ApertureConstruction` solar transmittance when one is available and
> say which you did. Do not leave it ambiguous — a figure that reads as transmitted solar gain but is
> actually incident energy will end up in a report.
>
> Tests: a south-facing window with no shading puts direct solar on the floor in winter (low sun) and
> less in summer (high sun) for the same room; adding a 1 m overhang reduces summer floor energy more
> than winter; the sum over all internal surfaces equals the total admitted direct energy from Stage 8
> within tolerance (nothing lost, nothing double-counted); a fully shaded aperture yields zero
> penetration rather than null.

---

### Stage 9 — Optimisation

> **IMPLEMENTED.** The method reference is `documentation/Stage9-Method.md`; the divergences from
> what is planned below are recorded in §2.4.3. The plan text is retained unedited as the original
> intent. The no-external-dependency decision held; the algorithm choice did not.

**Goal.** Search each typology's parameter space against the objectives. Because Stage 2 made each
evaluation cheap, this can be plain C# — no Galapagos, Wallacei, or Opossum dependency.

**New code** — `SAM.Analytical.SolarCalculator`:
- `Classes/ShadingOptimisationProblem.cs` — typology + bounds + objectives + constraints.
- `Query/PatternSearch.cs` — parallel coordinate/pattern search for single objective. Robust,
  derivative-free, no tuning, ~150 lines.
- `Query/NSGAII.cs` — compact NSGA-II (fast non-dominated sort, crowding distance, SBX crossover,
  polynomial mutation) for the Pareto front. ~300 lines.
- `Classes/ParetoFront.cs` — the non-dominated set with its parameter vectors and metrics.

Objectives: minimise unwanted-period irradiance; maximise wanted-period irradiance; minimise material
area. Constraints: max projection depth, min blade pitch, manufacturable angle steps.

**Difficulty.** Medium — well-trodden algorithms, and the expensive part is already solved.

**Model + effort.** **Sonnet, medium.** NSGA-II is textbook. Use Opus only if the objectives need
rethinking after seeing Stage 8 results.

> **Prompt —**
> Add optimisation to `SAM_SolarCalculator/SAM.Analytical.SolarCalculator`. Pure C#, no external
> optimiser dependency — Stage 2's cache makes each evaluation cheap enough that we do not need
> Galapagos/Wallacei/Opossum.
>
> `ShadingOptimisationProblem`: an `IShadingTypology`, its parameter bounds, a list of objectives
> (minimise unwanted-period irradiance, maximise wanted-period irradiance, minimise material area) and
> constraints (max projection depth, minimum blade pitch, angle snapped to manufacturable steps).
>
> `Query.PatternSearch(problem, double[] seed, int maxEvaluations)`: derivative-free compound pattern
> search for the single-objective case. Evaluate the pattern in parallel. Start from Stage 8's
> `SeedParameters`.
>
> `Query.NSGAII(problem, populationSize, generations)`: standard NSGA-II — fast non-dominated sort,
> crowding-distance selection, simulated binary crossover, polynomial mutation. Return a `ParetoFront`
> of non-dominated parameter vectors with their objective values. Evaluate each generation in parallel
> via `Parallel.For`.
>
> Both must respect constraints by rejection (resample), not by penalty, so returned solutions are
> always feasible. Both must be deterministic given a seed, for testable results.
>
> Tests: on a convex synthetic 2-parameter objective, `PatternSearch` reaches the known optimum within
> 1 %; `NSGAII` on ZDT1 (standard benchmark) produces a front whose hypervolume is within 5 % of the
> analytic front after 100 generations; every returned solution satisfies the constraints; the same
> seed gives identical results across runs.

---

### Stage 10 — Grasshopper components

**Goal.** Expose it. Seven new components, following the existing convention exactly.

| Component | Purpose |
|---|---|
| `SAMAnalytical.AnalysisPeriod` | Preset or custom → `AnalysisPeriod` + HOY list |
| `SAMAnalytical.ApertureSolarTargets` | Model (+ optional aperture selection) → targets; empty = all |
| `SAMAnalytical.ApertureIrradiance` | Targets + period → per-aperture irradiance; reuses previous work automatically |
| `SAMAnalytical.ShadingPotentialField` | Target + desirability + volume → field (+ preview mesh) |
| `SAMAnalytical.IdealShadingShape` | Field + threshold → `Mesh3D`/`Shell` + metrics |
| `SAMAnalytical.RationaliseShading` | Field + typology → seeded + optimised device + fit score |
| `SAMAnalytical.VerifyShading` | Device → actual before/after irradiance per period |

Plus a `GooShadingPotentialField` / `GooApertureSolarTarget` param pair in
`SAM.Core.Grasshopper.SolarCalculator`, and mesh preview with a legend for the field.

**The input set and defaults are fixed by §2.4** — that table is the specification, not a suggestion.
Every analysis component takes the same nine inputs in the same order with the same defaults, so the
workflow reads consistently across the toolbar.

**Difficulty.** Low–Medium — mechanical, but there is a lot of it, and the reuse UX needs thought.

**Model + effort.** **Sonnet, medium.** Boilerplate-heavy with a clear template to copy. Two
judgement calls: reuse must be visible enough that a stale result is never silent, without dragging
"cache" into the engineer's vocabulary; and an overridden input must announce itself.

> **Prompt —**
> Add Grasshopper components for the shading workflow to
> `SAM_SolarCalculator/Grasshopper/SAM.Analytical.Grasshopper.SolarCalculator/Component/`.
>
> Copy the conventions from `SAMAnalyticalSolarSimulation.cs` exactly: derive from
> `GH_SAMVariableOutputParameterComponent`, return `GH_SAMParam[]` from `Inputs`/`Outputs`, mark
> parameters `ParamVisibility.Binding` or `.Voluntary`, give each component a NEW fixed
> `ComponentGuid` (generate once, never change), set `LatestComponentVersion` to "1.0.0", category
> "SAM", sub-category "Solar", and include the SPDX + copyright header.
>
> Components: `SAMAnalytical.AnalysisPeriod`, `SAMAnalytical.ApertureSolarTargets`,
> `SAMAnalytical.ApertureIrradiance`, `SAMAnalytical.ShadingPotentialField`,
> `SAMAnalytical.IdealShadingShape`, `SAMAnalytical.RationaliseShading`, `SAMAnalytical.VerifyShading`.
>
> **The input set is specified in §2.4 of `documentation/ShadingOptimisation-Plan.md`. Implement that
> table exactly — names, order, visibility and defaults.** For the analysis components that is:
>
> | Input | Visibility | Default / empty behaviour |
> |---|---|---|
> | `_analyticalModel` | Binding | required |
> | `_weatherData_` | Voluntary | supplied wins; else the model's; else error |
> | `_apertures_` | Voluntary | empty → ALL valid external sun-exposed apertures |
> | `_analysisPeriod_` | Voluntary | empty → Full Year |
> | `_HOYs_` | Voluntary | explicit HOYs OVERRIDE `_analysisPeriod_` |
> | `_gridSize_` | **Binding** | 0.5 (metres) |
> | `_sunAngleStep_` | Voluntary | 2 (degrees) |
> | `_recalculate_` | Voluntary | false |
> | `_run` | Binding | false |
>
> Terminology is decided — do not deviate: **GridSize** not CellSize, **SunAngleStep** not BinSize,
> **Recalculate** not RebuildCache. The word "cache" must not appear in any component name, input,
> output or description.
>
> Further requirements:
> - Long-running components return immediately when `_run` is false — as `SAMAnalyticalSolarSimulation`
>   does.
> - Reuse and invalidation are AUTOMATIC, driven by the geometry hash. `_recalculate_` is a manual
>   override, not the normal route. Expose a `reusedPreviousCalculation` boolean output as a
>   diagnostic, and when the geometry hash differs, recompute and say so via
>   `AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, ...)`. A stale result must never be returned
>   silently — but the engineer must not have to manage this by hand.
> - When BOTH `_HOYs_` and `_analysisPeriod_` are supplied, use the HOYs and emit a `Remark` saying the
>   period was overridden. Never discard an input silently.
> - `ShadingPotentialField` outputs both the field object and a coloured preview `Mesh3D`
>   (blue = negative benefit, red = positive) with a legend.
> - Add `GooApertureSolarTarget` and `GooShadingPotentialField` param wrappers in
>   `SAM.Core.Grasshopper.SolarCalculator`, following the `GooResult`/`GooResultParam` pattern.
>
> Write the component descriptions for an engineer who has not read this plan: say what the component
> does, what the units are, and what each default means. Describe behaviour, not implementation.

---

### Stage 11 — Validation and documentation

**Goal.** Prove it, and say plainly where it is approximate.

**Work:**
1. **Cross-validation** — per-aperture annual irradiance against an independent tool for one test
   model. Ladybug's `LB Incident Radiation` is fine *as a reference*, run once, offline. This is the
   one place Ladybug appears in the project, and it is a cross-check, not a dependency.
2. **Analytical checks** — unobstructed horizontal surface vs the closed-form clear-sky annual total;
   overhang cutoff depth vs the profile-angle formula; SVF of an unobstructed vertical surface = 0.5.
3. **Regression gate** — the existing SAM-vs-TAS ~0.9 % coverage benchmark must not move. Any change
   to the shared occlusion code (Stage 2 part A especially) is guarded by it.
4. **Sun-grouping bias study** — MAE vs sun-angle step at 1°/2°/5°, published in the docs so users
   can choose.
5. **Grid convergence** — grid size and voxel size sensitivity, documented.
6. **`documentation/Stage11-Validation.md`** — **written**: consolidated validation status
   and the assumptions register (§3), fitness-for-purpose guidance (§4), the open validation gaps
   (§2.5, §5) and the deferred capabilities (§6).

**Difficulty.** Medium.

**Model + effort.** **Sonnet, medium** for the test harness and docs; **Opus, medium** for
interpreting the validation statistics and writing the assumptions register — that is a judgement
document, and it is what makes the tool trustworthy in a report.

> **Prompt —**
> Build the validation suite and documentation for the shading workflow.
>
> Add to `SAM_SolarCalculator.Tests` (xUnit, .NET 8, real `.sam` fixtures — see the existing
> `Tests/README.md`):
> - `AnalyticalValidationTests` — unobstructed horizontal surface annual irradiance vs a closed-form
>   clear-sky calculation (within 10 %); overhang cutoff depth vs the profile-angle formula (within
>   15 %); unobstructed vertical surface SVF = 0.5 ± 0.01.
> - `SunGroupingBiasTests` — mean absolute error of reuse-based vs exact per-hour annual irradiance at 1°,
>   2° and 5° sun-angle steps. Emit a table to test output.
> - `ConvergenceTests` — aperture total vs grid size (0.25/0.5/1.0 m) and field benefit vs voxel size.
> - `ShadingMetricsTests` — per-element direct contributions sum to Direct Shading Efficiency; solar
>   penetration across internal surfaces sums to admitted direct energy; both within tolerance.
> - Confirm the existing SAM-vs-TAS coverage benchmark still passes unchanged.
>
> Then write `documentation/ShadingOptimisation-Method.md` covering: the method and its literature
> basis (Kaftan & Marsh 2005; Sargent, Niemasz & Reinhart 2011 Shaderade; Arumí-Noé 1996; Shaviv),
> the sun-grouping architecture and its measured bias, and — most importantly — an **assumptions
> register** listing every approximation with its expected magnitude and direction:
> isotropic vs Perez sky, no inter-reflection between surfaces (and therefore "intercepted", never
> "reflected"), no thermal load model (desirability is approximated — see Stage 5), sun-position
> grouping, grid and voxel discretisation, attribution resolution being bounded by `gridSize`,
> direct contribution not being removal-loss, solar penetration being geometric rather than
> transmittance-adjusted, and the `minHorizonAngle` cutoff.
>
> Write the register so an engineer can decide whether the tool is fit for their specific job. Report
> real measured numbers from the tests, not estimates. If a validation target is not met, write down
> that it is not met and by how much — do not adjust the tolerance to make it pass.

---

## 5. Model and effort assignment at a glance

| Stage | Work | Model | Effort |
|---|---|---|---|
| 0 | Aperture analysis target | **Opus** | Medium |
| 1 | Analysis period / HOY | Sonnet | Low |
| 2 | **Sun grouping + visibility calculation** | **Opus** | **High** |
| 3 | Perez sky + view factors | **Opus** | Medium |
| 4 | Per-aperture irradiance | **Opus** | Medium |
| 5 | Desirability weighting | **Opus** | Medium |
| 6 | **Shading potential field** | **Opus** | **High** |
| 7 | Isosurface extraction | Sonnet | Medium |
| 8 | **Rationalisation to buildable + performance metrics** | **Opus** | Medium–High |
| 8.1 | Direct solar penetration into the space — **deferred, not Phase 1** | **Opus** | Medium |
| 9 | Optimisation (pattern search, NSGA-II) | Sonnet | Medium |
| 10 | Grasshopper components | Sonnet | Medium |
| 11 | Validation + assumptions register | Sonnet (+Opus for the register) | Medium |

**Rule of thumb.** Opus at high effort for the two stages that invent something (2 and 6) and the
stages where a silent wrong answer is plausible (0, 3, 4, 5, 8, 8.1). Sonnet for the stages with a
clear template or a textbook algorithm (1, 7, 9, 10, 11).

---

## 6. Sequencing

**Slice 1 — useful on its own (Stages 0–4).** Per-aperture irradiance, any period, interactive.
Ship it, use it, validate it. Nothing about shading design yet, and it is already worth having.

**Slice 2 — the design tool (Stages 5–8.1).** Desirability, the potential field, the ideal shape, the
rationalised device, and the performance metrics that make it reportable. Prototype Stage 6 on a single synthetic south-facing window with no context
before running it on a real model — the analytical profile-angle check in the Stage 6 prompt is the
gate.

**Slice 3 — polish (Stages 9–11).** Optimisation, components, validation.

**Do not start Stage 6 until Stage 2's sun-grouping bias is measured and accepted.** The field is
built on that visibility calculation; if it is biased, the shape is wrong in a way that looks
entirely reasonable.

---

## 7. Risks, and what is still missing

| Risk | Mitigation |
|---|---|
| **No thermal load model.** "Unwanted sun" is approximated (Stage 5). Real Shaderade uses cooling-minus-heating load. | `IDesirabilityStrategy` is pluggable; Phase 2 reads TAS loads through the existing SAM_Tas link. Document the approximation in the register. |
| **Isotropic sky biases the weighting.** | Stage 3 (Perez) is scheduled *before* the field is built, deliberately. |
| **Sun-grouping approximation.** Grouping sun positions introduces error. | Measured, not assumed — Stage 2 test 1 and Stage 11's bias study. `SunAngleStep` is user-controllable. |
| **`Shell` boolean fragility.** | Designed out. Booleans are used only in Stage 7 for display and are allowed to fail (§2.2). |
| **SAM geometry fidelity** — flipped normals, degenerate faces, apertures lost on empty spaces. | Stage 0 resolves normals against the host panel and space; assert on every fixture. |
| **Stale reuse.** A silently reused calculation gives a confidently wrong answer. | Automatic invalidation on the geometry hash, a visible `reusedPreviousCalculation` diagnostic output, and `_recalculate_` as a manual override (Stage 10, §2.4). |
| **No inter-reflection.** Specular and diffuse bounce off context is ignored. | Same limitation as `LB Incident Radiation`; only full Radiance solves it. Documented, not hidden — and the reason intercepted energy is never described as "reflected" (§2.5). |
| **Attribution resolution.** A shading element narrower than `gridSize` is under-attributed, because first-hit attribution lives on the analysis grid (§2.5). | Warn when any element's minimum dimension is below `gridSize`. Element totals stay correct in aggregate; only the per-element split degrades. |
| **Glazing transmittance not modelled.** Direct solar is evaluated at the aperture plane; no angular transmittance or SHGC is applied, so figures are incident, not transmitted. | Phase-1 limitation, recorded in the assumptions register. Stage 8.1 (deferred) adds construction-aware transmitted solar as a separate quantity alongside geometric penetration. |
| **First-hit contribution read as removal-loss.** Overlapping elements make the two differ. | Named **direct contribution** throughout, with the distinction stated wherever it is reported. Marginal contribution left as a documented extension point. |
| **Voxel memory.** Fine voxels × many apertures. | Voxel size is a parameter; the field is per-aperture and disposable. A 3 m × 3 m × 1.5 m volume at 50 mm is ~1.6 M voxels ≈ 13 MB — fine. Warn above a threshold. |

**Explicitly out of scope for Phase 1:** glare (DGP), daylight autonomy, thermal comfort, energy
demand, dynamic/movable shading control, BIPV yield, **internal solar penetration (Stage 8.1)**, and
**glazing angular transmittance / SHGC** — direct solar is evaluated at the aperture plane, with no
construction-aware transmission applied. The architecture does not preclude any of
them — the sun-group visibility cache is the right substrate for all of them — but none is attempted
here.

---

## 8. Why not just use Ladybug Tools?

Worth stating plainly, since the previous plan was built on it.

- **Ladybug's engine is the wrong shape for this problem.** `LB Incident Radiation` is a
  cumulative-sky calculation: it intersects geometry with a static sky dome once and gives a total.
  It is excellent at that and poor at "which specific sun vectors reach this window cell at which
  hours" — which is precisely the question every stage here asks.
- **The direct-beam question is what SAM already answers**, exactly, with polygon clipping rather than
  ray sampling. Reusing it keeps one engine, one set of tolerances, and one validation story against
  TAS.
- **Dependency cost.** Ladybug means Python, honeybee-radiance, a Radiance install, and version
  coupling to LBT releases — inside a C#/.NET toolkit that currently has none of that. `SAM_LadybugTools`
  exists and is useful for Honeybee model exchange, but pulling Radiance into the solar calculator's
  critical path is a large, permanent cost for a capability SAM already has.
- **What Ladybug is genuinely better at, and where it stays:** annual daylight, glare, and anything
  needing inter-reflection. Phase 2, through `SAM_LadybugTools`, not through this repo.
- **And it stays as a reference.** Stage 11 cross-checks against `LB Incident Radiation` once,
  offline. Borrowing the validation without inheriting the dependency is the right trade.

---

## 9. References

- Shaviv, E. (1975/1999) — computer-generated shading masks from the sun path.
- Arumí-Noé, F. (1996) *Algorithm for the geometric construction of an optimum shading device*,
  **Automation in Construction** 5(3). Winter solar funnel surface, then clipped to summer shading —
  the analytical precedent for Stage 6.
- Kaftan, E. & Marsh, A. (2005) *Integrating the cellular method for shading design with a thermal
  simulation*. The cellular scoring method.
- Sargent, J.A., Niemasz, J. & Reinhart, C.F. (2011) *Shaderade: combining Rhinoceros and EnergyPlus
  for the design of static exterior shading devices*, **Proc. Building Simulation 2011 (IBPSA)**,
  Sydney, pp. 310–317. Per-cell optimal transmittance from annual load-weighted desirability.
- *Optimisation of curvilinear external shading of windows in cellular offices*, **PLOS One**
  10.1371/journal.pone.0203575.
- *On optimal and near-optimal shapes of external shading of windows in apartment buildings*,
  **PLOS One** 10.1371/journal.pone.0212710. Optimal → near-optimal buildable simplification.
- Perez, R. et al. (1990) *Modeling daylight availability and irradiance components from direct and
  global irradiance*, **Solar Energy** 44(5), 271–289. Stage 3.
- Nazari, S., Keshavarz Mirza Mohammadi, P. & Sareh, P. (2023), **Engineering Reports** 5(10):e12726.
  NSGA-II via Wallacei, 20 000 cases.
- Yao, B. et al. (2024), **International Communications in Heat and Mass Transfer** 157:107697.
  NSGA-II egg-crate shading, 40–50 % annual energy reduction.
- *Multiobjective optimization of external shading for west-facing university dormitories in Kunming*
  (2025), **Scientific Reports** s41598-025-04465-8. Optimum 0.35 m depth, 0.27 m spacing, 7° tilt.
- Amanatides, J. & Woo, A. (1987) *A Fast Voxel Traversal Algorithm for Ray Tracing*, Eurographics.
  Stage 6.
- UN-Habitat, *Sun shading catalogue* — profile-angle d/h and p/h design ratios. Stage 8.
