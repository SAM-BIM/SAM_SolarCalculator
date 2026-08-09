// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using SAM.Core;
using SAM.Core.SolarCalculator;
using SAM.Geometry.SolarCalculator;

namespace SAM.Analytical.SolarCalculator
{
    /// <summary>
    /// Per-aperture irradiance over an AnalysisPeriod, from the cached-visibility evaluation.
    /// Units are explicit:
    ///   per-cell values        kWh/m2   (weather W/m2 integrated over whole hours / 1000)
    ///   aperture totals        kWh      (per-cell kWh/m2 x cell area, summed)
    ///   aperture averages      kWh/m2   (total kWh / gross cell-covered area)
    ///   sunlit hours           h        (count of weather-timeline hours with direct sun on the cell)
    /// Reference = aperture Guid (SolarFaceSimulationResult convention).
    /// </summary>
    public class ApertureIrradianceResult : Result, ISolarSimulationResult
    {
        private AnalysisPeriod analysisPeriod;
        private SkyModel skyModel = SkyModel.Undefined;
        private double cellSize = double.NaN;
        private double binSizeDegrees = double.NaN;
        private double albedo = double.NaN;
        private double timeShiftInMinutes;
        private double grossArea = double.NaN;
        private double[] cellAreas;
        private double[] direct;
        private double[] diffuse;
        private double[] groundReflected;
        private double[] sunlitHours;
        private List<DateTime> dateTimes;

        public ApertureIrradianceResult(string name, string source, string reference, AnalysisPeriod analysisPeriod, SkyModel skyModel, double cellSize, double binSizeDegrees, double albedo, double timeShiftInMinutes, double grossArea, double[] cellAreas, double[] direct, double[] diffuse, double[] groundReflected, double[] sunlitHours, IEnumerable<DateTime> dateTimes)
            : base(name, source, reference)
        {
            this.analysisPeriod = analysisPeriod == null ? null : new AnalysisPeriod(analysisPeriod);
            this.skyModel = skyModel;
            this.cellSize = cellSize;
            this.binSizeDegrees = binSizeDegrees;
            this.albedo = albedo;
            this.timeShiftInMinutes = timeShiftInMinutes;
            this.grossArea = grossArea;
            this.cellAreas = cellAreas == null ? null : (double[])cellAreas.Clone();
            this.direct = direct == null ? null : (double[])direct.Clone();
            this.diffuse = diffuse == null ? null : (double[])diffuse.Clone();
            this.groundReflected = groundReflected == null ? null : (double[])groundReflected.Clone();
            this.sunlitHours = sunlitHours == null ? null : (double[])sunlitHours.Clone();
            this.dateTimes = dateTimes == null ? null : new List<DateTime>(dateTimes);
        }

        public ApertureIrradianceResult(ApertureIrradianceResult apertureIrradianceResult)
            : base(apertureIrradianceResult)
        {
            if (apertureIrradianceResult != null)
            {
                analysisPeriod = apertureIrradianceResult.analysisPeriod == null ? null : new AnalysisPeriod(apertureIrradianceResult.analysisPeriod);
                skyModel = apertureIrradianceResult.skyModel;
                cellSize = apertureIrradianceResult.cellSize;
                binSizeDegrees = apertureIrradianceResult.binSizeDegrees;
                albedo = apertureIrradianceResult.albedo;
                timeShiftInMinutes = apertureIrradianceResult.timeShiftInMinutes;
                grossArea = apertureIrradianceResult.grossArea;
                cellAreas = apertureIrradianceResult.cellAreas == null ? null : (double[])apertureIrradianceResult.cellAreas.Clone();
                direct = apertureIrradianceResult.direct == null ? null : (double[])apertureIrradianceResult.direct.Clone();
                diffuse = apertureIrradianceResult.diffuse == null ? null : (double[])apertureIrradianceResult.diffuse.Clone();
                groundReflected = apertureIrradianceResult.groundReflected == null ? null : (double[])apertureIrradianceResult.groundReflected.Clone();
                sunlitHours = apertureIrradianceResult.sunlitHours == null ? null : (double[])apertureIrradianceResult.sunlitHours.Clone();
                dateTimes = apertureIrradianceResult.dateTimes == null ? null : new List<DateTime>(apertureIrradianceResult.dateTimes);
            }
        }

