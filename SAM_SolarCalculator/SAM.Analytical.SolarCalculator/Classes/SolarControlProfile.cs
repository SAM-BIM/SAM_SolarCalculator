// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using SAM.Core;
using SAM.Core.SolarCalculator;

namespace SAM.Analytical.SolarCalculator
{
    /// <summary>
    /// The hour-by-hour control schedule of one aperture under a <see cref="SolarControlSettings"/>
    /// rule: which hours of the weather year the sun is up, which of them make solar UNWANTED at
    /// this particular window, and which of those a device could actually be deployed in.
    ///
    /// EVERY HOUR NUMBER IS A 0-BASED HOUR OF THE YEAR: 0 = 1 Jan 00:00, 8759 = 31 Dec 23:00
    /// (8783 in a leap year). The sets are stored sparsely — a list of the hours that qualify, not
    /// an 8760-long array of flags.
    ///
    /// THE TWO QUESTIONS, KEPT APART:
    ///
    ///   <see cref="ShadeDemandHoursOfYear"/> — shading is REQUESTED because the conditions make
    ///   this window's solar unwanted. This is the environmental answer, and it is what becomes
    ///   desirability for the shading optimisation (<see cref="ExternalDesirability"/>).
    ///
    ///   <see cref="ShadeOnHoursOfYear"/> — the device is ACTUALLY DEPLOYED: requested AND permitted
    ///   by the wind constraint. This is the operating schedule. It is a subset of the demand, and
    ///   the difference is <see cref="HighWindHoursOfYear"/>.
    ///
    /// Wind never appears in the weights. A gale does not make solar welcome; it stops the awning
    /// coming out.
    ///
    /// DAYLIGHT ONLY. Demand is only ever raised while the sun is above the horizon. A shading
    /// device does nothing at night, and without this a warm night under OR logic would be reported
    /// as a shading hour. There is deliberately no "shade off" set: it would be dominated by the
    /// ~4400 night hours and would tell an engineer nothing.
    ///
    /// PHASE 1 WEIGHTS ARE BINARY: exactly +1 on each demand hour (the sign convention of
    /// <see cref="IDesirabilityStrategy"/> — positive means blocking is beneficial). The weights are
    /// carried as their own map rather than derived, so a later continuous rule can fill the same
    /// map with graded values without changing this API or any consumer of it.
    /// </summary>
    public class SolarControlProfile : IJSAMObject, ISolarObject
    {
        public const int CurrentSchemaVersion = 1;

        private int schemaVersion = CurrentSchemaVersion;
        private Guid apertureGuid;
        private int year;
        private double timeShiftInMinutes;
        private SolarControlSettings settings;
        private List<int> sunHoursOfYear;
        private List<int> solarDemandHoursOfYear;
        private List<int> temperatureDemandHoursOfYear;
        private List<int> windSafeHoursOfYear;
        private List<int> shadeDemandHoursOfYear;
        private List<int> shadeOnHoursOfYear;
        private List<int> highWindHoursOfYear;
        private Dictionary<int, double> weightByHourOfYear;
        private int evaluatedHours;
        private int missingWeatherHours;
        private int missingTemperatureHours;
        private int missingWindSpeedHours;

