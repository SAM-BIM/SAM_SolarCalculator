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
| 1 | Sun-position-binned visibility cache | Decouples geometry cost from period selection. No tool exposes this per-aperture. |
| 2 | Per-aperture (not per-panel) analysis target | `Simulate` currently drops apertures entirely; only `Simulate_Coverage` includes them. |
| 3 | Shading **potential field** (voxel scalar field) | Shaderade/`LB Shade Benefit` *evaluate* a supplied surface. Nothing *generates* the solid. |
| 4 | Ideal-shape → buildable-device rationaliser | No open-source tool fits overhang/fin/louvre/egg-crate to a target mask with a scored trade-off. |
| 5 | Anisotropic (Perez) sky | Current model is isotropic — see §2.3. Material for vertical façades. |

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
- `minHorizonAngle` culls near-horizon sun; `_timeShift_` defaults to −30 min to match TAS EDSL.

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
**sun-position bin cache**:

> Geometry (expensive) depends only on the **sun direction**.
> Radiation (cheap) depends on the **hour**.

Over a year the sun occupies a narrow 2-D band of the sky. Quantise sun position into bins of, say,
2° × 2° in (altitude, azimuth). Roughly **600–900 bins** cover all daylight hours at a mid-latitude
site, against ~4 400 daylight hours. Then:

1. **Build once** (per geometry): for each bin `b`, run the existing occlusion pass and store a
   **bitset** `lit[cell, b] ∈ {0,1}` over the analysis cells of every selected aperture.
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

**Storage:** 20 apertures × 200 cells × 900 bins ≈ 3.6 M bits ≈ 450 KB. Negligible.

**Accuracy:** bin size is a user parameter. 2° bins introduce a sub-degree sun-position error, well
below the model's other approximations; 1° bins double the build cost and are available for final
runs. This must be validated against exact per-hour simulation (Stage 11).

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

- `a_i` — an aperture analysis cell, area weight `w_i`
- `b` — a sun bin that lights `a_i` (straight out of the Stage 2 cache)
- `Desirability(b) = Σ_{h : bin(h)=b} weight(h) · DNI(h) · cosθ(h) · Δt`,
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
  azimuth/tilt, local `Plane` (origin at centroid, X horizontal, Y up-slope), area, cell grid.
- `Create/ApertureSolarTargets.cs` — `Create.ApertureSolarTargets(AnalyticalModel, IEnumerable<Guid> apertureGuids = null, double cellSize = 0.5, …)`.
  **If `apertureGuids` is null or empty, select every aperture on a sun-exposed external panel.**
- `Query/AnalysisCells.cs` — subdivide a `Face3D` into cells in its own plane, reusing the
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
> `Query.Tilt`), gross area, and a `List<Face3D>` of analysis cells with their areas and centroids.
>
> Add `Create.ApertureSolarTargets(AnalyticalModel analyticalModel, IEnumerable<Guid> apertureGuids,
> double cellSize, double tolerance_Area, double tolerance_Distance)`. When `apertureGuids` is null or
> empty it must return a target for EVERY aperture whose host panel satisfies `panel.IsExposedToSun()`
> and is not shared by two spaces — mirroring the filtering already in `ToSAM_SolarModel`.
>
> The outward normal must be resolved against the host panel's orientation and the space it bounds, not
> taken blindly from `Face3D.GetPlane().Normal` — a flipped face must not invert the result. Add an
> xUnit test using `Tests/Fixtures/ModelA.sam` asserting every returned normal has a non-negative dot
> product with the host panel's outward normal, and that passing null selects all apertures while
> passing a subset selects exactly that subset.
>
> Extract the cell-subdivision logic from `AddSampleCells` into a reusable public
> `Query.AnalysisCells(Face3D, double cellSize, …)` rather than duplicating it; update
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
  (e.g. 1 Nov → 28 Feb — the heating season) and the `_timeShift_` convention (−30 min, TAS EDSL).
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

### Stage 2 — Sun-position binning and the visibility cache ★ core

**Goal.** Build the cache described in §2.1: bin sun positions, run occlusion once per bin, store a
per-cell lit bitset, and make any analysis period a pure re-weighting.

