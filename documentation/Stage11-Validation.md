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
| A17 | **Local optimality on the search lattice.** The coarse phase mitigates but does not eliminate multimodality. | — | — | "Optimal" means best-found, not proven-global. |
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
