// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using SAM.Core.SolarCalculator;
using SAM.Geometry.Object.Spatial;
using SAM.Geometry.SolarCalculator;
using SAM.Geometry.Spatial;
using SAM.Weather;
using SAM.Weather.SolarCalculator;
using System;
using System.Collections.Generic;

namespace SAM.Analytical.SolarCalculator
{
    public static partial class Modify
    {
        /// <summary>
        /// Per-aperture irradiance over any AnalysisPeriod, from the reusable visibility caches.
        ///
        /// Pipeline: aperture targets (Stage 0) -> occluder context -> sun-bin direct-beam cache +
        /// sky/horizon/ground cache (Stages 2-3, the expensive geometric pass) -> arithmetic period
        /// evaluation (Stage 4). The caches are stored on the SolarModel attached under
        /// AnalyticalModelParameter.SolarModel and reused whenever their identity matches: changing
        /// the AnalysisPeriod (or the weather data) performs NO geometric recomputation; changing
        /// geometry, cell size, bin resolution, location/timezone, sun-time convention or tolerances
        /// rebuilds.
        ///
        /// SunTimeConvention makes the timeline's timestamp convention explicit (it is NOT the TAS
        /// compatibility shift, which is the separate IntervalEnd case): the default,
        /// IntervalStart (+30 min), is the SAM/EPW weather timeline — the importer decrements the
        /// EPW hour field, so a SAM weather timestamp labels the START of its interval and the
        /// representative sun position is the interval midpoint. IntervalEnd (-30 min) reproduces
        /// the legacy "_timeShift_ = -30" TAS EDSL comparison behaviour. The bins are built from the
        /// shifted positions and the evaluation uses the shift recorded on the cache, so a
        /// convention change rebuilds rather than silently mismatching bins.
        ///
        /// The period is re-rooted to the weather data's year when they differ (hour-of-year
        /// structure is preserved). Weather values are read on the weather-timeline hour.
        /// </summary>
        /// <param name="analyticalModel">Model with WeatherData attached (AnalyticalModelParameter.WeatherData).</param>
        /// <param name="analysisPeriod">Hours to integrate.</param>
        /// <param name="cacheReused">True when both caches were reused (no geometric pass ran).</param>
        /// <param name="apertureGuids">Null/empty = all apertures on sun-exposed external panels.</param>
        /// <param name="cellSize">Analysis cell size, m.</param>
        /// <param name="skyModel">Diffuse sky model.</param>
        /// <param name="binSizeDegrees">Sun-bin angular resolution, degrees.</param>
        /// <param name="rebuildCache">Force a cache rebuild even when the identity matches.</param>
        /// <param name="albedo">Ground reflectance.</param>
        /// <param name="sunTimeConvention">Timestamp convention of the weather timeline (default IntervalStart = EPW/SAM, +30 min).</param>
        /// <param name="minHorizonAngle">Minimum sun altitude, RADIANS.</param>
        public static List<ApertureIrradianceResult> SimulateApertures(this AnalyticalModel analyticalModel, AnalysisPeriod analysisPeriod, out bool cacheReused, IEnumerable<Guid> apertureGuids = null, double cellSize = 0.5, SkyModel skyModel = SkyModel.PerezAnisotropic, double binSizeDegrees = 2.0, bool rebuildCache = false, double albedo = 0.2, SunTimeConvention sunTimeConvention = SunTimeConvention.IntervalStart, double minHorizonAngle = Core.Tolerance.Angle, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance)
        {
            double timeShiftInMinutes = sunTimeConvention.TimeShiftInMinutes();
            if (double.IsNaN(timeShiftInMinutes))
            {
                cacheReused = false;
                return null;
            }

            return SimulateApertures(analyticalModel, analysisPeriod, out cacheReused, apertureGuids, cellSize, skyModel, binSizeDegrees, rebuildCache, albedo, timeShiftInMinutes, minHorizonAngle, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance);
        }

