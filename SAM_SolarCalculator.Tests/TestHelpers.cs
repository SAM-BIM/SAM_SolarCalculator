// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;
using SAM.Core;
using SAM.Weather;

namespace SAM.SolarCalculator.Tests
{
    internal static class TestHelpers
    {
        public static Location London()
        {
            Location location = new Location("London", -0.1278, 51.5074, 0);
            location.SetValue(LocationParameter.TimeZone, "UTC+00:00");
            return location;
        }

        /// <summary>
        /// Synthetic hourly weather for one year with the given per-day radiation profile.
        /// Keys are SAM WeatherDataType names (the same keys the EPW import writes).
        /// Values are W/m2 on the weather-timeline hour.
        /// </summary>
        public static WeatherData SyntheticWeatherData(int year, Location location, Func<DateTime, Tuple<double, double, double>> globalDiffuseDirect_NormalNotUsed)
        {
            WeatherData weatherData = new WeatherData("Synthetic", "Synthetic test weather", location.Latitude, location.Longitude, location.Elevation);
            weatherData.SetValue(WeatherDataParameter.TimeZone, "UTC+00:00");

            DateTime start = new DateTime(year, 1, 1);
            int hours = DateTime.IsLeapYear(year) ? 8784 : 8760;
            for (int i = 0; i < hours; i++)
            {
                DateTime dateTime = start.AddHours(i);
                Tuple<double, double, double> values = globalDiffuseDirect_NormalNotUsed(dateTime);
                Dictionary<string, double> dictionary = new Dictionary<string, double>
                {
                    { WeatherDataType.GlobalSolarRadiation.ToString(), values.Item1 },
                    { WeatherDataType.DiffuseSolarRadiation.ToString(), values.Item2 },
                    { WeatherDataType.DryBulbTemperature.ToString(), 12.0 },
                };
                weatherData.Add(dateTime, dictionary);
            }

            return weatherData;
        }
    }
}
