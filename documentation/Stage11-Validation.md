# Stage 11 — Validation status and assumptions register

**Scope:** the Phase-1 aperture-level shading and direct-solar workflow, Stages 0–10.1.
Stage 8.1 (internal solar penetration) is deferred and not covered here.

---

## 0. How to read this document

Two audiences, two sections.

- **§2 Validation status** — what has been measured, with the numbers. Read this to know whether the
  tool works.
- **§3 Assumptions register** — every approximation, its magnitude and its *direction*. Read this to
  know whether the tool is fit for **your** job. A number from this workflow does not belong in a
  report unless the assumptions below are acceptable for that report.

**Provenance of the figures.** Every number in §2 is quoted from the stage method documents, as
recorded on the build each was committed with. They were **not** re-measured while writing this
register. If a change moves a number, update it at source in the stage document *and* here. Do not
keep a stale baseline for comparison.

> ### ⚠ The test suite is not executed by CI
>
> `.github/workflows/build.yml` runs `msbuild /t:Rebuild` and nothing else. **There is no
> `dotnet test` step in any workflow**, and the only configuration built is `Release`
> (`/p:Configuration=Release`).
>
> Therefore a green `build` check means **the solution compiles, including the test project**. It
> does **not** mean any test passed. It is exactly strong enough to catch what it caught on
> `2bd94c8` — a test calling a method that does not exist — and no stronger.
>
> Consequently, as of this document: **no test in this repository has been verified to pass by CI**,
> and the figures in §2 rest on runs performed by the sessions that recorded them, which cannot be
> re-verified from here. Adding a test step to CI is the single highest-value validation action
> available, and it is listed first in §5.

---

## 1. What was built

```
AnalyticalModel (+ WeatherData)
  Stage 0   Create.ApertureSolarTargets        outward-normal-resolved aperture targets
            Convert.ToSAM_OccluderLinkedFace3Ds  context (panels cut at apertures + shades)
  Stage 1   AnalysisPeriod                     hour selection on the weather timeline
  Stage 2   Create.SunBins / SolarVisibilityCache   geometry × sun-group lit bitsets
  Stage 3   Create.SkyVisibilityCache          per-cell SVF / horizon / ground visibility, Perez sky
  Stage 4   Query.CachedIrradiance             arithmetic re-weighting → kWh/m² per cell
            Modify.SimulateApertures           orchestration + AddResult<Aperture>
  Stage 5   desirability weighting             signed wanted/unwanted direct solar
  Stage 6   ShadingPotentialField              voxel benefit field (DDA traversal)
  Stage 7   ideal shading geometry             marching tetrahedra
  Stage 8   RationaliseShading / VerifyShading first-hit attribution, per-element performance
            SolarAttributionCache              built only for candidate devices
  Stage 9   optimisation engine                typology fitting, scalar objective
  Stage 10  Grasshopper components             + Stage 10.1 multi-aperture handling
```

The two-cache split (§2.5 of the plan, as amended) is the load-bearing design decision:
`SolarVisibilityCache` is a compact shared bitset reused across every aperture and candidate;
`SolarAttributionCache` carries first-hit element identity and is built only where a candidate device
needs it.

---

## 2. Validation status

### 2.1 The central architectural claim — **validated**

The plan's core bet (§2.1) was that grouping sun positions makes period selection a pure re-weighting,
so changing season/day/period costs no geometry. Measured on ModelB (13 apertures, 135 cells @ 0.5 m,
36 occluders):

| operation | measured |
|---|---:|
| first call — build both caches + evaluate a full year | 2607 ms |
| second call — different `AnalysisPeriod`, caches reused | **298 ms** |

**~8.7× on period change, with no geometric recomputation.** Reuse behaves as specified:
`AnalysisPeriod` change → reused; `WeatherData` swap → reused; `gridSize` change → rebuild;
`recalculate: true` → rebuild; `SunTimeConvention` change → rebuild.

### 2.2 Sun-group quantisation bias — **within gate**

Synthetic south window + overhang, 4186 daylight hours, per-hour against the exact sampled
`Simulate_Coverage` baseline:

