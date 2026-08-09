// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using SAM.Core;

namespace SAM.Geometry.SolarCalculator
{
    public static partial class Query
    {
        /// <summary>
        /// UTC offset of a Location in hours, preserved as a fractional value (e.g. 5.5 for UTC+05:30).
        /// Returns 0 when the Location carries no TimeZone parameter. This replaces the previous
        /// System.Convert.ToInt32(Core.Query.Double(uTC)) call sites, which rounded/truncated
        /// fractional time zones and shifted the computed sun position by up to 30 minutes.
        /// </summary>
        public static double TimeZoneOffset(this Location location)
        {
            if (location != null && location.TryGetValue(LocationParameter.TimeZone, out string timeZoneString))
            {
                UTC uTC = Core.Query.UTC(timeZoneString);
                if (uTC != UTC.Undefined)
                {
                    return Core.Query.Double(uTC);
                }
            }

            return 0;
        }
    }
}