        /// <param name="apertureGuid">The aperture this schedule belongs to.</param>
        /// <param name="year">The weather year the hours of the year are counted in.</param>
        /// <param name="timeShiftInMinutes">Sun-position sampling offset of the timeline, minutes.</param>
        /// <param name="settings">The control rule that produced the sets.</param>
        /// <param name="sunHoursOfYear">Hours the sun was above the horizon.</param>
        /// <param name="solarDemandHoursOfYear">Sun hours meeting the solar (and sunlit-share) criterion.</param>
        /// <param name="temperatureDemandHoursOfYear">Sun hours meeting the temperature criterion. Empty when it is not in use.</param>
        /// <param name="windSafeHoursOfYear">Sun hours the wind constraint permits deployment in.</param>
        /// <param name="shadeDemandHoursOfYear">Sun hours the rule says shading is wanted in.</param>
        /// <param name="shadeOnHoursOfYear">Demand hours the wind constraint permits: the deployment schedule.</param>
        /// <param name="highWindHoursOfYear">Demand hours refused by the wind constraint.</param>
        /// <param name="weightByHourOfYear">Desirability weight by 0-based hour of the year.</param>
        /// <param name="evaluatedHours">Hours that could be evaluated at all.</param>
        /// <param name="missingWeatherHours">Hours skipped: no weather hour, a missing raw radiation field, or an unresolvable sun position.</param>
        /// <param name="missingTemperatureHours">Sun hours with no dry-bulb value, while the temperature criterion was in use.</param>
        /// <param name="missingWindSpeedHours">Sun hours with no wind speed, while the wind constraint was in use.</param>
        public SolarControlProfile(Guid apertureGuid, int year, double timeShiftInMinutes, SolarControlSettings settings, IEnumerable<int> sunHoursOfYear, IEnumerable<int> solarDemandHoursOfYear, IEnumerable<int> temperatureDemandHoursOfYear, IEnumerable<int> windSafeHoursOfYear, IEnumerable<int> shadeDemandHoursOfYear, IEnumerable<int> shadeOnHoursOfYear, IEnumerable<int> highWindHoursOfYear, IDictionary<int, double> weightByHourOfYear, int evaluatedHours, int missingWeatherHours, int missingTemperatureHours, int missingWindSpeedHours)
        {
            this.apertureGuid = apertureGuid;
            this.year = year;
            this.timeShiftInMinutes = timeShiftInMinutes;
            this.settings = settings == null ? null : new SolarControlSettings(settings);
            this.sunHoursOfYear = Copy(sunHoursOfYear);
            this.solarDemandHoursOfYear = Copy(solarDemandHoursOfYear);
            this.temperatureDemandHoursOfYear = Copy(temperatureDemandHoursOfYear);
            this.windSafeHoursOfYear = Copy(windSafeHoursOfYear);
            this.shadeDemandHoursOfYear = Copy(shadeDemandHoursOfYear);
            this.shadeOnHoursOfYear = Copy(shadeOnHoursOfYear);
            this.highWindHoursOfYear = Copy(highWindHoursOfYear);
            this.weightByHourOfYear = weightByHourOfYear == null ? new Dictionary<int, double>() : new Dictionary<int, double>(weightByHourOfYear);
            this.evaluatedHours = evaluatedHours;
            this.missingWeatherHours = missingWeatherHours;
            this.missingTemperatureHours = missingTemperatureHours;
            this.missingWindSpeedHours = missingWindSpeedHours;
        }

        public SolarControlProfile(SolarControlProfile solarControlProfile)
        {
            if (solarControlProfile != null)
            {
                schemaVersion = solarControlProfile.schemaVersion;
                apertureGuid = solarControlProfile.apertureGuid;
                year = solarControlProfile.year;
                timeShiftInMinutes = solarControlProfile.timeShiftInMinutes;
                settings = solarControlProfile.settings == null ? null : new SolarControlSettings(solarControlProfile.settings);
                sunHoursOfYear = Copy(solarControlProfile.sunHoursOfYear);
                solarDemandHoursOfYear = Copy(solarControlProfile.solarDemandHoursOfYear);
                temperatureDemandHoursOfYear = Copy(solarControlProfile.temperatureDemandHoursOfYear);
                windSafeHoursOfYear = Copy(solarControlProfile.windSafeHoursOfYear);
                shadeDemandHoursOfYear = Copy(solarControlProfile.shadeDemandHoursOfYear);
                shadeOnHoursOfYear = Copy(solarControlProfile.shadeOnHoursOfYear);
                highWindHoursOfYear = Copy(solarControlProfile.highWindHoursOfYear);
                weightByHourOfYear = solarControlProfile.weightByHourOfYear == null ? null : new Dictionary<int, double>(solarControlProfile.weightByHourOfYear);
                evaluatedHours = solarControlProfile.evaluatedHours;
                missingWeatherHours = solarControlProfile.missingWeatherHours;
                missingTemperatureHours = solarControlProfile.missingTemperatureHours;
                missingWindSpeedHours = solarControlProfile.missingWindSpeedHours;
            }
        }

        public SolarControlProfile(JsonObject jObject)
        {
            FromJsonObject(jObject);
        }

        public int SchemaVersion
        {
            get
            {
                return schemaVersion;
            }
        }

        /// <summary>The aperture this schedule belongs to.</summary>
        public Guid ApertureGuid
        {
            get
            {
                return apertureGuid;
            }
        }

        /// <summary>The weather year every hour of the year in this profile is counted in.</summary>
        public int Year
        {
            get
            {
                return year;
            }
        }

        /// <summary>Sun-position sampling offset applied to each weather timestamp, minutes.</summary>
        public double TimeShiftInMinutes
        {
            get
            {
                return timeShiftInMinutes;
            }
        }

        /// <summary>The control rule these hours were produced by. Defensive copy.</summary>
        public SolarControlSettings Settings
        {
            get
            {
                return settings == null ? null : new SolarControlSettings(settings);
            }
        }