| `sunAngleStep` | groups | build (ms) | MAE | DNI-weighted MAE |
|---:|---:|---:|---:|---:|
| 1° | 1336 | 1557 | 0.0125 | 0.0116 |
| **2° (default)** | **666** | **1587** | **0.0154** | **0.0145** |
| 5° | 240 | 1340 | 0.0293 | 0.0298 |

The 2° default sits at **1.54 % MAE**, inside the plan's 2 % gate. Two honest observations:

- **Build time does not fall linearly with group count** on a model this small — it is dominated by
  the per-group geometric pass, so 5° buys accuracy loss for little speed. The default is the right
  place to sit; going coarser is not obviously worth it.
- The 1° row shows the residual is not purely quantisation — halving the step does not halve the
  error, so some of the 1.25 % floor is baseline discretisation, not grouping.

### 2.3 Regression gate — **holds**

`WithShade_SAM_matches_TAS_within_tolerance`: 36 surfaces matched 1:1, 5371 overlapping hours,
**mean absolute delta 0.00880** against a gate of 0.02. The existing SAM-vs-TAS coverage benchmark is
unmoved by the new radiation path — the workflow was added alongside the validated engine, not
through it.

### 2.4 Component-level checks recorded

- Tregenza-145 quadrature reproduces analytic view factors to **~0.5 %**; can slightly exceed 1.0 for
  an unobstructed up-facing cell.
- Evaluated-hour conservation across sun-position shifts (T1); east/west symmetry with demonstrated
  detection power (T2); corrected standalone radiation vs cached irradiation (T3); cache identity
  (T4).
- Genuine DNI confirmed in `DirectSolarRadiation` (B6).
- ModelB annual per-aperture averages recorded for 13 apertures at 0.5 m grid.

### 2.5 Gates 1–2 — independent and analytical validation (added, **not executed**)

Gate 1 (`IndependentReferenceTests`) compares SAM against **Ladybug Tools**' `ladybug.sunpath.Sunpath`,
run **once offline** by `Fixtures/Reference/generate_reference.py` into a committed
`reference-results.json`. Nothing here runs Python; Ladybug is not a build, test or runtime dependency.
This is the "borrow the validation without inheriting the dependency" approach the plan called for.

It is careful about what it actually proves, which matters more than the headline:

| | |
|---|---|
| **Independent** | Solar position — Ladybug's own declination, equation of time and hour angle. |
| **Independent** | Transposition geometry and annual accumulation — `cos(incidence)` recomputed from Ladybug's sun vectors and summed separately. |
| **Shared by design** | The GHI/DHI series, exported from the fixture so both sides read byte-identical radiation. Two different weather files would measure the files. |
| **Shared by design** | The DNI decomposition rule — SAM's documented modelling choice. Its low-sun clamp is *measured* separately rather than validated against itself. |
| **Not covered** | Diffuse and ground-reflected transposition. Ladybug's Python API exposes decomposition (DISC/DIRINT), not Perez transposition onto a tilted surface. |

Asserted thresholds (read from source, **not** observed results): sun altitude and azimuth MAE
< 0.25°, max absolute < 1.0°, over all 8760 hours.

Gate 2 (`AnalyticalValidationTests`) adds closed-form checks: NOAA sun position at solstices and
equinoxes; solar-noon altitude against the declination identity; fractional time-zone offset; the
cosine law on an unobstructed surface; an aperture facing away admits nothing; **an overhang shading
exactly to the profile-angle construction**; an unobstructed vertical surface seeing half the sky;
first-element-reached attribution; and context obstruction never being credited to a device.

`ConvergenceStudyTests` and `ResolutionConvergenceTests` add the grid- and voxel-convergence work,
including the minimum-feature-size rule and the field's spatial stability under refinement.

### 2.6 Gate 7 — the optimality gap is **materially larger than the gate allows** ⚠

The first execution of this gate, once an invalid ceiling assertion was removed (it compared the
search against a five-times-coarser enumeration and required the search never to win), measured the
bounded search against exhaustive enumeration on 12 family/objective combinations:

