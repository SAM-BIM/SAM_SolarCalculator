// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using SAM.Core;
using SAM.Core.SolarCalculator;
using SAM.Geometry.Spatial;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace SAM.Geometry.SolarCalculator
{
    public class SolarCoverageSimulationResult : Result, ISolarSimulationResult
    {
        private Dictionary<DateTime, double> coverage = new Dictionary<DateTime, double>();

        public SolarCoverageSimulationResult(string name, string source, string reference, IEnumerable<Tuple<DateTime, double>> coverage)
            : base(name, source, reference)
        {
            if(coverage != null)
            {
                foreach(Tuple<DateTime, double> tuple in coverage)
                {
                    this.coverage[tuple.Item1] = tuple.Item2;
                }
            }
        }

        public SolarCoverageSimulationResult(string name, string source, string reference, SolarCoverageSimulationResult solarCoverageSimulationResult)
            : base(name, source, reference)
        {
            if(solarCoverageSimulationResult?.coverage != null)
            {
                foreach(KeyValuePair<DateTime, double> keyValuePair in solarCoverageSimulationResult.coverage)
                {
                    coverage[keyValuePair.Key] = keyValuePair.Value;
                }
            }
        }

        public SolarCoverageSimulationResult(SolarCoverageSimulationResult solarCoverageSimulationResult)
    : base(solarCoverageSimulationResult)
        {
            if (solarCoverageSimulationResult?.coverage != null)
            {
                foreach (KeyValuePair<DateTime, double> keyValuePair in solarCoverageSimulationResult.coverage)
                {
                    coverage[keyValuePair.Key] = keyValuePair.Value;
                }
            }
        }

        public SolarCoverageSimulationResult(JsonObject jObject) 
            : base(jObject)
        {

        }

        public SolarCoverageSimulationResult(SolarCoverageSimulationResult solarCoverageSimulationResult, IEnumerable<DateTime> dateTimes)
            : base(solarCoverageSimulationResult)
        {
            if (solarCoverageSimulationResult == null)
            {
                return;
            }

            if (solarCoverageSimulationResult.coverage != null)
            {
                coverage = new Dictionary<DateTime, double>();
                foreach (KeyValuePair<DateTime, double> keyValuePair in solarCoverageSimulationResult.coverage)
                {
                    if (dateTimes != null && !dateTimes.Contains(keyValuePair.Key))
                    {
                        continue;
                    }

                    coverage[keyValuePair.Key] = keyValuePair.Value;
                }
            }
        }

        public List<DateTime> DateTimes
        {
            get
            {
                if(coverage == null)
                {
                    return null;
                }

                return coverage.Keys.ToList();
            }
        }

        public List<double> Values
        {
            get
            {
                if (coverage == null)
                {
                    return null;
                }

                return coverage.Values.ToList();
            }
        }

        public List<Tuple<DateTime, double>> Coverage
        {
            get
            {
                if (coverage == null)
                {
                    return null;
                }

                List<Tuple<DateTime, double>> result = new List<Tuple<DateTime, double>>(coverage.Count);
                foreach (KeyValuePair<DateTime, double> keyValuePair in coverage)
                {
                    result.Add(Tuple.Create(keyValuePair.Key, keyValuePair.Value));
                }
                return result;
            }
        }

        public int Count
        {
            get
            {
                return coverage?.Count ?? 0;
            }
        }

        public double this[DateTime dateTime]
        {
            get
            {
                return GetCoverage(dateTime);
            }
        }

        public double GetCoverage(DateTime dateTime)
        {
            if(coverage.TryGetValue(dateTime, out double result))
            {
                return result;
            }

            return double.NaN;
        }

        public override bool FromJsonObject(JsonObject jObject)
        {
            if (!base.FromJsonObject(jObject))
                return false;

            if(jObject.ContainsKey("Coverage"))
            {
                coverage = new Dictionary<DateTime, double>();

                JsonArray jArray_Coverage = jObject["Coverage"] as JsonArray;
                if(jArray_Coverage != null)
                {
                    for(int i =0; i < jArray_Coverage.Count; i++)
                    {
                        JsonArray jArray = jArray_Coverage[i] as JsonArray;
                        if(jArray == null || jArray.Count < 2)
                        {
                            continue;
                        }

                        DateTime dateTime = jArray[0]?.GetValue<DateTime>() ?? default;
                        double value = jArray[1]?.GetValue<double>() ?? default;

                        coverage[dateTime] = value;
                    }
                }
            }

            return true;
        }

        public override JsonObject ToJsonObject()
        {
            JsonObject jObject = base.ToJsonObject();
            if (jObject == null)
                return null;

            if(coverage != null)
            {
                JsonArray jArray_Coverage = new JsonArray();
                foreach(KeyValuePair<DateTime, double> keyValuePair in coverage)
                {
                    JsonArray jArray = new JsonArray
                    {
                        keyValuePair.Key,
                        keyValuePair.Value
                    };

                    jArray_Coverage.Add(jArray);
                }

                jObject.Add("Coverage", jArray_Coverage);
            }

            return jObject;
        }
    }
}
