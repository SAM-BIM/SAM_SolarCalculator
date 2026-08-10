# Stages 0–4 implementation — method, conventions and measurements

Implementation record for the per-aperture irradiance vertical slice of
`ShadingOptimisation-Plan.md` (Stages 0–4). This document is the formulation reference; the full
validation suite and assumptions register are Stage 11 scope.

## 1. Architecture

```
AnalyticalModel (+ WeatherData)
   └─ Create.ApertureSolarTargets            Stage 0  outward-normal-resolved aperture targets
   └─ Convert.ToSAM_OccluderLinkedFace3Ds             context (panels cut at apertures + shades)
   └─ Create.SunBins / SolarVisibilityCache  Stage 2  geometry x sun-bin lit bitsets
   └─ Create.SkyVisibilityCache              Stage 3  per-cell SVF / horizon / ground visibility
   └─ AnalysisPeriod                         Stage 1  hour selection (weather timeline)
   └─ Query.CachedIrradiance                 Stage 4  arithmetic re-weighting -> kWh/m2 per cell
   └─ Modify.SimulateApertures               Stage 4  orchestration + AddResult<Aperture>
```

The two caches are stored on the attached `SolarModel` under
`SolarModelParameter.SolarVisibilityCache` / `SkyVisibilityCache` and reused whenever their identity
matches. Changing the `AnalysisPeriod` or the weather file performs **no** geometric recomputation.

## 2. Timelines and the time shift

- Weather values are read at the weather-timeline hour `h` (`WeatherData.GetWeatherHour(h)`).
- The sun position (bin lookup, incidence angle, Perez coefficients) is evaluated at
  `h + timeShiftInMinutes`. `timeShiftInMinutes = -30` reproduces the TAS EDSL mid-interval
  convention used by the `_timeShift_` Grasshopper input. Default is `0` — the shift is always an
  explicit argument, never silently applied inside `AnalysisPeriod`.

## 3. Conventions (measured, not assumed)

Sun vectors (`Query.SunDirection`, `Create.SunDirection(altitude, azimuth)`): the vector points
**sun → surface**, so `Z < 0` when the sun is up. Azimuth is compass degrees clockwise from north
(+Y). `Query.TryGetSunAngles` is the exact inverse.