| family | λ | optimiser | enumerated best | gap % | evaluated / enumerated |
|---|---:|---:|---:|---:|---:|
| Overhang | 0.5 | 147.495 | 133.974 | **−10.09** | 75 / 1500 |
| Overhang | 1 | 138.639 | 122.680 | **−13.01** | 69 / 1500 |
| Overhang | 2 | 114.797 | 121.617 | 5.61 | 66 / 1500 |
| HorizontalLouvres | 0.5 | 154.043 | 148.675 | **−3.61** | 53 / 1080 |
| HorizontalLouvres | 1 | 148.444 | 146.381 | **−1.41** | 53 / 1080 |
| HorizontalLouvres | 2 | 89.905 | 146.381 | **38.58** | 49 / 1080 |
| VerticalFins | 0.5 | 4.090 | 5.071 | 19.35 | 42 / 1800 |
| VerticalFins | 1 | 3.634 | 5.071 | 28.34 | 41 / 1800 |
| VerticalFins | 2 | 3.634 | 5.071 | 28.34 | 41 / 1800 |
| EggCrate | 0.5 | 82.952 | 115.743 | 28.33 | 40 / 600 |
| EggCrate | 1 | 74.013 | 111.762 | 33.78 | 40 / 600 |
| EggCrate | 2 | 60.950 | 103.798 | **41.28** | 38 / 600 |

**Mean 16.29 %, worst 41.28 %**, matched or beat the coarse lattice in 4 of 12. Negative = the search
found a better point than the coarser lattice contains. The gate's own thresholds are worst < 10 %
and mean < 3 %, so **it fails, and the threshold must not be relaxed to make it pass.**

**Mechanism, from the numbers.** `Optimise.ShadingTypology` runs a coarse lattice at
`coarseLevels = 3` samples per free parameter, then compass refinement from the single best coarse
point, under `maximumEvaluations = 400`. Only **38–75 evaluations are actually used** — between 10 %
and 19 % of the permitted budget. Two patterns identify the failure:

- Where the response is close to unimodal (Overhang and HorizontalLouvres at λ = 0.5 and 1) the
  search **beats** the coarse enumeration. The refinement works.
- The gap grows with **λ**, the wanted-solar penalty, and with the number of interacting parameters
  (EggCrate, VerticalFins). At λ = 2 every family is at its worst.

That is basin-lock. The method's own documentation says the coarse phase exists to prevent it — *"the
depth response of a real device is not unimodal once counts and tilts are in play"* — and three levels
per parameter is too sparse to locate the right basin once the objective stiffens.

**The coarse phase is a full Cartesian lattice** (`Lattice` is an odometer over the free parameters),
so sparseness, not structure, is the problem: `fraction = counter / (levels - 1)` means three levels
samples each parameter only at its **minimum, midpoint and maximum**. A depth bounded 0–1.5 m is tried
at 0, 0.75 and 1.5 m and nowhere between; compass refinement then descends from whichever of those
corners scored best.

**Two candidate fixes, neither yet attempted** — both change product behaviour and must be *measured*
on this gate rather than assumed:

1. **Multi-start refinement.** Refine from the best *k* coarse points instead of only the best one.
   This attacks basin-lock directly and fits the unspent budget, without growing the coarse grid.
2. **Raise `coarseLevels`.** Note this does not scale freely: at 5 levels a three-parameter family
   costs 125 coarse points and a four-parameter one 625, which exceeds the 400 budget outright. So it
   helps the narrow families and stalls the wide ones — which are precisely EggCrate and VerticalFins,
   the worst performers. Option 1 is the better first experiment.

**Consequence for reporting now.** Until this is closed, the wording rule in A17 is not a stylistic
preference but a requirement: the search returns *best found within a bounded deterministic search*,
and on EggCrate and VerticalFins that has been measured up to 41 % below the best point in a lattice
coarser than its own.

### 2.7 Multi-start refinement — measured. Large improvement, **gate still fails**

Multi-start refinement (`64b1370`): phase 1 keeps every coarse point, the best 5 distinct are ranked
and each gets its own compass descent, global winner under the existing total order. Coarse lattice,
budget, `IsBetter`, NO SHADE semantics and the gate thresholds all unchanged.