        /// <summary>Hours the sun was above the horizon (0-based). Every other set is a subset of this.</summary>
        public List<int> SunHoursOfYear
        {
            get
            {
                return Copy(sunHoursOfYear);
            }
        }

        /// <summary>
        /// Sun hours whose DIRECT beam on the window plane met the solar threshold — and, when that
        /// criterion is in use, whose sunlit share met the minimum. Orientation-specific: the same
        /// hour qualifies at one façade and not at another.
        /// </summary>
        public List<int> SolarDemandHoursOfYear
        {
            get
            {
                return Copy(solarDemandHoursOfYear);
            }
        }

        /// <summary>
        /// Sun hours whose outdoor dry-bulb met the temperature threshold. EMPTY when no temperature
        /// criterion is in use — the criterion is absent, not universally satisfied.
        /// </summary>
        public List<int> TemperatureDemandHoursOfYear
        {
            get
            {
                return Copy(temperatureDemandHoursOfYear);
            }
        }

        /// <summary>
        /// Sun hours the wind constraint permits deployment in. All sun hours when no constraint is
        /// set. An hour with no recorded wind speed IS included: the criterion could not be
        /// evaluated, which is not the same as being violated, and the number of such hours is on
        /// <see cref="MissingWindSpeedHours"/> so the gap in the weather file stays visible.
        /// </summary>
        public List<int> WindSafeHoursOfYear
        {
            get
            {
                return Copy(windSafeHoursOfYear);
            }
        }

        /// <summary>
        /// Shading REQUESTED: sun hours the rule judges this window's solar unwanted in. The
        /// environmental answer, before any hardware limit.
        /// </summary>
        public List<int> ShadeDemandHoursOfYear
        {
            get
            {
                return Copy(shadeDemandHoursOfYear);
            }
        }

        /// <summary>
        /// The DEPLOYMENT SCHEDULE: demand hours the wind constraint permits. Identical to the
        /// demand when no wind constraint is set. This is the only "allowed" set — in this phase
        /// there is no other reason for a device to be off while shading is wanted.
        /// </summary>
        public List<int> ShadeOnHoursOfYear
        {
            get
            {
                return Copy(shadeOnHoursOfYear);
            }
        }

        /// <summary>
        /// Demand hours the device could NOT be deployed in because the RECORDED wind speed exceeded
        /// the limit: shading was wanted and refused. Empty when no wind constraint is set. Exactly
        /// ShadeDemand minus ShadeOn.
        ///
        /// An hour with no recorded wind speed never appears here — an unevaluable criterion is not
        /// a high-wind event. See <see cref="MissingWindSpeedHours"/>.
        /// </summary>
        public List<int> HighWindHoursOfYear
        {
            get
            {
                return Copy(highWindHoursOfYear);
            }
        }

        public int SunHours
        {
            get
            {
                return sunHoursOfYear?.Count ?? 0;
            }
        }

        public int SolarDemandHours
        {
            get
            {
                return solarDemandHoursOfYear?.Count ?? 0;
            }
        }

        public int TemperatureDemandHours
        {
            get
            {
                return temperatureDemandHoursOfYear?.Count ?? 0;
            }
        }

        public int WindSafeHours
        {
            get
            {
                return windSafeHoursOfYear?.Count ?? 0;
            }
        }

        /// <summary>How many hours of the year shading is requested in.</summary>
        public int ShadeDemandHours
        {
            get
            {
                return shadeDemandHoursOfYear?.Count ?? 0;
            }
        }

        /// <summary>How many hours of the year the device is actually deployed for.</summary>
        public int ShadeOnHours
        {
            get
            {
                return shadeOnHoursOfYear?.Count ?? 0;
            }
        }

        /// <summary>How many requested hours the wind constraint refused.</summary>
        public int HighWindHours
        {
            get
            {
                return highWindHoursOfYear?.Count ?? 0;
            }
        }

        /// <summary>
        /// Share of the REQUESTED hours the device is actually deployed for, 0-1. NaN when nothing
        /// was requested — it must never read as 0 % or 100 % in that case.
        /// </summary>
        public double ShadeUseFraction
        {
            get
            {
                int demand = ShadeDemandHours;
                return demand == 0 ? double.NaN : (double)ShadeOnHours / demand;
            }
        }

        /// <summary>Hours that could be evaluated at all (weather present, sun position resolvable).</summary>
        public int EvaluatedHours
        {
            get
            {
                return evaluatedHours;
            }
        }

