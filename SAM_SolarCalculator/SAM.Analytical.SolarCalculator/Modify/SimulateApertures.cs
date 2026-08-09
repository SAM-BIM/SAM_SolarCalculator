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
        /// geometry, cell size, bin resolution or tolerances rebuilds.
        ///
        /// The period is re-rooted to the weather data's year when they differ (hour-of-year
        /// structure is preserved). Weather values are read on the weather-timeline hour; the sun
        /// position is evaluated at hour + timeShiftInMinutes (-30 matches TAS EDSL).
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
        /// <param name="timeShiftInMinutes">Sun-position time shift, minutes.</param>
        /// <param name="minHorizonAngle">Minimum sun altitude, RADIANS.</param>
        public static List<ApertureIrradianceResult> SimulateApertures(this AnalyticalModel analyticalModel, AnalysisPeriod analysisPeriod, out bool cacheReused, IEnumerable<Guid> apertureGuids = null, double cellSize = 0.5, SkyModel skyModel = SkyModel.PerezAnisotropic, double binSizeDegrees = 2.0, bool rebuildCache = false, double albedo = 0.2, double timeShiftInMinutes = 0, double minHorizonAngle = Core.Tolerance.Angle, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance)
        {
            cacheReused = false;

            if (analyticalModel == null || analysisPeriod == null)
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
            List<Face3D> cellFaces = new List<Face3D>();
            foreach (ApertureSolarTarget target in targets)
            {
                List<AnalysisCell> targetCells = target.AnalysisCells;
                Vector3D outward = target.OutwardNormal;
                foreach (AnalysisCell cell in targetCells)
                {
                    cells.Add(cell);
                    cellNormals.Add(outward);
                    cellFaces.Add(cell?.Face3D);
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

            string geometryHash = Geometry.SolarCalculator.Query.GeometryHash(occluders, cellFaces, tolerance_Distance);

            // Reuse the caches on the attached SolarModel only when every identity input matches.
            SolarModel solarModel = analyticalModel.GetValue<SolarModel>(AnalyticalModelParameter.SolarModel);
            SolarVisibilityCache solarVisibilityCache = rebuildCache ? null : solarModel?.GetValue<SolarVisibilityCache>(SolarModelParameter.SolarVisibilityCache);
            SkyVisibilityCache skyVisibilityCache = rebuildCache ? null : solarModel?.GetValue<SkyVisibilityCache>(SolarModelParameter.SkyVisibilityCache);

            if (solarVisibilityCache != null && !solarVisibilityCache.Matches(geometryHash, cellSize, binSizeDegrees, minHorizonAngle, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance, location.Latitude, location.Longitude, year, cells.Count))
            {
                solarVisibilityCache = null;
            }

            if (skyVisibilityCache != null && !skyVisibilityCache.Matches(geometryHash, cellSize, SkyPatchSubdivision.Tregenza145, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance, cells.Count))
            {
                skyVisibilityCache = null;
            }

            if (solarVisibilityCache == null || skyVisibilityCache == null)
            {
                solarVisibilityCache = Weather.SolarCalculator.Create.SolarVisibilityCache(location, year, binSizeDegrees, occluders, cells, cellSize, minHorizonAngle, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance);
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

            CachedIrradianceResult cachedIrradianceResult = Query.CachedIrradiance(solarVisibilityCache, skyVisibilityCache, weatherData, analysisPeriod, cellNormals, skyModel, albedo, timeShiftInMinutes, minHorizonAngle);
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
                    cachedIrradianceResult.DateTimes);

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
