// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using Innovative.Geometry;
using SAM.Core;
using System;

namespace SAM.Geometry.SolarCalculator
{
    public static partial class Create
    {
        /// <summary>
        /// SolarTimes for a Location at a DateTime. The full fractional UTC offset is carried into
        /// the SolarTimes constructor via DateTimeOffset (SolarCalculator 3.5.0 derives its decimal
        /// TimeZoneOffset from DateTimeOffset.Offset.TotalHours, so e.g. UTC+05:30 is preserved as 5.5).
        /// </summary>
        public static Innovative.SolarCalculator.SolarTimes SolarTimes(this Location location, DateTime dateTime)
        {
            if (location == null)
            {
                return null;
            }

            double timeZoneOffset = Query.TimeZoneOffset(location);

            return new Innovative.SolarCalculator.SolarTimes(new DateTimeOffset(dateTime, TimeSpan.FromHours(timeZoneOffset)), new Angle(location.Latitude), new Angle(location.Longitude));
        }
    }
}