**Why.** This is what makes period switching interactive, and it is the foundation Stage 6 marches
rays through. Get it wrong and everything above it is slow or incorrect.

**New code** — `SAM.Weather.SolarCalculator`:
- `Classes/SunBin.cs` — bin index, representative direction, the HOYs assigned to it.
- `Create/SunBins.cs` — `Create.SunBins(Location, IEnumerable<DateTime>, double binSizeDegrees)`.
  Bin on (altitude, azimuth); the representative direction is the **irradiance-weighted mean** of the
  member hours' directions, not the bin centre — this keeps the bias near zero on high-energy bins.
- `Classes/SolarVisibilityCache.cs` — geometry hash, bin list, cell index, and the lit bitset
  (`System.Collections.BitArray` or a packed `ulong[]`). JSON round-trip so it can be cached on the
  `SolarModel` and survive a file save.
- `Create/SolarVisibilityCache.cs` — build it by calling the existing occlusion pass once per bin.
- `Query/GeometryHash.cs` — stable hash over the context `Face3D` vertex set + the analysis cells,
  so the cache invalidates when geometry moves. Round coordinates to tolerance before hashing.

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
> **Part B — the cache.** Implement sun-position binning and a visibility cache.
>
> `Create.SunBins(Core.Location location, IEnumerable<DateTime> dateTimes, double binSizeDegrees)`:
> compute each hour's sun direction via `Geometry.SolarCalculator.Query.SunDirection`, discard hours
> below the horizon, bin the rest on (altitude, azimuth) at `binSizeDegrees` resolution, and return
> `SunBin` objects each holding the member DateTimes and a representative direction computed as the
> **irradiance-weighted mean** of member directions (weight by DNI from the SolarModel's WeatherData
> when available, else unweighted).
>
> `SolarVisibilityCache`: for a given set of analysis cells (from Stage 0's
> `ApertureSolarTarget`) and a set of `SunBin`, store a packed bitset `lit[cellIndex, binIndex]`. Build
> it by running the extracted Part-A occlusion method once per bin, in a `Parallel.For`. Include a
> geometry hash (`Query.GeometryHash`, coordinates rounded to `Core.Tolerance.Distance` before hashing)
> so the cache can be invalidated when geometry changes. Give it `ToJsonObject`/`FromJsonObject`.
>
> Add xUnit tests using `Tests/Fixtures/ModelA-WithShade.sam`:
> 1. Cache-backed lit fractions for a full year match a direct per-hour `Simulate_Coverage` run to
>    within 2 % mean absolute error at 2° bins — report the actual figure in the test output.
> 2. Bin count at 2° is at least 4× smaller than the daylight-hour count.
> 3. Changing any context face invalidates the geometry hash.
> 4. JSON round-trip preserves the bitset exactly.
>
> Report the measured build time and the bias from binning. If 2° bins exceed 2 % MAE, report that
> honestly and recommend the bin size that does not — do not tune the test to pass.

---

### Stage 3 — Anisotropic sky + per-cell view factors

**Goal.** Replace the isotropic diffuse/ground terms with Perez 1990, and compute per-cell sky and
ground view factors that account for context obstruction.

**Why.** §2.3. Without this, "unwanted hours" are chosen from a biased irradiance estimate, and the
generated shape inherits the bias.

**New code** — `SAM.Geometry.SolarCalculator` and `SAM.Weather.SolarCalculator`:
- `Create/Radiation.cs` — add a Perez overload alongside the existing isotropic one (**do not change
  the existing signature** — binary compatibility is explicitly protected in this repo, see the
  `Simulate_Coverage` overload comments).
- `Enums/SkyModel.cs` — `Isotropic`, `PerezAnisotropic`.
- `Query/SkyPatchDirections.cs` — Tregenza 145-patch (and Reinhart 577 for validation) direction set
  with solid angles.
- `Create/ViewFactors.cs` — per-cell `SVF` / `GVF` by running the Stage-2 occlusion pass against the
  patch directions once.

Also fix the `ToInt32` time-zone truncation (§1.8) here, since it is in the same file.

**Difficulty.** Medium — the maths is published and closed-form; the risk is units and conventions.

