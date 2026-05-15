// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors
using System.Text.Json.Nodes;
using SAM.Core;
using SAM.Core.SolarCalculator;

namespace SAM.Geometry.SolarCalculator
{
    public class Radiation : IJSAMObject, ISolarObject
    {
        private double diffuseHorizontal;
        private double directNormal;
        private double globalHorizontal;

        public Radiation(double directNormal, double diffuseHorizontal, double globalHorizontal)
            : base()
        {
            this.directNormal = directNormal;
            this.diffuseHorizontal = diffuseHorizontal;
            this.globalHorizontal = globalHorizontal;
        }

        public Radiation(JsonObject jObject)
        {
            FromJsonObject(jObject);
        }

        public Radiation(Radiation radiation)
        {
            if (radiation != null)
            {
                directNormal = radiation.directNormal;
                diffuseHorizontal = radiation.diffuseHorizontal;
                globalHorizontal = radiation.globalHorizontal;
            }
        }

        public double DiffuseHorizontal
        {
            get
            {
                return diffuseHorizontal;
            }
        }

        public double DirectNormal
        {
            get
            {
                return directNormal;
            }
        }
       
        public double GlobalHorizontal
        {
            get
            {
                return globalHorizontal;
            }
        }
        
        public bool FromJsonObject(JsonObject jObject)
        {
            if (jObject == null)
                return false;

            if (jObject.ContainsKey("DirectNormal"))
            {
                directNormal = jObject["DirectNormal"]?.GetValue<double>() ?? default(double);
            }

            if (jObject.ContainsKey("DiffuseHorizontal"))
            {
                diffuseHorizontal = jObject["DiffuseHorizontal"]?.GetValue<double>() ?? default(double);
            }

            if (jObject.ContainsKey("GlobalHorizontal"))
            {
                globalHorizontal = jObject["GlobalHorizontal"]?.GetValue<double>() ?? default(double);
            }

            return true;
        }

        public double GetTotal()
        {
            return directNormal + diffuseHorizontal + globalHorizontal; 
        }
        
        public JsonObject ToJsonObject()
        {
            JsonObject jObject = new JsonObject();
            jObject.Add("_type", Core.Query.FullTypeName(this));

            if(!double.IsNaN(directNormal))
            {
                jObject.Add("DirectNormal", directNormal);
            }

            if (!double.IsNaN(diffuseHorizontal))
            {
                jObject.Add("DiffuseHorizontal", diffuseHorizontal);
            }

            if (!double.IsNaN(globalHorizontal))
            {
                jObject.Add("GlobalHorizontal", globalHorizontal);
            }

            return jObject;
        }
    }
}
