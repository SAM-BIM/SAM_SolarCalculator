// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;
using SAM.Core.SolarCalculator;
using SAM.Weather;

namespace SAM.Analytical.SolarCalculator
{
    public static partial class Create
    {
        /// <summary>
        /// The hourly shading control schedule of one aperture, from weather alone — no geometry
        /// pass, no ray tracing, no analytical model.
        ///
        /// For every hour of the weather year the aperture-plane direct beam is evaluated from the
        /// window's own orientation (see <see cref="ApertureSolarHour"/>), the control rule is
        /// applied, and the hours that qualify are collected. The cost is one sun-position
        /// evaluation per hour and nothing else — on the order of a second for a whole
        /// aperture-year, against minutes for a single ray-tracing pass. Restrict it with an
        /// AnalysisPeriod when only part of the year is of interest.
        ///
        /// THE RULE, exactly as applied:
        ///
        ///   sun up          = solar elevation &gt; 0. Nothing below is ever a demand hour: a shading
        ///                     device does nothing at night.
        ///   solar demand    = sun up AND aperture-plane direct beam &gt;= MinimumApertureIrradiance.
        ///   temperature     = outdoor dry-bulb &gt;= MinimumOutdoorTemperature, when in use.
        ///   shade demand    = And: solar demand AND (temperature, when in use)
        ///                     Or : solar demand OR  (sun up AND temperature), when in use
        ///                     With the temperature criterion NOT in use, both reduce to the solar
        ///                     demand — an absent criterion can neither block an AND nor satisfy
        ///                     an OR.
        ///   wind safe       = no constraint, OR the recorded wind speed &lt;= MaximumWindSpeed, OR
        ///                     the hour carries no wind speed at all (see below).
        ///   shade on        = shade demand AND wind safe.
        ///
        /// MISSING VALUES ARE NEVER INVENTED, and a criterion that cannot be evaluated is set
        /// aside rather than decided:
        ///   a missing dry-bulb DOES NOT satisfy the temperature criterion — no demand is
        ///   asserted from data that is not there — and it is counted in MissingTemperatureHours;
        ///   a missing wind speed makes the wind criterion UNAVAILABLE for that hour, not
        ///   violated. The hour is left operable and counted in MissingWindSpeedHours; it is
        ///   NEVER reported as a high-wind hour. This is an ANNUAL DESIGN PREPROCESSOR, not a
        ///   live safety controller: turning an absent reading into a retraction would understate
        ///   what a device can do all year on the strength of a gap in the weather file, and the
        ///   diagnostic count is there to make that gap visible instead.
        ///   An hour whose radiation fields or sun position are unusable is skipped entirely and
        ///   counted in MissingWeatherHours.
        /// </summary>
        /// <param name="target">The aperture. Its OUTWARD normal is what makes the result orientation-specific.</param>
        /// <param name="weatherData">Hourly weather; its Location drives the sun position.</param>
        /// <param name="solarControlSettings">The control rule. Null = the default rule (solar threshold only).</param>
        /// <param name="year">Weather year. Values not present in the weather fall back to its first year.</param>
        /// <param name="analysisPeriod">Optional restriction to part of the year. Null = the whole year. A period built for a different year is used for its HOUR-OF-YEAR structure on this year, the same way Create.ReRoot treats one.</param>
        /// <param name="sunTimeConvention">Timestamp convention of the weather timeline. IntervalStart (+30 min) is the SAM/EPW convention and the default everywhere else in this library.</param>
        public static SolarControlProfile SolarControlProfile(this ApertureSolarTarget target, WeatherData weatherData, SolarControlSettings solarControlSettings = null, int year = -1, AnalysisPeriod analysisPeriod = null, SunTimeConvention sunTimeConvention = SunTimeConvention.IntervalStart)
        {
            if (weatherData == null)
            {
                return null;
            }

            double timeShiftInMinutes = sunTimeConvention.TimeShiftInMinutes();
            if (double.IsNaN(timeShiftInMinutes))
            {
                return null;
            }

            // Same resolution rule as Create.ApertureSolarContext: a year the weather does not carry
            // falls back to the weather's own first year rather than producing an empty result.
            WeatherYear weatherYear = weatherData[year];
            if (weatherYear == null)
            {
                weatherYear = weatherData.WeatherYears?.Find(x => x != null);
                if (weatherYear == null)
                {
                    return null;
                }

                year = weatherYear.Year;
            }

            return SolarControlProfile(target, weatherData, solarControlSettings, year, timeShiftInMinutes, analysisPeriod);
        }