**Model + effort.** **Opus, medium.** Perez coefficient bins and the azimuth convention in the
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
> 4. Add `Create.ViewFactors(...)` computing per-cell sky view factor and ground view factor by testing
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

**Goal.** The first end-to-end useful result: per-aperture, per-cell incident irradiance for any
`AnalysisPeriod`, computed from the cache in milliseconds.

**New code** — `SAM.Analytical.SolarCalculator`:
- `Classes/ApertureIrradianceResult.cs : ISolarSimulationResult` — aperture GUID, period, per-cell
  kWh/m², aperture totals (kWh and kWh/m²), direct/diffuse/reflected split, sunlit-hours count.
- `Modify/SimulateApertures.cs` — orchestration: build or reuse cache → apply period weights →
  emit results → attach to the `AnalyticalModel` via `AddResult<Aperture>`.
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
> constructor/JSON/`Reference` pattern. Hold: aperture Guid, the `AnalysisPeriod`, per-cell kWh/m²,
> area-weighted aperture total in kWh and kWh/m², the direct/diffuse/ground-reflected split, and
> sunlit-hour count per cell.
>
> `Modify.SimulateApertures(AnalyticalModel, AnalysisPeriod, IEnumerable<Guid> apertureGuids, double
> cellSize, SkyModel skyModel, double binSizeDegrees, bool rebuildCache)`:
> build or reuse the cache (keyed by geometry hash, stored on the `SolarModel` under a new
> `SolarModelParameter`), evaluate the period, attach results with `analyticalModel.AddResult<Aperture>`,
> and return them. Null/empty `apertureGuids` means all apertures.
>
> Units are the main risk — be explicit. Weather data is W/m²; results are kWh/m². Account for the
> timestep and for per-cell area weighting when aggregating cells to the aperture total.
>
> Tests using `Tests/Fixtures/ModelA-NoShade.sam` and `ModelA-WithShade.sam`:
> - A south-facing aperture receives more annual irradiance than a north-facing one (northern hemisphere).
> - The shaded model yields strictly less than the unshaded one on the same aperture.
> - Summer-period + winter-period totals are within 1 % of the full-year total for the same aperture
>   (conservation — no double counting, no gaps).
> - Two different `AnalysisPeriod`s on the SAME cache produce different results without rebuilding it
>   (assert the geometry pass runs once — count calls).
> - Halving `cellSize` changes the aperture total by less than 2 % (grid convergence).

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
for each sun bin b (parallel):
    d  = desirability weight of bin b            // Stage 5, energy-weighted
    if |d| < epsilon: continue
    for each aperture cell a_i lit at b:         // Stage 2 cache — O(1) lookup
        march a ray from centroid(a_i) along -direction(b) through the voxel grid
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
> IEnumerable<SunBin> bins, IDesirabilityStrategy desirability, WeatherData weatherData,
> ShadingVolume volume)`:
>
> ```
> for each sun bin b, in parallel:
>     d = Σ over hours h in bin b of  desirability.Weight(h) · DNI(h) · cos θ(h) · Δt
>     if |d| < epsilon: skip
>     for each aperture cell a_i that the cache marks lit at bin b:
>         march a ray from centroid(a_i) along -direction(b) through the voxel grid
>         for each voxel v the ray enters:
>             Benefit[v] += area(a_i) · d
> ```
>
> Requirements:
> - Use a 3-D DDA voxel traversal (Amanatides & Woo). Do NOT test every voxel for ray intersection.
> - Accumulate into per-thread buffers and reduce at the end — do not lock or use Interlocked per voxel.
> - Positive `Benefit` = material here blocks unwanted sun. Negative = material here blocks WANTED sun.
> - Expose `Percentile(double)` so a threshold can be chosen by "keep the top N % of benefit".
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
  context, re-run Stage 4, and report the *actual* change in wanted and unwanted irradiance. The field
  is a heuristic; this is the truth. Always report both.

**Difficulty.** High.

**Model + effort.** **Opus, medium-high.** The trig is textbook; the typology abstraction and the
scoring design need care, and the verify-loop must not be allowed to become optional.

> **Prompt —**
> Implement shading rationalisation in `SAM_SolarCalculator/SAM.Analytical.SolarCalculator` — fitting
> buildable devices to the Stage 6 potential field.
>
> `IShadingTypology`: a parameter vector (`double[]`) with named bounds, and
> `List<Face3D> Geometry(ApertureSolarTarget target, double[] parameters)`. Implement `Overhang`
> (depth, tilt angle, offset above window head, side extension), `VerticalFins` (count, depth, angle,
> spacing), `HorizontalLouvres` (pitch, depth, blade angle, offset), `EggCrate` (overhang + fins), and
> `PerforatedScreen` (offset from façade, porosity, depth).
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
> AnalysisPeriod wanted, AnalysisPeriod unwanted)`: add the device geometry to the model as context,
> re-run the Stage 4 aperture simulation for both periods, and return the actual percentage change in
> incident irradiance for each. This is the ground truth — `FitScore` is only a heuristic over the
> voxel field, and the two can disagree.
>
> Tests: on a south-facing window at 51.5° N, the seeded overhang depth is within 25 % of the
> optimiser's converged depth (so seeding genuinely helps); `VerifyDevice` on a 1 m overhang shows
> reduced summer and reduced winter irradiance, with the summer reduction larger; a zero-depth device
> scores `Capture == 0` and `Material == 0`; `FitScore` ranks a deep overhang above a shallow one for
> a summer-unwanted weighting.
>
> Do NOT let `VerifyDevice` become optional or skippable in the API — every reported result must be
> verifiable against a real simulation.