        public ApertureIrradianceResult(JsonObject jObject)
            : base(jObject)
        {
        }

        public AnalysisPeriod AnalysisPeriod
        {
            get
            {
                return analysisPeriod == null ? null : new AnalysisPeriod(analysisPeriod);
            }
        }

        public SkyModel SkyModel
        {
            get
            {
                return skyModel;
            }
        }

        public double GrossArea
        {
            get
            {
                return grossArea;
            }
        }

        public int CellCount
        {
            get
            {
                return direct?.Length ?? 0;
            }
        }

        /// <summary>Cell areas, m2.</summary>
        public double[] CellAreas
        {
            get
            {
                return cellAreas == null ? null : (double[])cellAreas.Clone();
            }
        }

        /// <summary>Direct-beam energy density per cell, kWh/m2.</summary>
        public double[] Direct
        {
            get
            {
                return direct == null ? null : (double[])direct.Clone();
            }
        }

        /// <summary>Diffuse-sky energy density per cell, kWh/m2.</summary>
        public double[] Diffuse
        {
            get
            {
                return diffuse == null ? null : (double[])diffuse.Clone();
            }
        }

        /// <summary>Ground-reflected energy density per cell, kWh/m2.</summary>
        public double[] GroundReflected
        {
            get
            {
                return groundReflected == null ? null : (double[])groundReflected.Clone();
            }
        }

        /// <summary>Total energy density per cell (direct + diffuse + ground-reflected), kWh/m2.</summary>
        public double[] Total
        {
            get
            {
                if (direct == null)
                {
                    return null;
                }

                double[] result = new double[direct.Length];
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = direct[i] + diffuse[i] + groundReflected[i];
                }

                return result;
            }
        }

        /// <summary>Hours with direct sun per cell.</summary>
        public double[] SunlitHours
        {
            get
            {
                return sunlitHours == null ? null : (double[])sunlitHours.Clone();
            }
        }

        /// <summary>Area-weighted aperture total energy, kWh.</summary>
        public double TotalEnergy
        {
            get
            {
                return AreaWeighted(direct) + AreaWeighted(diffuse) + AreaWeighted(groundReflected);
            }
        }

        /// <summary>Area-weighted direct-beam energy, kWh.</summary>
        public double DirectEnergy
        {
            get
            {
                return AreaWeighted(direct);
            }
        }

        /// <summary>Area-weighted diffuse-sky energy, kWh.</summary>
        public double DiffuseEnergy
        {
            get
            {
                return AreaWeighted(diffuse);
            }
        }

        /// <summary>Area-weighted ground-reflected energy, kWh.</summary>
        public double GroundReflectedEnergy
        {
            get
            {
                return AreaWeighted(groundReflected);
            }
        }

        /// <summary>Aperture-average energy density, kWh/m2.</summary>
        public double AverageIrradiance
        {
            get
            {
                double area = CellCoveredArea;
                return area > 0 ? TotalEnergy / area : double.NaN;
            }
        }

        /// <summary>Sum of cell areas (the discretised aperture area), m2.</summary>
        public double CellCoveredArea
        {
            get
            {
                if (cellAreas == null)
                {
                    return double.NaN;
                }

                double result = 0;
                foreach (double area in cellAreas)
                {
                    result += area;
                }

                return result;
            }
        }

        public List<DateTime> DateTimes
        {
            get
            {
                return dateTimes == null ? null : new List<DateTime>(dateTimes);
            }
        }

