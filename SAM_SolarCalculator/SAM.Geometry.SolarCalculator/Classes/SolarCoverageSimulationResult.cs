// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System.Text.Json.Nodes;
using SAM.Core;
using SAM.Core.SolarCalculator;
using SAM.Geometry.Spatial;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SAM.Geometry.SolarCalculator
{
    public class SolarCoverageSimulationResult : Result, ISolarSimulationResult
    {
        private Dictionary<DateTime, float> coverage = new Dictionary<DateTime, float>();

        public SolarCoverageSimulationResult(string name, string source, string reference, IEnumerable<Tuple<DateTime, float>> coverage)
            : base(name, source, reference)
        {
            if(coverage != null)
            {
                foreach(Tuple<DateTime, float> tuple in coverage)
                {
                    this.coverage[tuple.Item1] = tuple.Item2;
                }
            }
        }

        public SolarCoverageSimulationResult(SolarCoverageSimulationResult solarCoverageSimulationResult)
            : base(solarCoverageSimulationResult)
        {
            if(solarCoverageSimulationResult?.coverage != null)
            {
                foreach(KeyValuePair<DateTime, float> keyValuePair in solarCoverageSimulationResult.coverage)
                {
                    coverage[keyValuePair.Key] = keyValuePair.Value;
                }
            }
        }

        public SolarCoverageSimulationResult(JsonObject jObject) 
            : base(jObject)
        {

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

        public List<float> Values
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

        public List<Tuple<DateTime, float>> Coverage
        {
            get
            {
                if (coverage == null)
                {
                    return null;
                }

                List<Tuple<DateTime, float>> result = new List<Tuple<DateTime, float>>(coverage.Count);
                foreach (KeyValuePair<DateTime, float> keyValuePair in coverage)
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

        public float this[DateTime dateTime]
        {
            get
            {
                return GetCoverage(dateTime);
            }
        }

        public float GetCoverage(DateTime dateTime)
        {
            if(coverage.TryGetValue(dateTime, out float result))
            {
                return result;
            }

            return float.NaN;
        }

        public override bool FromJsonObject(JsonObject jObject)
        {
            if (!base.FromJsonObject(jObject))
                return false;

            if(jObject.ContainsKey("Coverage"))
            {
                coverage = new Dictionary<DateTime, float>();

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
                        float value = jArray[1]?.GetValue<float>() ?? default;

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
                foreach(KeyValuePair<DateTime, float> keyValuePair in coverage)
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
