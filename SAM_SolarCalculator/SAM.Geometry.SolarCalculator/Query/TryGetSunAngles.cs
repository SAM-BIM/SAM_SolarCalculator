// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using SAM.Geometry.Spatial;
using System;

namespace SAM.Geometry.SolarCalculator
{
    public static partial class Query
    {
        /// <summary>
        /// Decomposes a SAM sun-direction vector (points FROM the sun TOWARD the surface, so
        /// Z &lt; 0 while the sun is up) into solar altitude and compass azimuth. Inverse of
        /// Create.SunDirection(double, double).
        /// </summary>
        /// <param name="altitude">Solar altitude above the horizon, degrees. Negative when the sun is below the horizon.</param>
        /// <param name="azimuth">Solar azimuth, compass degrees clockwise from north (+Y), 0–360.</param>
        public static bool TryGetSunAngles(this Vector3D sunDirection, out double altitude, out double azimuth)
        {
            altitude = double.NaN;
            azimuth = double.NaN;

            if (sunDirection == null || !sunDirection.IsValid() || sunDirection.Length < Core.Tolerance.Distance)
            {
                return false;
            }

            Vector3D unit = sunDirection.Unit;

            altitude = Math.Asin(Math.Max(-1.0, Math.Min(1.0, -unit.Z))) * 180.0 / Math.PI;

            azimuth = Math.Atan2(-unit.X, -unit.Y) * 180.0 / Math.PI;
            if (azimuth < 0)
            {
                azimuth += 360.0;
            }

            return true;
        }
    }
}