        /// <summary>
        /// SimulateApertures with an explicit sun-position sampling offset in minutes (research use;
        /// prefer the SunTimeConvention overload so the timeline semantics are self-describing).
        /// The offset becomes part of the cache identity: bins are built from weather-hour + offset
        /// positions and the evaluation reads the offset back from the cache.
        /// </summary>
        public static List<ApertureIrradianceResult> SimulateApertures(this AnalyticalModel analyticalModel, AnalysisPeriod analysisPeriod, out bool cacheReused, IEnumerable<Guid> apertureGuids, double cellSize, SkyModel skyModel, double binSizeDegrees, bool rebuildCache, double albedo, double timeShiftInMinutes, double minHorizonAngle = Core.Tolerance.Angle, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance)
        {
            cacheReused = false;

            if (analyticalModel == null || analysisPeriod == null || double.IsNaN(timeShiftInMinutes))
            {
                return null;
            }

            WeatherData weatherData = analyticalModel.GetValue<WeatherData>(AnalyticalModelParameter.WeatherData);
            if (weatherData == null)
            {
                return null;
            }

            // Re-root the period to the weather year when they differ (HOY structure preserved).
            int year = analysisPeriod.Year;
            WeatherYear weatherYear = weatherData[year];
            if (weatherYear == null)
            {
                weatherYear = weatherData.WeatherYears?.Find(x => x != null);
                if (weatherYear == null)
                {
                    return null;
                }

                year = weatherYear.Year;
                analysisPeriod = ReRoot(analysisPeriod, year);
                if (analysisPeriod == null)
                {
                    return null;
                }
            }

            List<ApertureSolarTarget> targets = analyticalModel.ApertureSolarTargets(apertureGuids, cellSize, tolerance_Area, tolerance_Distance);
            if (targets == null || targets.Count == 0)
            {
                return null;
            }

            List<LinkedFace3D> occluders = Convert.ToSAM_OccluderLinkedFace3Ds(analyticalModel);
            if (occluders == null)
            {
                occluders = new List<LinkedFace3D>();
            }

            // Flatten the targets into the cache cell space.
            List<AnalysisCell> cells = new List<AnalysisCell>();
            List<Vector3D> cellNormals = new List<Vector3D>();
            foreach (ApertureSolarTarget target in targets)
            {
                List<AnalysisCell> targetCells = target.AnalysisCells;
                Vector3D outward = target.OutwardNormal;
                foreach (AnalysisCell cell in targetCells)
                {
                    cells.Add(cell);
                    cellNormals.Add(outward);
                }
            }

            if (cells.Count == 0)
            {
                return null;
            }

            Core.Location location = weatherData.Location ?? analyticalModel.Location;
            if (location == null)
            {
                return null;
            }

            string contextGeometryHash = Geometry.SolarCalculator.Query.GeometryHash(occluders, tolerance_Distance);
            string targetGeometryHash = Geometry.SolarCalculator.Query.TargetHash(cells, tolerance_Distance);
            double timeZoneOffset = Geometry.SolarCalculator.Query.TimeZoneOffset(location);

            // Reuse the caches on the attached SolarModel only when every identity input matches.
            SolarModel solarModel = analyticalModel.GetValue<SolarModel>(AnalyticalModelParameter.SolarModel);
            SolarVisibilityCache solarVisibilityCache = rebuildCache ? null : solarModel?.GetValue<SolarVisibilityCache>(SolarModelParameter.SolarVisibilityCache);
            SkyVisibilityCache skyVisibilityCache = rebuildCache ? null : solarModel?.GetValue<SkyVisibilityCache>(SolarModelParameter.SkyVisibilityCache);

            if (solarVisibilityCache != null && !solarVisibilityCache.Matches(contextGeometryHash, targetGeometryHash, cellSize, binSizeDegrees, minHorizonAngle, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance, location.Latitude, location.Longitude, timeZoneOffset, timeShiftInMinutes, year, cells.Count))
            {
                solarVisibilityCache = null;
            }

            if (skyVisibilityCache != null && !skyVisibilityCache.Matches(contextGeometryHash, targetGeometryHash, cellSize, SkyPatchSubdivision.Tregenza145, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance, cells.Count))
            {
                skyVisibilityCache = null;
            }

            if (solarVisibilityCache == null || skyVisibilityCache == null)
            {
                solarVisibilityCache = Weather.SolarCalculator.Create.SolarVisibilityCache(location, year, binSizeDegrees, occluders, cells, cellSize, minHorizonAngle, timeShiftInMinutes, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance);
                skyVisibilityCache = Weather.SolarCalculator.Create.SkyVisibilityCache(occluders, cells, cellSize, SkyPatchSubdivision.Tregenza145, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance);
                if (solarVisibilityCache == null || skyVisibilityCache == null)
                {
                    return null;
                }

                if (solarModel == null)
                {
                    solarModel = new SolarModel(location);
                    foreach (LinkedFace3D occluder in occluders)
                    {
                        solarModel.Add(occluder);
                    }

                    analyticalModel.SetValue(AnalyticalModelParameter.SolarModel, solarModel);
                }

                solarModel.SetValue(SolarModelParameter.SolarVisibilityCache, solarVisibilityCache);
                solarModel.SetValue(SolarModelParameter.SkyVisibilityCache, skyVisibilityCache);
            }
            else
            {
                cacheReused = true;
            }

            CachedIrradianceResult cachedIrradianceResult = Query.CachedIrradiance(solarVisibilityCache, skyVisibilityCache, weatherData, analysisPeriod, cellNormals, skyModel, albedo);
            if (cachedIrradianceResult == null)
            {
                return null;
            }

            double[] directAll = cachedIrradianceResult.Direct;
            double[] diffuseAll = cachedIrradianceResult.Diffuse;
            double[] groundAll = cachedIrradianceResult.GroundReflected;
            double[] sunlitAll = cachedIrradianceResult.SunlitHours;

            Dictionary<Guid, Aperture> aperturesByGuid = new Dictionary<Guid, Aperture>();
            List<Aperture> apertures = analyticalModel.GetApertures();
            if (apertures != null)
            {
                foreach (Aperture aperture in apertures)
                {
                    if (aperture != null)
                    {
                        aperturesByGuid[aperture.Guid] = aperture;
                    }
                }
            }

            List<ApertureIrradianceResult> result = new List<ApertureIrradianceResult>(targets.Count);
            int offset = 0;
            foreach (ApertureSolarTarget target in targets)
            {
                int count = target.CellCount;

                double[] cellAreas = new double[count];
                double[] direct = new double[count];
                double[] diffuse = new double[count];
                double[] ground = new double[count];
                double[] sunlit = new double[count];
                for (int i = 0; i < count; i++)
                {
                    cellAreas[i] = cells[offset + i].Area;
                    direct[i] = directAll[offset + i];
                    diffuse[i] = diffuseAll[offset + i];
                    ground[i] = groundAll[offset + i];
                    sunlit[i] = sunlitAll[offset + i];
                }
                offset += count;

                aperturesByGuid.TryGetValue(target.ApertureGuid, out Aperture aperture);

                ApertureIrradianceResult apertureIrradianceResult = new ApertureIrradianceResult(
                    aperture?.Name,
                    "SAM.Analytical.SolarCalculator",
                    target.ApertureGuid.ToString(),
                    analysisPeriod, skyModel, cellSize, binSizeDegrees, albedo, timeShiftInMinutes,
                    target.GrossArea, cellAreas, direct, diffuse, ground, sunlit,
                    cachedIrradianceResult.DateTimes,
                    cachedIrradianceResult.MissedBinHours, cachedIrradianceResult.BelowHorizonHours, cachedIrradianceResult.MissingWeatherHours);

                if (aperture != null)
                {
                    analyticalModel.AddResult<Aperture>(apertureIrradianceResult, aperture);
                }

                result.Add(apertureIrradianceResult);
            }

            return result;
        }

        private static AnalysisPeriod ReRoot(AnalysisPeriod analysisPeriod, int year)
        {
            if (analysisPeriod == null || analysisPeriod.Year == year)
            {
                return analysisPeriod;
            }

            List<int> explicitHours = analysisPeriod.ExplicitHoursOfYear;
            if (explicitHours != null)
            {
                return new AnalysisPeriod(year, explicitHours);
            }

            return new AnalysisPeriod(year, analysisPeriod.StartMonth, analysisPeriod.StartDay, analysisPeriod.EndMonth, analysisPeriod.EndDay, analysisPeriod.StartHour, analysisPeriod.EndHour, analysisPeriod.Timestep);
        }
    }
}
