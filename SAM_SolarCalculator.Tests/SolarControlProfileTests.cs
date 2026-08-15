// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using SAM.Analytical;
using SAM.Analytical.SolarCalculator;
using SAM.Core;
using SAM.Core.SolarCalculator;
using SAM.Geometry.Object.Spatial;
using SAM.Geometry.SolarCalculator;
using SAM.Geometry.Spatial;
using SAM.Weather;
using SAM.Weather.SolarCalculator;

namespace SAM.SolarCalculator.Tests
{
    /// <summary>
    /// The hourly weather- and aperture-specific control layer: aperture-plane solar, the solar /
    /// temperature / wind criteria, the AND-OR combination, and the desirability it hands to the
    /// existing shading optimisation.
    ///
    /// Everything here is asserted as a RELATIONSHIP or against a value the test computes itself.
    /// No annual hour count is hard-coded: a claimed "1 372 hours a year" would be a number nobody
    /// could check and would break on any harmless change to the synthetic weather.
    /// </summary>
    public class SolarControlProfileTests
    {
        private readonly ITestOutputHelper output;

        public SolarControlProfileTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private const int Year = 2018;
        private const double Shift = 30.0;

        private static Location London()
        {
            return TestHelpers.London();
        }

        private static ApertureSolarTarget Target(Vector3D outward, double gridSize = 1.0)
        {
            Face3D face = SyntheticTargets.Face(outward, new Point3D(0, 0, 5), 1.0);
            List<AnalysisCell> cells = Geometry.SolarCalculator.Query.AnalysisCells(face, gridSize);
            return new ApertureSolarTarget(Guid.NewGuid(), Guid.NewGuid(), face, cells);
        }

        /// <summary>
        /// Synthetic weather with full control of temperature and wind. Radiation is a pure function
        /// of the sun position at (timestamp + shift), with NO diffuse, so the direct normal
        /// irradiance is exactly <paramref name="directNormal"/> whenever the sun is up — which makes
        /// the aperture-plane beam exactly DNI x cos(incidence) and hand-checkable.
        /// </summary>
        /// <param name="dryBulbTemperature">Dry bulb by timestamp. Return NaN to leave the field out entirely.</param>
        /// <param name="windSpeed">Wind speed by timestamp. Return NaN to leave the field out entirely.</param>
        private static WeatherData SyntheticWeather(int year, Location location, double directNormal, Func<DateTime, double> dryBulbTemperature, Func<DateTime, double> windSpeed)
        {
            double[] sinAltitude = SinAltitude(year, location);

            WeatherData weatherData = new WeatherData("Synthetic", "Synthetic control-profile weather", location.Latitude, location.Longitude, location.Elevation);
            weatherData.SetValue(WeatherDataParameter.TimeZone, "UTC+00:00");

            DateTime start = new DateTime(year, 1, 1);
            for (int i = 0; i < sinAltitude.Length; i++)
            {
                DateTime dateTime = start.AddHours(i);

                // GHI = DNI x sin(altitude), DHI = 0, so DNI comes back out exactly.
                double global = directNormal * sinAltitude[i];

                Dictionary<string, double> dictionary = new Dictionary<string, double>
                {
                    { WeatherDataType.GlobalSolarRadiation.ToString(), global },
                    { WeatherDataType.DiffuseSolarRadiation.ToString(), 0.0 },
                };

                double temperature = dryBulbTemperature == null ? double.NaN : dryBulbTemperature(dateTime);
                if (!double.IsNaN(temperature))
                {
                    dictionary[WeatherDataType.DryBulbTemperature.ToString()] = temperature;
                }

                double wind = windSpeed == null ? double.NaN : windSpeed(dateTime);
                if (!double.IsNaN(wind))
                {
                    dictionary[WeatherDataType.WindSpeed.ToString()] = wind;
                }

                weatherData.Add(dateTime, dictionary);
            }

            return weatherData;
        }

        private static WeatherData SyntheticWeather(double directNormal = 900.0, double temperature = 12.0, double wind = 2.0)
        {
            return SyntheticWeather(Year, London(), directNormal, x => temperature, x => wind);
        }

        private static readonly object padlock = new object();
        private static readonly Dictionary<int, double[]> sinAltitudeByYear = new Dictionary<int, double[]>();

        /// <summary>
        /// sin(solar altitude) at every hour of the year, clamped at zero below the horizon, on the
        /// same sampling shift the tests analyse on. Memoised per year: it is deterministic for the
        /// one site these tests use, and evaluating the sun 8760 times is by far the slowest part of
        /// building synthetic weather.
        /// </summary>
        private static double[] SinAltitude(int year, Location location)
        {
            lock (padlock)
            {
                if (sinAltitudeByYear.TryGetValue(year, out double[] existing))
                {
                    return existing;
                }

                DateTime start = new DateTime(year, 1, 1);
                double[] result = new double[DateTime.IsLeapYear(year) ? 8784 : 8760];
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = Geometry.SolarCalculator.Query.TryGetSunAngles(location, start.AddHours(i).AddMinutes(Shift), out double altitude, out _) && altitude > 0
                        ? Math.Sin(altitude * Math.PI / 180.0)
                        : 0.0;
                }

                sinAltitudeByYear[year] = result;
                return result;
            }
        }