| family | λ | gap % before | gap % after | evals before | evals after |
|---|---:|---:|---:|---:|---:|
| Overhang | 0.5 | −10.09 | −12.36 | 75 | 140 |
| Overhang | 1 | −13.01 | −13.82 | 69 | 158 |
| Overhang | 2 | +5.61 | **−5.46** | 66 | 125 |
| HorizontalLouvres | 0.5 | −3.61 | −3.61 | 53 | 81 |
| HorizontalLouvres | 1 | −1.41 | −1.41 | 53 | 99 |
| HorizontalLouvres | 2 | **+38.58** | **−4.27** | 49 | 91 |
| VerticalFins | 0.5 | +19.35 | **0** | 42 | 67 |
| VerticalFins | 1 | +28.34 | **0** | 41 | 74 |
| VerticalFins | 2 | +28.34 | **0** | 41 | 74 |
| EggCrate | 0.5 | +28.33 | +28.33 | 40 | 63 |
| EggCrate | 1 | +33.78 | +33.78 | 40 | 60 |
| EggCrate | 2 | +41.28 | +41.28 | 38 | 56 |

| | before | after | gate |
|---|---:|---:|---|
| mean gap | 16.291 % | **5.204 %** | < 3 % — still fails |
| worst gap | 41.28 % | **41.28 %** | < 10 % — still fails |
| matched or beat | 4 / 12 | **9 / 12** | — |
| evaluations | 38–75 | 56–158 | ≤ 400 budget |

**The change engaged everywhere** — evaluations rose on all twelve — and basin-lock was real: mean gap
fell by 68 %, VerticalFins now lands *exactly* on the enumerated best at every λ, and the two worst
non-EggCrate cells (HorizontalLouvres λ=2 at +38.58 %, Overhang λ=2 at +5.61 %) both crossed to
*beating* the coarse lattice. **This is not a claim that the problem is fixed: the gate fails.**

**EggCrate did not move at all** — 82.952 / 74.013 / 60.95, bit-identical to single-start, while its
evaluation count rose from 38–40 to 56–63. Five distinct starts were refined and none beat the
original winner. It alone now sets the worst case, and it is the only reason the worst-case threshold
still fails.

**Leading hypothesis, not yet tested and not acted on.** EggCrate is the only family whose parameters
are strongly coupled: `Depth` [0.05, 2.0] is shared by *both* the louvre array and the fin array,
alongside `LouvreCount` and `FinCount` [1, 16]. Compass search probes **one axis at a time** and
accepts the first improvement; where progress requires changing depth *and* a count together, every
single-axis probe fails, the step halves, and the descent terminates at a point no individual move can
leave. Multi-start cannot rescue that — it changes where descents *begin*, not the moves available to
them. The evidence fits: the two-parameter families (Louvres, Fins) were fully repaired, the
three-parameter coupled one was untouched.

If that is right, the next experiment is a **move set**, not more starts or a finer lattice: allow
compound probes (e.g. pattern-search moves over parameter pairs) so a coordinated step is reachable.
Recorded as a hypothesis; no further change made pending direction.

### 2.6 What remains **not** validated

Stated plainly, because it bounds what may be claimed:

- **No test has been verified to pass.** See the warning in §0 — CI compiles and does not run the
  suite, and this container has no .NET toolchain. Gates 1–2 exist as *code*; their results are
  unobserved from here.