---

### Stage 9 — Optimisation

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
| `SAMAnalytical.ApertureIrradiance` | Targets + period → per-aperture irradiance; caches internally |
| `SAMAnalytical.ShadingPotentialField` | Target + desirability + volume → field (+ preview mesh) |
| `SAMAnalytical.IdealShadingShape` | Field + threshold → `Mesh3D`/`Shell` + metrics |
| `SAMAnalytical.RationaliseShading` | Field + typology → seeded + optimised device + fit score |
| `SAMAnalytical.VerifyShading` | Device → actual before/after irradiance per period |

Plus a `GooShadingPotentialField` / `GooApertureSolarTarget` param pair in
`SAM.Core.Grasshopper.SolarCalculator`, and mesh preview with a legend for the field.

**Difficulty.** Low–Medium — mechanical, but there is a lot of it, and the caching UX (when does the
cache rebuild?) needs thought.

**Model + effort.** **Sonnet, medium.** Boilerplate-heavy with a clear template to copy. One
judgement call: make cache reuse visible to the user, so a stale result is never silent.

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
> Requirements:
> - Long-running components take a `_run` boolean and return immediately when false — as
>   `SAMAnalyticalSolarSimulation` does.
> - `_apertures_` on `ApertureSolarTargets` is `ParamVisibility.Voluntary`; when empty, ALL apertures
>   are analysed. State this explicitly in the component description.
> - `ApertureIrradiance` must expose cache state to the user: a `cacheReused` boolean output and a
>   `_rebuildCache_` input. A stale cache must never be used silently — if the geometry hash differs,
>   rebuild and say so via `AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, ...)`.
> - `ShadingPotentialField` outputs both the field object and a coloured preview `Mesh3D`
>   (blue = negative benefit, red = positive) with a legend.
> - Add `GooApertureSolarTarget` and `GooShadingPotentialField` param wrappers in
>   `SAM.Core.Grasshopper.SolarCalculator`, following the `GooResult`/`GooResultParam` pattern.
>
> Write the component descriptions for an engineer who has not read this plan: say what the component
> does, what the units are, and what the defaults mean.

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
4. **Binning bias study** — MAE vs bin size at 1°/2°/5°, published in the docs so users can choose.
5. **Grid convergence** — cell size and voxel size sensitivity, documented.
6. **`documentation/`** — method description, the assumptions register (isotropic vs Perez, no
   inter-reflection, no load model, binning approximation), and a worked example.

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
> - `BinningBiasTests` — mean absolute error of cache-based vs exact per-hour annual irradiance at 1°,
>   2° and 5° bin sizes. Emit a table to test output.
> - `ConvergenceTests` — aperture total vs cell size (0.25/0.5/1.0 m) and field benefit vs voxel size.
> - Confirm the existing SAM-vs-TAS coverage benchmark still passes unchanged.
>
> Then write `documentation/ShadingOptimisation-Method.md` covering: the method and its literature
> basis (Kaftan & Marsh 2005; Sargent, Niemasz & Reinhart 2011 Shaderade; Arumí-Noé 1996; Shaviv),
> the sun-binning architecture and its measured bias, and — most importantly — an **assumptions
> register** listing every approximation with its expected magnitude and direction:
> isotropic vs Perez sky, no inter-reflection between surfaces, no thermal load model (desirability is
> approximated — see Stage 5), sun-position binning, cell and voxel discretisation, and the
> `minHorizonAngle` cutoff.
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
| 2 | **Sun binning + visibility cache** | **Opus** | **High** |
| 3 | Perez sky + view factors | **Opus** | Medium |
| 4 | Per-aperture irradiance | **Opus** | Medium |
| 5 | Desirability weighting | **Opus** | Medium |
| 6 | **Shading potential field** | **Opus** | **High** |
| 7 | Isosurface extraction | Sonnet | Medium |
| 8 | **Rationalisation to buildable** | **Opus** | Medium–High |
| 9 | Optimisation (pattern search, NSGA-II) | Sonnet | Medium |
| 10 | Grasshopper components | Sonnet | Medium |
| 11 | Validation + assumptions register | Sonnet (+Opus for the register) | Medium |