        public override bool FromJsonObject(JsonObject jObject)
        {
            if (!base.FromJsonObject(jObject))
            {
                return false;
            }

            if (jObject.ContainsKey("AnalysisPeriod"))
            {
                analysisPeriod = new AnalysisPeriod(jObject["AnalysisPeriod"] as JsonObject);
            }

            if (jObject.ContainsKey("SkyModel"))
            {
                Enum.TryParse(jObject["SkyModel"]?.GetValue<string>(), out skyModel);
            }

            if (jObject.ContainsKey("CellSize"))
            {
                cellSize = jObject["CellSize"]?.GetValue<double>() ?? double.NaN;
            }

            if (jObject.ContainsKey("BinSizeDegrees"))
            {
                binSizeDegrees = jObject["BinSizeDegrees"]?.GetValue<double>() ?? double.NaN;
            }

            if (jObject.ContainsKey("Albedo"))
            {
                albedo = jObject["Albedo"]?.GetValue<double>() ?? double.NaN;
            }

            if (jObject.ContainsKey("TimeShiftInMinutes"))
            {
                timeShiftInMinutes = jObject["TimeShiftInMinutes"]?.GetValue<double>() ?? default;
            }

            if (jObject.ContainsKey("GrossArea"))
            {
                grossArea = jObject["GrossArea"]?.GetValue<double>() ?? double.NaN;
            }

            cellAreas = Doubles(jObject, "CellAreas");
            direct = Doubles(jObject, "Direct");
            diffuse = Doubles(jObject, "Diffuse");
            groundReflected = Doubles(jObject, "GroundReflected");
            sunlitHours = Doubles(jObject, "SunlitHours");

            dateTimes = null;
            if (jObject.ContainsKey("DateTimes"))
            {
                JsonArray jArray = jObject["DateTimes"] as JsonArray;
                if (jArray != null)
                {
                    dateTimes = new List<DateTime>();
                    foreach (JsonNode jNode in jArray)
                    {
                        dateTimes.Add(jNode?.GetValue<DateTime>() ?? default);
                    }
                }
            }

            return true;
        }

        public override JsonObject ToJsonObject()
        {
            JsonObject jObject = base.ToJsonObject();
            if (jObject == null)
            {
                return null;
            }

            if (analysisPeriod != null)
            {
                jObject.Add("AnalysisPeriod", analysisPeriod.ToJsonObject());
            }

            jObject.Add("SkyModel", skyModel.ToString());
            jObject.Add("CellSize", cellSize);
            jObject.Add("BinSizeDegrees", binSizeDegrees);
            jObject.Add("Albedo", albedo);
            jObject.Add("TimeShiftInMinutes", timeShiftInMinutes);
            jObject.Add("GrossArea", grossArea);

            Add(jObject, "CellAreas", cellAreas);
            Add(jObject, "Direct", direct);
            Add(jObject, "Diffuse", diffuse);
            Add(jObject, "GroundReflected", groundReflected);
            Add(jObject, "SunlitHours", sunlitHours);

            if (dateTimes != null)
            {
                JsonArray jArray = new JsonArray();
                foreach (DateTime dateTime in dateTimes)
                {
                    jArray.Add(dateTime);
                }
                jObject.Add("DateTimes", jArray);
            }

            return jObject;
        }

        private double AreaWeighted(double[] values)
        {
            if (values == null || cellAreas == null)
            {
                return double.NaN;
            }

            double result = 0;
            for (int i = 0; i < values.Length; i++)
            {
                result += values[i] * cellAreas[i];
            }

            return result;
        }

        private static void Add(JsonObject jObject, string name, double[] values)
        {
            if (values == null)
            {
                return;
            }

            JsonArray jArray = new JsonArray();
            foreach (double value in values)
            {
                jArray.Add(value);
            }
            jObject.Add(name, jArray);
        }

        private static double[] Doubles(JsonObject jObject, string name)
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

            List<double> values = new List<double>(jArray.Count);
            foreach (JsonNode jNode in jArray)
            {
                values.Add(jNode?.GetValue<double>() ?? double.NaN);
            }

            return values.ToArray();
        }
    }
}
