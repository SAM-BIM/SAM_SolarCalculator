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
    public class SolarFaceSimulationResult : Result, ISolarObject
    {
        private Face3D face3D;
        private List<Tuple<DateTime, Radiation, List<Face3D>>> sunExposure;

        public SolarFaceSimulationResult(string name, string source, string reference, Face3D face3D, IEnumerable<Tuple<DateTime, Radiation, List<Face3D>>> sunExposure)
            : base(name, source, reference)
        {
            this.face3D = face3D?.Clone<Face3D>();
            if(sunExposure != null)
            {
                this.sunExposure = new List<Tuple<DateTime, Radiation, List<Spatial.Face3D>>>();
                foreach(Tuple<DateTime, Radiation, List<Spatial.Face3D>> tuple in sunExposure)
                {
                    this.sunExposure.Add(new Tuple<DateTime, Radiation, List<Spatial.Face3D>>(
                        tuple.Item1, 
                        tuple.Item2?.Clone(),
                        tuple?.Item3 == null ? null : tuple.Item3.ConvertAll(x => new Spatial.Face3D(x))));
                }
            }
        }

        public SolarFaceSimulationResult(SolarFaceSimulationResult solarFaceSimulationResult, IEnumerable<Tuple<DateTime, Radiation, List<Face3D>>> sunExposure)
        : base(solarFaceSimulationResult)
        {
            face3D = solarFaceSimulationResult?.face3D?.Clone<Face3D>();

            if (sunExposure != null)
            {
                this.sunExposure = new List<Tuple<DateTime, Radiation, List<Spatial.Face3D>>>();
                foreach (Tuple<DateTime, Radiation, List<Spatial.Face3D>> tuple in sunExposure)
                {
                    this.sunExposure.Add(new Tuple<DateTime, Radiation, List<Spatial.Face3D>>(tuple.Item1, tuple.Item2?.Clone(), tuple?.Item3 == null ? null : tuple.Item3.ConvertAll(x => new Spatial.Face3D(x))));
                }
            }
        }

        public SolarFaceSimulationResult(SolarFaceSimulationResult solarFaceSimulationResult, Dictionary<DateTime, Tuple<Radiation, List<Face3D>>> sunExposure)
            : base(solarFaceSimulationResult)
        {
            face3D = solarFaceSimulationResult?.face3D?.Clone<Face3D>();
            if (sunExposure != null)
            {
                this.sunExposure = new List<Tuple<DateTime, Radiation, List<Spatial.Face3D>>>();
                foreach (DateTime dateTime in sunExposure.Keys)
                {
                    this.sunExposure.Add(new Tuple<DateTime, Radiation, List<Spatial.Face3D>>(dateTime, sunExposure[dateTime].Item1?.Clone(), sunExposure[dateTime].Item2?.ConvertAll(x => new Spatial.Face3D(x))));
                }
            }
        }

        public SolarFaceSimulationResult(JsonObject jObject) 
            : base(jObject)
        {
        }

        public SolarFaceSimulationResult(SolarFaceSimulationResult solarFaceSimulationResult)
            :base(solarFaceSimulationResult)
        {
            if(solarFaceSimulationResult == null)
            {
                return;
            }

            face3D = solarFaceSimulationResult.face3D?.Clone<Face3D>();

            if(solarFaceSimulationResult.sunExposure != null)
            {
                sunExposure = new List<Tuple<DateTime, Radiation, List<Spatial.Face3D>>>();
                foreach(Tuple<DateTime, Radiation, List<Spatial.Face3D>> tuple in solarFaceSimulationResult.sunExposure)
                {
                    sunExposure.Add(new Tuple<DateTime, Radiation, List<Spatial.Face3D>>(tuple.Item1, tuple.Item2?.Clone(), tuple?.Item3 == null ? null : tuple.Item3.ConvertAll(x => new Spatial.Face3D(x))));
                }
            }
        }

        public SolarFaceSimulationResult(SolarFaceSimulationResult solarFaceSimulationResult, IEnumerable<DateTime> dateTimes)
            : base(solarFaceSimulationResult)
        {
            if (solarFaceSimulationResult == null)
            {
                return;
            }

            face3D = solarFaceSimulationResult.face3D?.Clone<Face3D>();

            if (solarFaceSimulationResult.sunExposure != null)
            {
                sunExposure = new List<Tuple<DateTime, Radiation, List<Spatial.Face3D>>>();
                foreach (Tuple<DateTime, Radiation, List<Spatial.Face3D>> tuple in solarFaceSimulationResult.sunExposure)
                {
                    if(dateTimes != null && !dateTimes.Contains(tuple.Item1))
                    {
                        continue;
                    }
                    
                    sunExposure.Add(new Tuple<DateTime, Radiation, List<Spatial.Face3D>>(tuple.Item1, tuple.Item2, tuple?.Item3 == null ? null : tuple.Item3.ConvertAll(x => new Spatial.Face3D(x))));
                }
            }
        }

        public List<Face3D> GetSunExposureFace3Ds(DateTime dateTime)
        {
            if(sunExposure == null)
            {
                return null;
            }

            Tuple<DateTime, Radiation, List<Face3D>> tuple = sunExposure.Find(x => x.Item1.Equals(dateTime));
            return tuple?.Item3?.ConvertAll(x => new Face3D(x));
        }

        public Radiation GetRadiation(DateTime dateTime)
        {
            if (sunExposure == null)
            {
                return null;
            }

            Tuple<DateTime, Radiation, List<Face3D>> tuple = sunExposure.Find(x => x.Item1.Equals(dateTime));
            return tuple?.Item2?.Clone();
        }

        public double GetSunExposureArea(DateTime dateTime)
        {
            if (sunExposure == null)
            {
                return 0;
            }

            Tuple<DateTime, Radiation, List<Face3D>> tuple = sunExposure.Find(x => x.Item1.Equals(dateTime));
            if(tuple == null || tuple.Item2 == null || tuple.Item3.Count == 0)
            {
                return 0;
            }

            return tuple.Item3.ConvertAll(x => x.GetArea()).Sum();
        }

        public Face3D Face3D
        {
            get
            {
                return face3D?.Clone<Face3D>();
            }
        }

        public List<DateTime> DateTimes
        {
            get
            {
                if(sunExposure == null)
                {
                    return null;
                }

                return sunExposure.ConvertAll(x => x.Item1);
            }
        }

        public List<double> TotalRadiations
        {
            get
            {
                if(sunExposure == null)
                {
                    return null;
                }

                List<double> result = new List<double>();
                foreach(Tuple<DateTime, Radiation, List<Face3D>> tuple in sunExposure)
                {
                    result.Add(tuple.Item2 == null ? double.NaN : tuple.Item2.GetTotal());
                }

                return result;
            }
        }

        public List<double> IncidentRadiations
        {
            get
            {
                if (sunExposure == null || face3D == null || sunExposure == null)
                {
                    return null;
                }

                double area = face3D.GetArea();
                if(double.IsNaN(area) || area == 0)
                {
                    return null;
                }

                List<double> result = new List<double>();
                foreach (Tuple<DateTime, Radiation, List<Face3D>> tuple in sunExposure)
                {
                    double radiation = tuple.Item2 == null ? double.NaN : tuple.Item2.GetTotal();

                    double sunExposureArea = GetSunExposureArea(tuple.Item1);

                    result.Add(radiation * sunExposureArea / area);
                }

                return result;
            }
        }

        public override bool FromJsonObject(JsonObject jObject)
        {
            if (!base.FromJsonObject(jObject))
                return false;

            if(jObject.ContainsKey("SunExposure"))
            {
                sunExposure = new List<Tuple<DateTime, Radiation, List<Spatial.Face3D>>>();

                JsonArray jArray_SunExposure = jObject["SunExposure"] as JsonArray;
                if(jArray_SunExposure != null)
                {
                    for(int i =0; i < jArray_SunExposure.Count; i++)
                    {
                        JsonArray jArray = jArray_SunExposure[i] as JsonArray;
                        if(jArray == null || jArray.Count < 2)
                        {
                            continue;
                        }

                        DateTime dateTime = jArray[0]?.GetValue<DateTime>() ?? default(DateTime);
                        List<Spatial.Face3D> face3Ds = Core.Create.IJSAMObjects<Spatial.Face3D>(jArray[1] as JsonArray);

                        Radiation radiation = jArray.Count <= 2 ? null : Core.Create.IJSAMObject<Radiation>(jArray[2] as JsonObject);

                        sunExposure.Add(new Tuple<DateTime, Radiation, List<Spatial.Face3D>>(dateTime, radiation, face3Ds));
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

            if(sunExposure != null)
            {
                JsonArray jArray_SunExposure = new JsonArray();
                foreach(Tuple<DateTime, Radiation, List<Spatial.Face3D>> tuple in sunExposure)
                {
                    if (tuple == null)
                    {
                        continue;
                    }

                    JsonArray jArray = new JsonArray();
                    jArray.Add(tuple.Item1);

                    JsonArray face3DsArray = new JsonArray();
                    if (tuple?.Item3 != null)
                    {
                        foreach (Spatial.Face3D face3D in tuple.Item3)
                            face3DsArray.Add(face3D?.ToJsonObject());
                    }
                    jArray.Add(face3DsArray);

                    if(tuple.Item2 != null)
                    {
                        jArray.Add(tuple.Item2.ToJsonObject());
                    }

                    jArray_SunExposure.Add(jArray);
                }

                jObject.Add("SunExposure", jArray_SunExposure);
            }

            return jObject;
        }
    }
}