**Legacy isotropic formula** (`Geometry.SolarCalculator.Create.Radiation`): a runtime probe of the
released code shows it (a) assumes the plane normal points *into* the receiving side
(`tilt_Temp = 180 − tilt`) and (b) rotates the solar azimuth by +90°
(`solarAzimuth = (rad + π/2)·180/π`), which mis-assigns direct beam by one orientation on tilted
surfaces (a south wall at solar noon receives the east wall's beam). It also consumes SAM's
`CalculatedDirectSolarRadiation()` (= global − diffuse, i.e. beam on **horizontal**) as if it were
DNI. None of this affects the coverage benchmark, which never reads `Radiation`. **The legacy
signatures and behaviour are deliberately unchanged** (binary/behavioural compatibility); the
corrected conventions live in the new `SkyModel`-aware overloads and the cache evaluation:

- tilt = tilt of the **receiving (outward)** normal from horizontal (0 = up, 90 = vertical);
- surface azimuth = compass azimuth of the outward normal;
- DNI = `(GHI − DHI) / sin(elevation)`, capped at 5° elevation (SAM stores beam-horizontal; the EPW
  importer never fills the direct-normal field);
- solar azimuth used unrotated (NOAA clockwise from north).

## 4. Perez 1990 formulation with component-aware obstruction

Per hour `h`, per cell (Perez et al. 1990, Solar Energy 44(5), 271–289):

```
epsilon = ((DHI + DNI)/DHI + 1.041*theta_z^3) / (1 + 1.041*theta_z^3)     [theta_z in radians]
delta   = m * DHI / I0n,   m = Kasten-Young air mass, I0n = 1367*(1+0.033*cos(2*pi*doy/365))
F1 = max(0, f11 + f12*delta + f13*theta_z),  F2 = f21 + f22*delta + f23*theta_z   [8-bin table]
a = max(0, cos(thetaI)),   b = max(cos 85 deg, cos theta_z)

beam    = DNI * a * lit(cell, bin(h))
diffuse = DHI * [ (1-F1) * SVF(cell)                       <- isotropic component
                + F1 * (a/b) * lit(cell, bin(h))           <- circumsolar: sun-direction lit bit
                + F2 * sin(beta) * HVF(cell) ]             <- horizon: horizon-band visibility
ground  = GHI * albedo * GVF(cell)                         <- isotropic ground
```

- `SVF(cell)` — cosine-weighted sky view factor from the Tregenza-145 patch ray-cast
  (`(1/pi) * Σ_visible cos θ ω`); unobstructed vertical = 0.5 (measured 0.4979).
- `HVF(cell)` — cosine-weighted visible fraction of the lowest (0–12°) sky band, normalised to 1
  when the whole band is visible.
- `GVF(cell)` — cosine-weighted ground view factor over the mirrored ground dome.
- `lit(cell, bin)` — the direct-beam cache bit; also the circumsolar obstruction state.

`SkyModel.Isotropic` in the evaluation path uses `diffuse = DHI * SVF(cell)` (the same
obstruction-aware form factor; the legacy `cos^2(tilt/2)` value is recovered exactly for
unobstructed cells). Whole-hour integration: `W/m2 * 1 h / 1000 = kWh/m2`, applied exactly once;
aperture totals `kWh = Σ_cell kWh/m2 * cell area`; averages divide by the cell-covered area.

## 5. Cache identity (invalidation rules)

`SolarVisibilityCache.GetIdentity()` = schema version (1) + algorithm tag (`CellRaycast`) + geometry
hash + cell size + bin size + min horizon angle + area/snap/angle/distance tolerances + location
(lat/lon) + year + cell count. `SkyVisibilityCache` = schema version + `PatchRaycast` + patch
subdivision + geometry hash + cell size + tolerances + cell count (location-independent).

The **geometry hash** (`Query.GeometryHash`) is a SHA-256 over per-face, tolerance-rounded,
order-independent vertex sets of every occluder **and** every analysis cell.

Weather data and `AnalysisPeriod` are **not** part of either identity — a weather swap or a period
change reuses the geometry. Sun bins are solar geometry only: the representative direction is the
angular **bin centre** (no DNI weighting), so the direct-beam cache never depends on the weather
file.

## 6. Measured validation numbers (this build)

- SAM-vs-TAS coverage benchmark (`WithShade_SAM_matches_TAS_within_tolerance`): **passes** after the
  occlusion refactor (gate: overall mean abs delta < 0.02; historical live value ~0.0088).
- Binning bias vs exact per-hour sampled coverage (synthetic south window + overhang, 4186 daylight
  hours): **1° → 1336 bins, MAE 0.0125; 2° → 666 bins, MAE 0.0154 (within the 2% gate);
  5° → 240 bins, MAE 0.0293**. Exact baseline 1.8 s; cache build ~0.8 s.
- ModelB (13 apertures, 135 cells @0.5 m, 36 occluders): cache build + first evaluation 2.6 s;
  cached period re-evaluation 0.2 s.
- Annual per-aperture average (ModelB, attached weather): south 778.9, north 357.6, east 736.5,
  west 547.3 kWh/m²; shaded model total = 94.2% of unshaded.
- Conservation: complementary half-year periods sum to the full year within 1e-9 relative.
- Component isolation: small plate over the midday solstice sun — isotropic diffuse ratio 0.803,
  Perez diffuse ratio 0.598 (circumsolar term removed by the lit bit, not by SVF scaling).

## 7. Known limitations (Stage 0–4 scope)

- No inter-reflection between surfaces (context blocks, never bounces).
- Isotropic ground; ground view factor is a cosine-weighted patch sum.
- Hourly weather only (`AnalysisPeriod.Timestep` is reserved, only 1 supported).
- Bins are built per (location, year); a period outside the cache year drops unmatched edge bins.
- Interior partitions (panels shared by two spaces) are excluded from the context, as in the
  existing engine; a neighbouring building must be modelled as shade panels to occlude.