        /// <summary>Hours skipped: no weather hour, a missing raw radiation field, or an unresolvable sun position.</summary>
        public int MissingWeatherHours
        {
            get
            {
                return missingWeatherHours;
            }
        }

        /// <summary>Sun hours carrying no dry-bulb value while the temperature criterion was in use.</summary>
        public int MissingTemperatureHours
        {
            get
            {
                return missingTemperatureHours;
            }
        }

        /// <summary>
        /// Sun hours carrying no wind speed while the wind constraint was in use — hours the limit
        /// could not be checked against. They are left OPERABLE (this is an annual design
        /// preprocessor, not a live safety controller) and are never counted as high wind, so this
        /// number is how far the wind side of the answer rests on an incomplete weather file.
        /// </summary>
        public int MissingWindSpeedHours
        {
            get
            {
                return missingWindSpeedHours;
            }
        }

        /// <summary>
        /// Desirability weight by 0-based hour of the year — positive means blocking this hour's sun
        /// is beneficial, following <see cref="IDesirabilityStrategy"/>. Phase 1 supplies exactly
        /// +1 on each demand hour; hours not listed are neutral. Defensive copy.
        /// </summary>
        public Dictionary<int, double> WeightByHourOfYear
        {
            get
            {
                return weightByHourOfYear == null ? null : new Dictionary<int, double>(weightByHourOfYear);
            }
        }

        /// <summary>The weighted hours of the year, ascending — the keys of <see cref="WeightByHourOfYear"/>.</summary>
        public List<int> WeightedHoursOfYear
        {
            get
            {
                if (weightByHourOfYear == null)
                {
                    return null;
                }

                List<int> result = new List<int>(weightByHourOfYear.Keys);
                result.Sort();
                return result;
            }
        }

        /// <summary>
        /// The weights in <see cref="WeightedHoursOfYear"/> order, so the two lists can be read as
        /// one table.
        /// </summary>
        public List<double> Weights
        {
            get
            {
                List<int> hoursOfYear = WeightedHoursOfYear;
                if (hoursOfYear == null)
                {
                    return null;
                }

                List<double> result = new List<double>(hoursOfYear.Count);
                foreach (int hourOfYear in hoursOfYear)
                {
                    result.Add(weightByHourOfYear[hourOfYear]);
                }

                return result;
            }
        }

        /// <summary>
        /// This schedule as a desirability strategy the existing shading optimisation consumes
        /// unchanged: wire it into RationaliseShading's _desirability_ and the search sizes the
        /// device against exactly these hours.
        ///
        /// It carries the DEMAND, not the deployment: the optimiser is choosing geometry for the
        /// hours whose solar is unwanted, and whether a gale retracted the awning on some of them is
        /// not a property of the solar.
        /// </summary>
        public ExternalDesirability ExternalDesirability()
        {
            return new ExternalDesirability(year, weightByHourOfYear);
        }

        public bool FromJsonObject(JsonObject jObject)
        {
            if (jObject == null)
            {
                return false;
            }

            if (jObject.ContainsKey("SchemaVersion"))
            {
                schemaVersion = jObject["SchemaVersion"]?.GetValue<int>() ?? default;
            }

            if (jObject.ContainsKey("ApertureGuid"))
            {
                Guid.TryParse(jObject["ApertureGuid"]?.GetValue<string>(), out apertureGuid);
            }

            if (jObject.ContainsKey("Year"))
            {
                year = jObject["Year"]?.GetValue<int>() ?? default;
            }

            if (jObject.ContainsKey("TimeShiftInMinutes"))
            {
                timeShiftInMinutes = jObject["TimeShiftInMinutes"]?.GetValue<double>() ?? default;
            }

            settings = jObject.ContainsKey("Settings") ? new SolarControlSettings(jObject["Settings"] as JsonObject) : null;

            sunHoursOfYear = HoursOfYear(jObject, "SunHoursOfYear");
            solarDemandHoursOfYear = HoursOfYear(jObject, "SolarDemandHoursOfYear");
            temperatureDemandHoursOfYear = HoursOfYear(jObject, "TemperatureDemandHoursOfYear");
            windSafeHoursOfYear = HoursOfYear(jObject, "WindSafeHoursOfYear");
            shadeDemandHoursOfYear = HoursOfYear(jObject, "ShadeDemandHoursOfYear");
            shadeOnHoursOfYear = HoursOfYear(jObject, "ShadeOnHoursOfYear");
            highWindHoursOfYear = HoursOfYear(jObject, "HighWindHoursOfYear");

            weightByHourOfYear = null;
            if (jObject.ContainsKey("WeightByHourOfYear"))
            {
                JsonObject jObject_Dictionary = jObject["WeightByHourOfYear"] as JsonObject;
                if (jObject_Dictionary != null)
                {
                    weightByHourOfYear = new Dictionary<int, double>();
                    foreach (KeyValuePair<string, JsonNode> pair in jObject_Dictionary)
                    {
                        if (int.TryParse(pair.Key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int hourOfYear))
                        {
                            weightByHourOfYear[hourOfYear] = pair.Value?.GetValue<double>() ?? 0.0;
                        }
                    }
                }
            }

            if (jObject.ContainsKey("EvaluatedHours"))
            {
                evaluatedHours = jObject["EvaluatedHours"]?.GetValue<int>() ?? default;
            }

            if (jObject.ContainsKey("MissingWeatherHours"))
            {
                missingWeatherHours = jObject["MissingWeatherHours"]?.GetValue<int>() ?? default;
            }

            if (jObject.ContainsKey("MissingTemperatureHours"))
            {
                missingTemperatureHours = jObject["MissingTemperatureHours"]?.GetValue<int>() ?? default;
            }

            if (jObject.ContainsKey("MissingWindSpeedHours"))
            {
                missingWindSpeedHours = jObject["MissingWindSpeedHours"]?.GetValue<int>() ?? default;
            }

            return true;
        }