        private static int HourOfYear(DateTime dateTime)
        {
            return (int)(dateTime - new DateTime(dateTime.Year, 1, 1)).TotalHours;
        }

        // ---------------------------------------------------------------- aperture-plane solar ----

        [Fact]
        public void South_Facing_Aperture_Receives_Direct_Solar_On_Its_Own_Plane()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);
            WeatherData weatherData = SyntheticWeather();

            List<ApertureSolarHour> hours = target.ApertureSolarHours(weatherData, Year, Shift);

            Assert.NotNull(hours);
            Assert.Equal(8760, hours.Count);

            // Midsummer noon: the sun is in front of a south façade and its beam lands on the plane.
            ApertureSolarHour noon = hours.First(x => x.HourOfYear == HourOfYear(new DateTime(Year, 6, 21, 12, 0, 0)));
            Assert.True(noon.SunAboveHorizon);
            Assert.True(noon.SunInFrontOfAperture);
            Assert.True(noon.ApertureDirectIrradiance > 0);

            // DNI comes back out of the synthetic weather exactly, and the plane beam is DNI x cos.
            Assert.Equal(900.0, noon.DirectNormalIrradiance, 6);
            Assert.Equal(900.0 * noon.CosIncidence, noon.ApertureDirectIrradiance, 6);

            output.WriteLine($"21 Jun 12:00 south: sun {noon.SolarElevation:0.#}° / {noon.SolarAzimuth:0.#}°, cos {noon.CosIncidence:0.###}, plane beam {noon.ApertureDirectIrradiance:0} W/m²");

            // Night carries no beam and is never counted as a sun hour.
            ApertureSolarHour midnight = hours.First(x => x.HourOfYear == HourOfYear(new DateTime(Year, 6, 21, 1, 0, 0)));
            Assert.False(midnight.SunAboveHorizon);
            Assert.Equal(0.0, midnight.ApertureDirectIrradiance);
        }

        [Fact]
        public void North_Facing_Aperture_Receives_No_Direct_Solar_When_The_Sun_Is_Behind_It()
        {
            WeatherData weatherData = SyntheticWeather();
            Location location = London();
            DateTime dateTime = new DateTime(Year, 6, 21, 12, 0, 0);
            int hourOfYear = HourOfYear(dateTime);
            WeatherHour weatherHour = weatherData.GetWeatherHour(dateTime);

            ApertureSolarHour south = Analytical.SolarCalculator.Create.ApertureSolarHour(location, weatherHour, SyntheticTargets.South, dateTime, hourOfYear, Shift);
            ApertureSolarHour north = Analytical.SolarCalculator.Create.ApertureSolarHour(location, weatherHour, SyntheticTargets.North, dateTime, hourOfYear, Shift);

            // The SAME hour and the SAME sun: only the orientation differs.
            Assert.Equal(south.SolarElevation, north.SolarElevation, 12);
            Assert.Equal(south.DirectNormalIrradiance, north.DirectNormalIrradiance, 12);

            Assert.True(south.SunInFrontOfAperture);
            Assert.True(south.ApertureDirectIrradiance > 0);

            Assert.False(north.SunInFrontOfAperture);
            Assert.Equal(0.0, north.ApertureDirectIrradiance);

            output.WriteLine($"same hour: south {south.ApertureDirectIrradiance:0} W/m², north {north.ApertureDirectIrradiance:0} W/m²");
        }

        [Fact]
        public void Orientation_Decides_Which_Hours_Are_Generated()
        {
            WeatherData weatherData = SyntheticWeather();
            SolarControlSettings settings = new SolarControlSettings(300.0);

            SolarControlProfile east = Target(SyntheticTargets.East).SolarControlProfile(weatherData, settings, Year);
            SolarControlProfile west = Target(SyntheticTargets.West).SolarControlProfile(weatherData, settings, Year);

            Assert.True(east.ShadeDemandHours > 0);
            Assert.True(west.ShadeDemandHours > 0);

            // Same site, same weather, same rule: only the façade differs. The east window's
            // unwanted hours are mornings, the west window's afternoons, and they barely meet.
            double eastMean = east.ShadeDemandHoursOfYear.Average(x => x % 24);
            double westMean = west.ShadeDemandHoursOfYear.Average(x => x % 24);
            output.WriteLine($"mean local hour of demand: east {eastMean:0.0}, west {westMean:0.0}");

            Assert.True(westMean - eastMean > 4.0, $"an east and a west façade must not be given the same hours (east {eastMean:0.0}, west {westMean:0.0})");

            int shared = east.ShadeDemandHoursOfYear.Intersect(west.ShadeDemandHoursOfYear).Count();
            Assert.True(shared < 0.1 * Math.Min(east.ShadeDemandHours, west.ShadeDemandHours), $"east and west shared {shared} demand hours");

            // The sun hours themselves are a property of the site, not the window.
            Assert.Equal(east.SunHours, west.SunHours);
        }

        // ------------------------------------------------------------------- solar threshold ----

        [Fact]
        public void Solar_Threshold_Selects_Exactly_The_Hours_Above_It()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);
            WeatherData weatherData = SyntheticWeather();

            List<ApertureSolarHour> hours = target.ApertureSolarHours(weatherData, Year, Shift);

            List<SolarControlProfile> profiles = new List<SolarControlProfile>();
            double[] thresholds = new[] { 100.0, 400.0, 800.0 };
            foreach (double threshold in thresholds)
            {
                SolarControlProfile profile = target.SolarControlProfile(weatherData, new SolarControlSettings(threshold), Year);
                profiles.Add(profile);

                // Hand-evaluated from the hourly series the test holds itself.
                List<int> expected = hours
                    .Where(x => x.SunAboveHorizon && x.ApertureDirectIrradiance >= threshold)
                    .Select(x => x.HourOfYear)
                    .ToList();

                Assert.Equal(expected, profile.ShadeDemandHoursOfYear);
                Assert.Equal(expected, profile.SolarDemandHoursOfYear);
                output.WriteLine($"threshold {threshold:0} W/m² -> {profile.ShadeDemandHours} hours");
            }

            SolarControlProfile low = profiles[0];
            SolarControlProfile high = profiles[2];

            Assert.True(high.ShadeDemandHours > 0);
            Assert.True(low.ShadeDemandHours > high.ShadeDemandHours);

            // Raising the threshold can only ever remove hours.
            Assert.Empty(high.ShadeDemandHoursOfYear.Except(low.ShadeDemandHoursOfYear));

            // A threshold no hour can reach is a real answer, not a failure.
            SolarControlProfile impossible = target.SolarControlProfile(weatherData, new SolarControlSettings(5000.0), Year);
            Assert.Equal(0, impossible.ShadeDemandHours);
            Assert.True(impossible.SunHours > 0);
            Assert.True(double.IsNaN(impossible.ShadeUseFraction));
        }

        [Fact]
        public void Every_Set_Is_A_Subset_Of_The_Sun_Hours()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);
            WeatherData weatherData = SyntheticWeather(900.0, 20.0, 12.0);

            SolarControlProfile profile = target.SolarControlProfile(weatherData, new SolarControlSettings(200.0, 15.0, 8.0), Year);

            List<int> sun = profile.SunHoursOfYear;
            Assert.Empty(profile.SolarDemandHoursOfYear.Except(sun));
            Assert.Empty(profile.TemperatureDemandHoursOfYear.Except(sun));
            Assert.Empty(profile.WindSafeHoursOfYear.Except(sun));
            Assert.Empty(profile.ShadeDemandHoursOfYear.Except(sun));
            Assert.Empty(profile.ShadeOnHoursOfYear.Except(profile.ShadeDemandHoursOfYear));
            Assert.Empty(profile.HighWindHoursOfYear.Except(profile.ShadeDemandHoursOfYear));

            // Deployed and refused partition the requested hours exactly.
            Assert.Equal(profile.ShadeDemandHours, profile.ShadeOnHours + profile.HighWindHours);
            Assert.Empty(profile.ShadeOnHoursOfYear.Intersect(profile.HighWindHoursOfYear));
        }

        // ------------------------------------------------------------------------ temperature ----

        [Fact]
        public void Temperature_Under_And_Logic_Must_Also_Be_Met()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);

            // Sun that clears the threshold all year; air that never does.
            SolarControlProfile cold = target.SolarControlProfile(
                SyntheticWeather(Year, London(), 900.0, x => 5.0, x => 2.0),
                new SolarControlSettings(200.0, 15.0, double.NaN, SolarControlLogic.And), Year);

            Assert.True(cold.SolarDemandHours > 0, "the sun criterion alone is met on plenty of hours");
            Assert.Equal(0, cold.TemperatureDemandHours);
            Assert.Equal(0, cold.ShadeDemandHours);

            SolarControlProfile warm = target.SolarControlProfile(
                SyntheticWeather(Year, London(), 900.0, x => 25.0, x => 2.0),
                new SolarControlSettings(200.0, 15.0, double.NaN, SolarControlLogic.And), Year);

            Assert.Equal(warm.SolarDemandHours, warm.ShadeDemandHours);
            Assert.True(warm.ShadeDemandHours > 0);

            output.WriteLine($"AND: 5 °C -> {cold.ShadeDemandHours} h, 25 °C -> {warm.ShadeDemandHours} h (solar alone {warm.SolarDemandHours} h)");
        }

        [Fact]
        public void Temperature_Under_Or_Logic_Is_An_Alternative_Route_To_Demand_And_Never_Fires_At_Night()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);

            // Warm all year, but a solar threshold only the brightest hours reach.
            WeatherData weatherData = SyntheticWeather(Year, London(), 900.0, x => 25.0, x => 2.0);

            SolarControlProfile and = target.SolarControlProfile(weatherData, new SolarControlSettings(800.0, 15.0, double.NaN, SolarControlLogic.And), Year);
            SolarControlProfile or = target.SolarControlProfile(weatherData, new SolarControlSettings(800.0, 15.0, double.NaN, SolarControlLogic.Or), Year);

            // OR admits every warm daylight hour, so it is strictly larger than the solar criterion.
            Assert.True(or.ShadeDemandHours > and.ShadeDemandHours);
            Assert.Equal(or.SunHours, or.ShadeDemandHours);
            Assert.Empty(and.ShadeDemandHoursOfYear.Except(or.ShadeDemandHoursOfYear));

            // AND admits only the hours both criteria claim.
            Assert.Equal(and.SolarDemandHours, and.ShadeDemandHours);

            // And no night hour is ever a demand hour, however warm the air is: a shading device
            // does nothing in the dark, so an OR on temperature must not deploy one.
            Assert.Empty(or.ShadeDemandHoursOfYear.Except(or.SunHoursOfYear));

            output.WriteLine($"25 °C all year, 800 W/m² threshold: AND {and.ShadeDemandHours} h, OR {or.ShadeDemandHours} h, sun {or.SunHours} h");
        }

        [Fact]
        public void An_Unused_Temperature_Criterion_Changes_Nothing_Under_Either_Logic()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);
            WeatherData weatherData = SyntheticWeather();

            SolarControlProfile solarOnly = target.SolarControlProfile(weatherData, new SolarControlSettings(400.0), Year);
            SolarControlProfile and = target.SolarControlProfile(weatherData, new SolarControlSettings(400.0, double.NaN, double.NaN, SolarControlLogic.And), Year);
            SolarControlProfile or = target.SolarControlProfile(weatherData, new SolarControlSettings(400.0, double.NaN, double.NaN, SolarControlLogic.Or), Year);

            // The trap this guards: an absent criterion reading as "always true" would make OR
            // permanently true, and every daylight hour would come back as unwanted solar.
            Assert.Equal(solarOnly.ShadeDemandHoursOfYear, and.ShadeDemandHoursOfYear);
            Assert.Equal(solarOnly.ShadeDemandHoursOfYear, or.ShadeDemandHoursOfYear);
            Assert.True(or.ShadeDemandHours < or.SunHours);
            Assert.Empty(or.TemperatureDemandHoursOfYear);
        }

        [Fact]
        public void A_Missing_Dry_Bulb_Asserts_Nothing_And_Is_Counted()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);

            // A weather file with no temperature field at all.
            WeatherData weatherData = SyntheticWeather(Year, London(), 900.0, x => double.NaN, x => 2.0);

            SolarControlProfile and = target.SolarControlProfile(weatherData, new SolarControlSettings(200.0, 15.0, double.NaN, SolarControlLogic.And), Year);
            SolarControlProfile or = target.SolarControlProfile(weatherData, new SolarControlSettings(200.0, 15.0, double.NaN, SolarControlLogic.Or), Year);
            SolarControlProfile solarOnly = target.SolarControlProfile(weatherData, new SolarControlSettings(200.0), Year);

            // Documented behaviour: a value that is not there cannot satisfy a criterion. Under AND
            // nothing is claimed; under OR the solar criterion still stands on its own.
            Assert.Equal(0, and.TemperatureDemandHours);
            Assert.Equal(0, and.ShadeDemandHours);
            Assert.Equal(solarOnly.ShadeDemandHoursOfYear, or.ShadeDemandHoursOfYear);

            // And it is reported rather than hidden.
            Assert.Equal(and.SunHours, and.MissingTemperatureHours);
            Assert.Equal(0, solarOnly.MissingTemperatureHours);
        }

        // ------------------------------------------------------------------------------ wind ----

        [Fact]
        public void Wind_Prevents_Deployment_Without_Changing_What_Is_Wanted()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);

            // Calm before noon, a gale after it.
            WeatherData weatherData = SyntheticWeather(Year, London(), 900.0, x => 20.0, x => x.Hour < 12 ? 2.0 : 20.0);

            SolarControlProfile unconstrained = target.SolarControlProfile(weatherData, new SolarControlSettings(200.0), Year);
            SolarControlProfile constrained = target.SolarControlProfile(weatherData, new SolarControlSettings(200.0, double.NaN, 10.0), Year);

            // The wind limit does not change WHAT IS WANTED.
            Assert.Equal(unconstrained.ShadeDemandHoursOfYear, constrained.ShadeDemandHoursOfYear);
            Assert.Equal(unconstrained.WeightByHourOfYear.Count, constrained.WeightByHourOfYear.Count);

            // It changes what can be DONE about it.
            Assert.True(constrained.HighWindHours > 0);
            Assert.True(constrained.ShadeOnHours < constrained.ShadeDemandHours);
            Assert.Equal(constrained.ShadeDemandHours, constrained.ShadeOnHours + constrained.HighWindHours);
            Assert.Equal(unconstrained.ShadeDemandHours, unconstrained.ShadeOnHours);
            Assert.Equal(0, unconstrained.HighWindHours);

            // Every refused hour is a windy one, every deployed hour a calm one.
            Assert.All(constrained.HighWindHoursOfYear, x => Assert.True(x % 24 >= 12));
            Assert.All(constrained.ShadeOnHoursOfYear, x => Assert.True(x % 24 < 12));

            output.WriteLine($"requested {constrained.ShadeDemandHours} h, deployed {constrained.ShadeOnHours} h, refused by wind {constrained.HighWindHours} h, use {100.0 * constrained.ShadeUseFraction:0.#} %");
        }

        [Fact]
        public void A_Missing_Wind_Speed_Leaves_The_Limit_UNCHECKED_Rather_Than_Breached()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);

            // A weather file with no wind field at all.
            WeatherData weatherData = SyntheticWeather(Year, London(), 900.0, x => 20.0, x => double.NaN);

            SolarControlProfile unconstrained = target.SolarControlProfile(weatherData, new SolarControlSettings(200.0), Year);
            SolarControlProfile constrained = target.SolarControlProfile(weatherData, new SolarControlSettings(200.0, double.NaN, 10.0), Year);

            // With no limit set the device simply operates; the absent field is not consulted.
            Assert.Equal(unconstrained.ShadeDemandHours, unconstrained.ShadeOnHours);
            Assert.Equal(0, unconstrained.MissingWindSpeedHours);

            // With a limit set, an hour that carries no reading leaves the criterion UNAVAILABLE,
            // which is not the same as violated. This is an annual design preprocessor, not a live
            // safety controller: an absent reading must not be turned into a retraction, or a gap in
            // the weather file would silently understate what the device can do all year.
            Assert.Equal(unconstrained.ShadeDemandHoursOfYear, constrained.ShadeDemandHoursOfYear);
            Assert.True(constrained.ShadeDemandHours > 0);
            Assert.Equal(constrained.ShadeDemandHours, constrained.ShadeOnHours);
            Assert.Equal(0, constrained.HighWindHours);
            Assert.Empty(constrained.HighWindHoursOfYear);
            Assert.Equal(constrained.SunHoursOfYear, constrained.WindSafeHoursOfYear);

            // It is never silent about it: the count is exactly how far the wind side of the answer
            // rests on an incomplete weather file.
            Assert.Equal(constrained.SunHours, constrained.MissingWindSpeedHours);

            // A file that DOES carry wind still retracts on the hours that breach the limit, so the
            // rule above has not simply disabled the constraint.
            SolarControlProfile windy = target.SolarControlProfile(
                SyntheticWeather(Year, London(), 900.0, x => 20.0, x => 20.0),
                new SolarControlSettings(200.0, double.NaN, 10.0), Year);

            Assert.Equal(0, windy.MissingWindSpeedHours);
            Assert.True(windy.HighWindHours > 0);
            Assert.Equal(0, windy.ShadeOnHours);
        }

        // --------------------------------------------------------------- feeding the optimiser ----

        [Fact]
        public void The_Profile_Feeds_The_Existing_Optimisation_As_External_Desirability()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);
            WeatherData weatherData = SyntheticWeather();

            SolarControlProfile profile = target.SolarControlProfile(weatherData, new SolarControlSettings(600.0), Year);
            Assert.True(profile.ShadeDemandHours > 0);

            // Weights are +1 on exactly the requested hours, keyed by 0-BASED hour of the year.
            Assert.Equal(profile.ShadeDemandHoursOfYear, profile.WeightedHoursOfYear);
            Assert.All(profile.Weights, x => Assert.Equal(1.0, x));

            ExternalDesirability desirability = profile.ExternalDesirability();
            DateTime yearStart = new DateTime(Year, 1, 1);

            int demandHourOfYear = profile.ShadeDemandHoursOfYear[0];
            DateTime demand = yearStart.AddHours(demandHourOfYear);
            Assert.Equal(1.0, desirability.Weight(demand, weatherData.GetWeatherHour(demand), target));
            output.WriteLine($"first requested hour: HOY {demandHourOfYear} = {demand:yyyy-MM-dd HH:mm}");

            // An hour outside the set is neutral, not negative: this rule says nothing about the
            // solar it did not claim.
            DateTime night = new DateTime(Year, 6, 21, 2, 0, 0);
            Assert.Equal(0.0, desirability.Weight(night, weatherData.GetWeatherHour(night), target));

            // And it drives the Stage 5 accounting: the energy the weighting claims is exactly the
            // aperture-plane beam of the requested hours, computed independently here.
            SolarVisibilityCache cache = Weather.SolarCalculator.Create.SolarVisibilityCache(
                London(), Year, 2.0, new List<LinkedFace3D>(), target.AnalysisCells,
                cellSize: 1.0, minHorizonAngle: 0.0349066, sunPositionShiftInMinutes: Shift);

            ApertureDesirability apertureDesirability = Analytical.SolarCalculator.Create.ApertureDesirability(target, cache, desirability, weatherData);
            Assert.NotNull(apertureDesirability);
            Assert.True(apertureDesirability.TotalUnwantedEnergy > 0);
            Assert.Equal(0.0, apertureDesirability.TotalWantedEnergy);

            // The bins skip hours below the cache's minimum horizon angle, so the accounting can
            // only ever be a subset of the requested hours' energy — never more than it.
            HashSet<int> demandHours = new HashSet<int>(profile.ShadeDemandHoursOfYear);
            double expected = target.ApertureSolarHours(weatherData, Year, Shift)
                .Where(x => demandHours.Contains(x.HourOfYear))
                .Sum(x => x.ApertureDirectIrradiance / 1000.0);

            output.WriteLine($"requested-hour energy: profile {expected:0.###} kWh/m², Stage 5 {apertureDesirability.TotalUnwantedEnergy:0.###} kWh/m²");
            Assert.True(apertureDesirability.TotalUnwantedEnergy <= expected + 1e-9);
            Assert.True(apertureDesirability.TotalUnwantedEnergy > 0.99 * expected, "the two must agree bar the near-horizon hours the sun bins do not cover");
        }

        // ---------------------------------------------------------------------- year handling ----

        [Fact]
        public void Hours_Of_Year_Are_Zero_Based_And_A_Leap_Year_Reports_What_The_Weather_Cannot_Supply()
        {
            const int leapYear = 2020;
            ApertureSolarTarget target = Target(SyntheticTargets.South);
            WeatherData weatherData = SyntheticWeather(leapYear, London(), 900.0, x => 20.0, x => 2.0);

            // A leap year has 8784 hours in the CALENDAR, and hours of the year are numbered over
            // that calendar. SAM's WeatherYear, however, holds exactly 365 days, so the 366th day
            // has nowhere to live: its 24 hours cannot be read back whatever is written. That is a
            // property of the weather framework, not of this feature, and the right response is to
            // report it rather than to invent values for the missing day.
            List<ApertureSolarHour> hours = target.ApertureSolarHours(weatherData, leapYear, Shift);
            Assert.Equal(8760, hours.Count);

            // 0-BASED, over the leap-year calendar: 0 = 1 Jan 00:00 and 1416 = 29 Feb 00:00, which
            // is only true if the extra day is counted in the numbering.
            Assert.Equal(new DateTime(leapYear, 1, 1, 0, 0, 0), hours[0].DateTime);
            Assert.Equal(0, hours[0].HourOfYear);
            Assert.Equal(new DateTime(leapYear, 2, 29, 0, 0, 0), hours.First(x => x.HourOfYear == 1416).DateTime);

            // The 24 unreadable hours are the last calendar day, and they are the ones missing.
            Assert.Equal(8759, hours[hours.Count - 1].HourOfYear);
            Assert.Equal(new DateTime(leapYear, 12, 30, 23, 0, 0), hours[hours.Count - 1].DateTime);

            SolarControlProfile profile = target.SolarControlProfile(weatherData, new SolarControlSettings(200.0), leapYear);
            Assert.Equal(leapYear, profile.Year);
            Assert.Equal(8760, profile.EvaluatedHours);
            Assert.Equal(24, profile.MissingWeatherHours);
            Assert.All(profile.SunHoursOfYear, x => Assert.InRange(x, 0, 8783));

            // A non-leap year loses nothing.
            SolarControlProfile common = target.SolarControlProfile(SyntheticWeather(), new SolarControlSettings(200.0), Year);
            Assert.Equal(8760, common.EvaluatedHours);
            Assert.Equal(0, common.MissingWeatherHours);
        }

        [Fact]
        public void A_Year_The_Weather_Does_Not_Carry_Falls_Back_To_The_Weather_Own_Year()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);
            WeatherData weatherData = SyntheticWeather();

            // The same rule Create.ApertureSolarContext follows: an empty schedule for a year the
            // file has no data for would be indistinguishable from a window that needs no shading.
            SolarControlProfile profile = target.SolarControlProfile(weatherData, new SolarControlSettings(200.0), 1999);

            Assert.Equal(Year, profile.Year);
            Assert.True(profile.ShadeDemandHours > 0);
        }

        [Fact]
        public void An_Analysis_Period_Restricts_The_Profile_To_Its_Own_Hours()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);
            WeatherData weatherData = SyntheticWeather();

            AnalysisPeriod summer = new AnalysisPeriod(Year, 6, 1, 8, 31);
            SolarControlProfile profile = target.SolarControlProfile(weatherData, new SolarControlSettings(200.0), Year, summer);
            SolarControlProfile fullYear = target.SolarControlProfile(weatherData, new SolarControlSettings(200.0), Year);

            Assert.Equal(summer.HoursOfYear().Count, profile.EvaluatedHours);
            Assert.Empty(profile.ShadeDemandHoursOfYear.Except(summer.HoursOfYear()));
            Assert.Empty(profile.ShadeDemandHoursOfYear.Except(fullYear.ShadeDemandHoursOfYear));
            Assert.True(profile.ShadeDemandHours > 0);
            Assert.True(profile.ShadeDemandHours < fullYear.ShadeDemandHours);
        }

        // ------------------------------------------------------------------------------ JSON ----

        [Fact]
        public void Json_RoundTrips_Preserve_The_Rule_And_Every_Hour()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);
            WeatherData weatherData = SyntheticWeather(Year, London(), 900.0, x => 18.0, x => x.Hour < 12 ? 2.0 : 20.0);

            SolarControlSettings settings = new SolarControlSettings(250.0, 15.0, 10.0, SolarControlLogic.Or);
            SolarControlSettings settings_Restored = new SolarControlSettings(settings.ToJsonObject());

            Assert.Equal(settings.MinimumApertureIrradiance, settings_Restored.MinimumApertureIrradiance, 12);
            Assert.Equal(settings.MinimumOutdoorTemperature, settings_Restored.MinimumOutdoorTemperature, 12);
            Assert.Equal(settings.MaximumWindSpeed, settings_Restored.MaximumWindSpeed, 12);
            Assert.Equal(SolarControlLogic.Or, settings_Restored.ControlLogic);

            // A criterion that was NOT in use must not acquire one by being saved and reloaded.
            SolarControlSettings solarOnly = new SolarControlSettings(250.0);
            SolarControlSettings solarOnly_Restored = new SolarControlSettings(solarOnly.ToJsonObject());
            Assert.False(solarOnly_Restored.TemperatureCriterionInUse);
            Assert.False(solarOnly_Restored.WindConstraintInUse);
            Assert.Equal(SolarControlLogic.And, solarOnly_Restored.ControlLogic);

            SolarControlProfile profile = target.SolarControlProfile(weatherData, new SolarControlSettings(250.0, 15.0, 10.0), Year);
            SolarControlProfile profile_Restored = new SolarControlProfile(profile.ToJsonObject());

            Assert.Equal(profile.ApertureGuid, profile_Restored.ApertureGuid);
            Assert.Equal(profile.Year, profile_Restored.Year);
            Assert.Equal(profile.TimeShiftInMinutes, profile_Restored.TimeShiftInMinutes, 12);
            Assert.Equal(profile.SunHoursOfYear, profile_Restored.SunHoursOfYear);
            Assert.Equal(profile.SolarDemandHoursOfYear, profile_Restored.SolarDemandHoursOfYear);
            Assert.Equal(profile.TemperatureDemandHoursOfYear, profile_Restored.TemperatureDemandHoursOfYear);
            Assert.Equal(profile.WindSafeHoursOfYear, profile_Restored.WindSafeHoursOfYear);
            Assert.Equal(profile.ShadeDemandHoursOfYear, profile_Restored.ShadeDemandHoursOfYear);
            Assert.Equal(profile.ShadeOnHoursOfYear, profile_Restored.ShadeOnHoursOfYear);
            Assert.Equal(profile.HighWindHoursOfYear, profile_Restored.HighWindHoursOfYear);
            Assert.Equal(profile.WeightedHoursOfYear, profile_Restored.WeightedHoursOfYear);
            Assert.Equal(profile.Weights, profile_Restored.Weights);
            Assert.Equal(profile.MissingWindSpeedHours, profile_Restored.MissingWindSpeedHours);
            Assert.Equal(profile.Settings.MaximumWindSpeed, profile_Restored.Settings.MaximumWindSpeed, 12);

            // It survives real text, not only an in-memory node tree: a NaN written as a number
            // would throw here.
            string text = profile.ToJsonObject().ToJsonString();
            Assert.Contains("ShadeOnHoursOfYear", text);
            Assert.True(profile.ShadeDemandHours > 0);
        }

        [Fact]
        public void A_Copied_Profile_Is_Independent_Of_The_Original()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);
            SolarControlProfile profile = target.SolarControlProfile(SyntheticWeather(), new SolarControlSettings(300.0), Year);

            SolarControlProfile copy = new SolarControlProfile(profile);
            Assert.Equal(profile.ShadeDemandHoursOfYear, copy.ShadeDemandHoursOfYear);

            // The getters hand out copies, so a caller cannot reach in and edit the schedule.
            List<int> hoursOfYear = profile.ShadeDemandHoursOfYear;
            hoursOfYear.Clear();
            Assert.Equal(copy.ShadeDemandHours, profile.ShadeDemandHours);
        }

        // ------------------------------------------------------------------- invalid settings ----

        [Fact]
        public void An_Unusable_Rule_Is_Refused_Rather_Than_Guessed_At()
        {
            ApertureSolarTarget target = Target(SyntheticTargets.South);
            WeatherData weatherData = SyntheticWeather();

            Assert.False(new SolarControlSettings(-100.0).IsValid(out string message));
            Assert.NotNull(message);
            Assert.Null(target.SolarControlProfile(weatherData, new SolarControlSettings(-100.0), Year));
            Assert.Null(target.SolarControlProfile(weatherData, new SolarControlSettings(200.0, double.NaN, -5.0), Year));

            Assert.Null(target.SolarControlProfile(null, new SolarControlSettings(200.0), Year));
            Assert.Null(((ApertureSolarTarget)null).SolarControlProfile(weatherData, new SolarControlSettings(200.0), Year));

            // Undefined logic is read as And rather than silently producing nothing.
            Assert.Equal(SolarControlLogic.And, new SolarControlSettings(200.0, 15.0, double.NaN, SolarControlLogic.Undefined).ControlLogic);
        }

        // ---------------------------------------------------- real project: Kołobrzeg façade ----

        /// <summary>
        /// The real WSW façade of the Kołobrzeg fixture (azimuth ~257.5°) against a synthetic
        /// aperture facing the opposite way at the SAME site and on the SAME weather file.
        ///
        /// Nothing here asserts an annual hour count. What is asserted is that the generated hours
        /// are a property of the ORIENTATION: the west-south-west façade's unwanted hours are
        /// afternoons and the east-north-east one's are mornings, and they hardly overlap. The
        /// comparison values are computed by the test from the fixture, not quoted.
        /// </summary>
        [Fact]
        public void Kolobrzeg_WSW_Facade_Generates_Afternoon_Hours_Not_The_Site_Daylight_Hours()
        {
            AnalyticalModel analyticalModel = KolobrzegFixture.Model();
            WeatherData weatherData = analyticalModel.GetValue<WeatherData>(AnalyticalModelParameter.WeatherData);
            Assert.NotNull(weatherData);

            int year = KolobrzegFixture.Year(analyticalModel);

            List<ApertureSolarTarget> targets = analyticalModel.ApertureSolarTargets(new List<Guid> { KolobrzegFixture.TallApertureGuid }, KolobrzegFixture.HistoricalGridSize);
            ApertureSolarTarget target = Assert.Single(targets);

            // The studied façade really is west-south-west.
            Assert.InRange(target.Azimuth, 250.0, 265.0);

            SolarControlSettings settings = new SolarControlSettings(200.0);
            SolarControlProfile profile = target.SolarControlProfile(weatherData, settings, year);
            Assert.NotNull(profile);

            output.WriteLine($"Kołobrzeg {year}, façade {target.Azimuth:0.#}°: {profile}");
            output.WriteLine($"evaluated {profile.EvaluatedHours} h, missing weather {profile.MissingWeatherHours} h");

            Assert.True(profile.SunHours > 0);
            Assert.True(profile.ShadeDemandHours > 0);

            // The generated hours are a small share of the daylight year: a façade threshold is not
            // a daylight switch.
            Assert.True(profile.ShadeDemandHours < profile.SunHours, "a solar threshold that admits every daylight hour is not orientation-specific");

            // The same site and the same weather, from the opposite direction.
            Vector3D outward = target.OutwardNormal;
            ApertureSolarTarget opposite = Target(new Vector3D(-outward.X, -outward.Y, outward.Z));
            SolarControlProfile oppositeProfile = opposite.SolarControlProfile(weatherData, settings, year);

            double wswMean = profile.ShadeDemandHoursOfYear.Average(x => x % 24);
            double eneMean = oppositeProfile.ShadeDemandHoursOfYear.Average(x => x % 24);
            output.WriteLine($"mean local hour of demand: WSW {wswMean:0.0} ({profile.ShadeDemandHours} h), opposite façade {eneMean:0.0} ({oppositeProfile.ShadeDemandHours} h)");

            Assert.True(wswMean > eneMean + 4.0, $"the WSW façade's unwanted hours must be later in the day than the opposite façade's (WSW {wswMean:0.0}, opposite {eneMean:0.0})");

            int shared = profile.ShadeDemandHoursOfYear.Intersect(oppositeProfile.ShadeDemandHoursOfYear).Count();
            Assert.True(shared < 0.1 * Math.Min(profile.ShadeDemandHours, oppositeProfile.ShadeDemandHours), $"opposite façades shared {shared} unwanted hours");

            // The daylight hours themselves belong to the site, so they are identical.
            Assert.Equal(profile.SunHours, oppositeProfile.SunHours);
        }
    }
}