**Rule of thumb.** Opus at high effort for the two stages that invent something (2 and 6) and the
stages where a silent wrong answer is plausible (0, 3, 4, 5, 8). Sonnet for the stages with a clear
template or a textbook algorithm (1, 7, 9, 10, 11).

---

## 6. Sequencing

**Slice 1 — useful on its own (Stages 0–4).** Per-aperture irradiance, any period, interactive.
Ship it, use it, validate it. Nothing about shading design yet, and it is already worth having.

**Slice 2 — the design tool (Stages 5–8).** Desirability, the potential field, the ideal shape, the
rationalised device. Prototype Stage 6 on a single synthetic south-facing window with no context
before running it on a real model — the analytical profile-angle check in the Stage 6 prompt is the
gate.

**Slice 3 — polish (Stages 9–11).** Optimisation, components, validation.

**Do not start Stage 6 until Stage 2's binning bias is measured and accepted.** The field is built on
the cache; if the cache is biased, the shape is wrong in a way that looks entirely reasonable.

---

## 7. Risks, and what is still missing

| Risk | Mitigation |
|---|---|
| **No thermal load model.** "Unwanted sun" is approximated (Stage 5). Real Shaderade uses cooling-minus-heating load. | `IDesirabilityStrategy` is pluggable; Phase 2 reads TAS loads through the existing SAM_Tas link. Document the approximation in the register. |
| **Isotropic sky biases the weighting.** | Stage 3 (Perez) is scheduled *before* the field is built, deliberately. |
| **Binning approximation.** Sun-position bins introduce error. | Measured, not assumed — Stage 2 test 1 and Stage 11's bias study. Bin size is user-controllable. |
| **`Shell` boolean fragility.** | Designed out. Booleans are used only in Stage 7 for display and are allowed to fail (§2.2). |
| **SAM geometry fidelity** — flipped normals, degenerate faces, apertures lost on empty spaces. | Stage 0 resolves normals against the host panel and space; assert on every fixture. |
| **Cache staleness.** A silently stale cache gives a confidently wrong answer. | Geometry hash + a visible `cacheReused` output in Grasshopper (Stage 10). |
| **No inter-reflection.** Specular and diffuse bounce off context is ignored. | Same limitation as `LB Incident Radiation`; only full Radiance solves it. Documented, not hidden. |
| **Voxel memory.** Fine voxels × many apertures. | Voxel size is a parameter; the field is per-aperture and disposable. A 3 m × 3 m × 1.5 m volume at 50 mm is ~1.6 M voxels ≈ 13 MB — fine. Warn above a threshold. |

**Explicitly out of scope for Phase 1:** glare (DGP), daylight autonomy, thermal comfort, energy
demand, dynamic/movable shading control, and BIPV yield. The architecture does not preclude any of
them — the sun-bin cache is the right substrate for all of them — but none is attempted here.

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