        public JsonObject ToJsonObject()
        {
            JsonObject jObject = new JsonObject();
            jObject.Add("_type", Core.Query.FullTypeName(this));

            jObject.Add("SchemaVersion", schemaVersion);
            jObject.Add("ApertureGuid", apertureGuid.ToString());
            jObject.Add("Year", year);
            jObject.Add("TimeShiftInMinutes", timeShiftInMinutes);

            if (settings != null)
            {
                jObject.Add("Settings", settings.ToJsonObject());
            }

            Add(jObject, "SunHoursOfYear", sunHoursOfYear);
            Add(jObject, "SolarDemandHoursOfYear", solarDemandHoursOfYear);
            Add(jObject, "TemperatureDemandHoursOfYear", temperatureDemandHoursOfYear);
            Add(jObject, "WindSafeHoursOfYear", windSafeHoursOfYear);
            Add(jObject, "ShadeDemandHoursOfYear", shadeDemandHoursOfYear);
            Add(jObject, "ShadeOnHoursOfYear", shadeOnHoursOfYear);
            Add(jObject, "HighWindHoursOfYear", highWindHoursOfYear);

            if (weightByHourOfYear != null)
            {
                JsonObject jObject_Dictionary = new JsonObject();
                foreach (KeyValuePair<int, double> pair in weightByHourOfYear)
                {
                    jObject_Dictionary.Add(pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), pair.Value);
                }
                jObject.Add("WeightByHourOfYear", jObject_Dictionary);
            }

            jObject.Add("EvaluatedHours", evaluatedHours);
            jObject.Add("MissingWeatherHours", missingWeatherHours);
            jObject.Add("MissingTemperatureHours", missingTemperatureHours);
            jObject.Add("MissingWindSpeedHours", missingWindSpeedHours);

            return jObject;
        }

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0} sun hours | {1} hours shading requested | {2} hours deployed{3} | {4}",
                SunHours,
                ShadeDemandHours,
                ShadeOnHours,
                HighWindHours == 0 ? string.Empty : string.Format(System.Globalization.CultureInfo.InvariantCulture, " | {0} hours refused by wind", HighWindHours),
                settings == null ? "no rule" : settings.ToString());
        }

        private static List<int> Copy(IEnumerable<int> hoursOfYear)
        {
            return hoursOfYear == null ? null : new List<int>(hoursOfYear);
        }

        private static void Add(JsonObject jObject, string name, List<int> hoursOfYear)
        {
            if (hoursOfYear == null)
            {
                return;
            }

            JsonArray jArray = new JsonArray();
            foreach (int hourOfYear in hoursOfYear)
            {
                jArray.Add(hourOfYear);
            }

            jObject.Add(name, jArray);
        }

        private static List<int> HoursOfYear(JsonObject jObject, string name)
        {
            if (!jObject.ContainsKey(name))
            {
                return null;
            }

            JsonArray jArray = jObject[name] as JsonArray;
            if (jArray == null)
            {
                return null;
            }

            List<int> result = new List<int>(jArray.Count);
            foreach (JsonNode jNode in jArray)
            {
                result.Add(jNode?.GetValue<int>() ?? default);
            }

            return result;
        }
    }
}
