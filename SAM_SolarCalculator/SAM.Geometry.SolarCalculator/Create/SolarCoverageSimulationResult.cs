// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using SAM.Geometry.Object.Spatial;
using SAM.Geometry.Spatial;
using System;
using System.Collections.Generic;

namespace SAM.Geometry.SolarCalculator
{
    public static partial class Create
    {
        /// <summary>
        /// Builds a <see cref="SolarCoverageSimulationResult"/> from raw sun-exposure tuples.
        /// Coverage at each timestep is <c>sum(litFaceArea) / totalFaceArea</c>, clamped to [0, 1].
        /// </summary>
        /// <param name="linkedFace3D">The exposed face whose total area is the denominator.</param>
        /// <param name="sunExposure">Per-timestep tuple of (DateTime, Radiation, lit Face3Ds).</param>
        /// <param name="name">Optional name; defaults to the LinkedFace3D's Reference (or null).</param>
        public static SolarCoverageSimulationResult SolarCoverageSimulationResult(this LinkedFace3D linkedFace3D, IEnumerable<Tuple<DateTime, Radiation, List<Face3D>>> sunExposure, string name = null)
        {
            if (linkedFace3D == null)
            {
                return null;
            }

            return SolarCoverageSimulationResult(linkedFace3D.Guid, linkedFace3D.Face3D, sunExposure, name);
        }

        public static SolarCoverageSimulationResult SolarCoverageSimulationResult(this Guid guid, Face3D face3D, IEnumerable<Tuple<DateTime, Radiation, List<Face3D>>> sunExposure, string name = null)
        {
            if (face3D == null || sunExposure == null)
            {
                return null;
            }

            double totalArea = face3D.GetArea();
            if (totalArea <= 0)
            {
                return null;
            }

            List<Tuple<DateTime, float>> coverage = new List<Tuple<DateTime, float>>();
            foreach (Tuple<DateTime, Radiation, List<Face3D>> tuple in sunExposure)
            {
                if (tuple == null)
                {
                    continue;
                }

                double litArea = 0;
                if (tuple.Item3 != null)
                {
                    foreach (Face3D face3D_Lit in tuple.Item3)
                    {
                        if (face3D_Lit == null)
                        {
                            continue;
                        }

                        litArea += face3D_Lit.GetArea();
                    }
                }

                float c = (float)Math.Max(0.0, Math.Min(1.0, litArea / totalArea));
                coverage.Add(Tuple.Create(tuple.Item1, c));
            }

            return new SolarCoverageSimulationResult(name, Query.Source(), guid.ToString(), coverage);
        }

        /// <summary>
        /// Converts an existing <see cref="SolarFaceSimulationResult"/> into a lighter
        /// <see cref="SolarCoverageSimulationResult"/>. Useful when you already ran a face
        /// simulation and want the coverage view for comparison against TAS-imported data.
        /// </summary>
        public static SolarCoverageSimulationResult SolarCoverageSimulationResult(this SolarFaceSimulationResult solarFaceSimulationResult)
        {
            if (solarFaceSimulationResult == null)
            {
                return null;
            }

            Face3D face3D = solarFaceSimulationResult.Face3D;
            if (face3D == null)
            {
                return null;
            }

            double totalArea = face3D.GetArea();
            if (totalArea <= 0)
            {
                return null;
            }

            List<DateTime> dateTimes = solarFaceSimulationResult.DateTimes;
            if (dateTimes == null)
            {
                return null;
            }

            List<Tuple<DateTime, float>> coverage = new List<Tuple<DateTime, float>>(dateTimes.Count);
            foreach (DateTime dateTime in dateTimes)
            {
                double litArea = solarFaceSimulationResult.GetSunExposureArea(dateTime);
                float c = (float)Math.Max(0.0, Math.Min(1.0, litArea / totalArea));
                coverage.Add(Tuple.Create(dateTime, c));
            }

            return new SolarCoverageSimulationResult(
                solarFaceSimulationResult.Name,
                solarFaceSimulationResult.Source,
                solarFaceSimulationResult.Reference,
                coverage);
        }
    }
}