- **Diffuse and ground-reflected transposition have no independent reference** (Gate 1, "not
  covered"). The Perez implementation is checked for internal consistency and against analytic view
  factors, not against another tool's tilted-surface diffuse.
- **No full-Radiance annual run** for any typology.
- **The desirability weighting is unvalidated against thermal outcome** — necessarily, since there is
  no load model (§3, A1). Optimised devices are optimal *against the stated proxy*, not against energy.
- **Debug configuration is never built.** CI builds `Release` only, so Debug-only failures
  (assertions, `#if DEBUG` paths, different overflow behaviour) would not be caught.

---

## 3. Assumptions register

The register an engineer needs to decide fitness for purpose. **Direction** states which way the
result is biased: *under* = the tool reports less than reality, *over* = more.

### 3.1 Physics not modelled

| # | Assumption | Magnitude | Direction | Where it bites |
|---|---|---|---|---|
| A1 | **No thermal load model.** "Unwanted sun" is a season/temperature/irradiance proxy, not cooling-minus-heating load as in Shaderade. | Unquantified — depends entirely on the proxy chosen | Either | The generated shape. This is the largest single modelling assumption in the workflow. |
| A2 | **No inter-reflection.** Context blocks, never bounces. No shade material reflectance, no secondary rays. | Unquantified; largest in dense urban canyons and near high-albedo surfaces | **Under** (missing bounced gain) | Absolute irradiance. Intercepted solar must **never** be called "reflected". |
| A3 | **No glazing transmittance or SHGC.** Direct solar is evaluated at the aperture *plane*. | A typical double-glazed unit transmits ~0.5–0.7 of incident at normal incidence, less off-normal | **Over**, substantially, if read as transmitted gain | Any figure quoted as solar *entering the space*. These are **incident** figures. |
| A4 | **Diffuse is not in the shading objective.** Desirability is direct-beam only. | Unquantified | **Under**-values deep devices, which also cut diffuse | Optimiser output — it may select a shallower device than a total-irradiance objective would. |
| A5 | **Isotropic ground reflection**, cosine-weighted patch sum. | Small on vertical façades | Either | Ground-reflected component only. |

### 3.2 Discretisation

| # | Assumption | Magnitude | Direction | Where it bites |
|---|---|---|---|---|
| A6 | **Sun-group quantisation** at `sunAngleStep` 2°. | **1.54 % MAE** measured (§2.2) | Roughly unbiased — the representative direction is the irradiance-weighted mean of member hours | All results. Reduce to 1° for final runs (0.0125 MAE). |
| A7 | **Analysis grid** at `gridSize`, default 0.5 m. | Device fineness is capped by it | **Under**-attributes elements narrower than `gridSize` | Per-element contribution split. Aggregate totals stay correct. |
| A8 | **Voxel size** in the potential field, 0.05–0.25 m in tests. | Caller-set | Either | Ideal-shape resolution. |
| A9 | **Hourly time resolution only.** `Timestep != 1` throws rather than being silently ignored. | Up to ±30 min mis-timing of a shading cutoff | Either | Cutoff-time-sensitive results, e.g. a device sized to a specific hour. |
| A10 | **`minHorizonAngle` 2° gate** skips near-horizon hours whole, including their diffuse and ground-reflected energy. | **0.19 % of annual GHI** measured on ModelB | **Under** | Annual totals. Negligible. |
| A11 | **Tregenza-145 quadrature** for view factors. | ~0.5 % vs analytic; can slightly exceed 1.0 for an unobstructed up-facing cell | Either | SVF/GVF, hence diffuse. |

### 3.3 Scope and geometry

| # | Assumption | Magnitude | Direction | Where it bites |
|---|---|---|---|---|
| A12 | **Each aperture optimised independently** — a device does not shade its neighbour. | Grows with device depth and aperture density | **Under**-estimates shading in dense arrays | Façades of closely spaced windows with deep devices. |
| A13 | **Interior partitions excluded from context.** A neighbouring building must be modelled as shade panels. Apertures on two-space panels are rejected even when explicitly selected. | Total, where it applies | **Over**-estimates irradiance if context was expected to be picked up automatically | Models relying on adjacent-building geometry being inferred. |
| A14 | **Four device families.** No light shelves, external roller blinds, or operable/seasonal devices. | — | — | Applicability. |
| A15 | **Perforated / translucent screens unsupported** — the ray engine is binary. | — | — | Applicability. |
| A16 | **Single scalar objective.** No Pareto front; λ and μ chosen up front. | — | — | Optimisation. Trade-offs are fixed before the search, not explored after. |
| A17 | **Local optimality on the search lattice.** Multi-start refinement mitigates but does not eliminate it. | **Measured: mean 5.2 %, worst 41.3 % below the enumerated best** (§2.7; was 16.3 % / 41.3 % single-start) | **Under** — the recommended device can be materially worse than the family's best | Now concentrated in **EggCrate**, where it reaches 41.3 %. Louvres and Fins are at or better than the coarse enumeration. "Optimal" means best-found, not proven-global — see §2.7. |
| A18 | **`MaterialFraction` is area only** — no thickness, weight, fixing or cost. | — | **Under**-states real buildability cost | Any material/cost trade-off. |
| A19 | **A valid, resolvable TimeZone is required.** | — | — | Fails loudly, by design. |
| A20 | **The ideal shape's mesh is display geometry**, not performance truth. | — | — | Anyone measuring off the mesh instead of running `VerifyShading`. |

---

## 4. Fitness for purpose

**Reasonable to use for:**

- Comparing shading options against each other on the same model — relative ranking is far more robust
  than absolute magnitude, and most assumptions above bias all options in the same direction.
- Sizing external shading geometry, where the profile-angle physics dominates.
- Identifying which façades and apertures need shading attention.
- Per-element contribution reporting, as **direct contribution** — with A7 in mind.

**Not currently defensible for:**

- Quoting absolute solar gain entering a space (A3 — these are incident, not transmitted).
- Energy or carbon figures (A1 — no load model).
- Overheating compliance (TM52/TM59) or any assessment needing thermal outcome.
- Daylight, glare (DGP) or visual comfort.
- Dense arrays of deep devices without checking A12 by hand.

**The two that most often get misread**, worth stating to anyone receiving output: incident is not
transmitted (A3), and direct contribution is not removal-loss (per §2.5 of the plan — each blocked ray
has exactly one first hit, so contributions sum to the system total by construction, but removing an
overhang may simply expose the fin behind it).

---

## 5. Recommended next validation work

In priority order, with the reasoning:

1. **Add a test step to CI.** `.github/workflows/build.yml` compiles and stops. Adding
   `dotnet test SAM_SolarCalculator/SAM_SolarCalculator.Tests` after the Rebuild step converts a
   large, careful test suite from *written* to *enforced*. Everything below is worth less until this
   exists — Gates 1–2 currently guarantee only that they compile. **This is the single highest-value
   action available**, and it is a few lines of YAML.
2. **Build Debug as well as Release** in CI, so Debug-only failures are caught.
3. **Record observed results, not just thresholds.** The gates assert bounds (e.g. sun-position MAE
   < 0.25°). Emitting the achieved values to test output, and pasting them into §2, turns a pass/fail
   into a trend that can be watched for drift.
4. **An independent reference for diffuse transposition** — the one gap Gate 1 explicitly does not
   cover. Ladybug's Python API does not expose Perez tilted-surface transposition, so this needs a
   different reference (a Radiance `gendaymtx` run, or published Perez validation data).
5. **Quantify A12** — run two adjacent apertures with deep devices independently and together, and
   record the difference. It is the one scope limitation likely to bite on a real façade.
6. **Quantify A2** on an urban-canyon fixture, even roughly, so the largest unquantified physics
   assumption has an order of magnitude.
7. **A3 worked example** — the same aperture reported as incident and as transmitted through a stated
   construction, so the factor is concrete for anyone reading a result.

---

## 6. Deferred capabilities

Recorded so they are not mistaken for oversights:

- **Stage 8.1 — internal solar penetration** (direct solar reaching floors/walls, and the reduction
  due to shading). Deferred, out of Phase-1 scope. The Stage 8 attribution structures are designed so
  this is **additive** — a second attribution cache over the interior geometry set — rather than a
  redesign. When built, geometric penetration should stay the fundamental quantity, with transmitted
  solar added as a separate construction-aware figure rather than silently replacing it.
- **Glazing angular transmittance / SHGC** (A3).
- **Marginal contribution** — per-element performance recomputed with one element removed, the honest
  measure where elements overlap.
- **Multi-objective optimisation** with a Pareto front (A16).
- **Inter-reflection** (A2), glare, daylight autonomy, thermal comfort, energy demand, BIPV yield.

---

## 7. Running the suite

```bash
dotnet test SAM_SolarCalculator/SAM_SolarCalculator.Tests
```

Green means the validated behaviour still holds.

**CI does not do this.** The `build` workflow compiles (`msbuild /t:Rebuild`, `Release` only) and has
no test step — see the warning in §0 and item 1 of §5. Until that changes, the command above must be
run by hand, and a green PR is not evidence that any test passed.

**A trap when reproducing the SPDX check locally:** it greps for the copyright line with the character
class `[-–]`, and the repo's headers use an en-dash. Under an unset or `C` locale that class is
byte-oriented and cannot match the three-byte UTF-8 en-dash, so **every correctly-headered file
appears to fail**. Run it under a UTF-8 locale, or the output is noise.