        private static SolarControlProfile SolarControlProfile(ApertureSolarTarget target, WeatherData weatherData, SolarControlSettings solarControlSettings, int year, double timeShiftInMinutes, AnalysisPeriod analysisPeriod)
        {
            if (target == null || weatherData == null || double.IsNaN(timeShiftInMinutes))
            {
                return null;
            }

            SolarControlSettings settings = solarControlSettings == null ? new SolarControlSettings() : new SolarControlSettings(solarControlSettings);
            if (!settings.IsValid(out string _))
            {
                return null;
            }

            List<ApertureSolarHour> apertureSolarHours = target.ApertureSolarHours(weatherData, year, timeShiftInMinutes, analysisPeriod);
            if (apertureSolarHours == null)
            {
                return null;
            }

            int hourCount = analysisPeriod == null
                ? (DateTime.IsLeapYear(year) ? 8784 : 8760)
                : (analysisPeriod.HoursOfYear()?.Count ?? 0);

            List<int> sunHoursOfYear = new List<int>();
            List<int> solarDemandHoursOfYear = new List<int>();
            List<int> temperatureDemandHoursOfYear = new List<int>();
            List<int> windSafeHoursOfYear = new List<int>();
            List<int> shadeDemandHoursOfYear = new List<int>();
            List<int> shadeOnHoursOfYear = new List<int>();
            List<int> highWindHoursOfYear = new List<int>();
            Dictionary<int, double> weightByHourOfYear = new Dictionary<int, double>();

            int missingTemperatureHours = 0;
            int missingWindSpeedHours = 0;

            bool temperatureInUse = settings.TemperatureCriterionInUse;
            bool windInUse = settings.WindConstraintInUse;

            foreach (ApertureSolarHour apertureSolarHour in apertureSolarHours)
            {
                if (!apertureSolarHour.SunAboveHorizon)
                {
                    continue;
                }

                int hourOfYear = apertureSolarHour.HourOfYear;
                sunHoursOfYear.Add(hourOfYear);

                // ---- solar criterion: the DIRECT beam on this window's own plane.
                bool solarDemand = apertureSolarHour.ApertureDirectIrradiance >= settings.MinimumApertureIrradiance;
                if (solarDemand)
                {
                    solarDemandHoursOfYear.Add(hourOfYear);
                }

                // ---- temperature criterion, when in use. A missing value asserts nothing.
                bool temperatureDemand = false;
                if (temperatureInUse)
                {
                    double dryBulbTemperature = apertureSolarHour.DryBulbTemperature;
                    if (double.IsNaN(dryBulbTemperature))
                    {
                        missingTemperatureHours++;
                    }
                    else
                    {
                        temperatureDemand = dryBulbTemperature >= settings.MinimumOutdoorTemperature;
                        if (temperatureDemand)
                        {
                            temperatureDemandHoursOfYear.Add(hourOfYear);
                        }
                    }
                }

                // ---- the combination. A criterion not in use takes no part in it.
                bool shadeDemand;
                if (!temperatureInUse)
                {
                    shadeDemand = solarDemand;
                }
                else if (settings.ControlLogic == SolarControlLogic.Or)
                {
                    shadeDemand = solarDemand || temperatureDemand;
                }
                else
                {
                    shadeDemand = solarDemand && temperatureDemand;
                }

                // ---- wind: an OPERATING constraint, evaluated independently of the demand.
                bool windSafe = true;
                if (windInUse)
                {
                    double windSpeed = apertureSolarHour.WindSpeed;
                    if (double.IsNaN(windSpeed))
                    {
                        // The criterion is UNAVAILABLE this hour, not violated. The absent
                        // reading is counted and the hour is left operable; it is never called
                        // high wind.
                        missingWindSpeedHours++;
                    }
                    else
                    {
                        windSafe = windSpeed <= settings.MaximumWindSpeed;
                    }
                }

                if (windSafe)
                {
                    windSafeHoursOfYear.Add(hourOfYear);
                }

                if (!shadeDemand)
                {
                    continue;
                }

                shadeDemandHoursOfYear.Add(hourOfYear);

                // Phase 1 weights are binary. They follow the DEMAND, never the deployment: wind is
                // a hardware limit, not a statement about whether the solar was welcome.
                weightByHourOfYear[hourOfYear] = 1.0;

                if (windSafe)
                {
                    shadeOnHoursOfYear.Add(hourOfYear);
                }
                else
                {
                    highWindHoursOfYear.Add(hourOfYear);
                }
            }

            return new SolarControlProfile(
                target.ApertureGuid, year, timeShiftInMinutes, settings,
                sunHoursOfYear, solarDemandHoursOfYear, temperatureDemandHoursOfYear, windSafeHoursOfYear,
                shadeDemandHoursOfYear, shadeOnHoursOfYear, highWindHoursOfYear, weightByHourOfYear,
                apertureSolarHours.Count, Math.Max(0, hourCount - apertureSolarHours.Count), missingTemperatureHours, missingWindSpeedHours);
        }
    }
}
