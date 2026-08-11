// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Text.Json.Nodes;
using SAM.Core;
using SAM.Core.SolarCalculator;

namespace SAM.Analytical.SolarCalculator
{
    /// <summary>
    /// One search variable: a typology parameter, the range Stage 9 may move it over, and the
    /// granularity it is meaningful at.
    ///
    /// The granularity is not decoration. Some typology parameters are integers in disguise —
    /// HorizontalLouvres.Count is rounded to an int before any geometry is built — so the objective
    /// over them is a staircase, and a search that keeps halving its step below 1 would spend its
    /// evaluations re-measuring identical devices. Snapping every proposal to Step makes the search
    /// space finite and the result exactly reproducible, because two runs cannot land on
    /// almost-equal parameter vectors that round to different geometry.
    ///
    /// Bounds are always intersected with the typology's OWN declared bounds when the parameter set
    /// is built, so Stage 9 can never propose a value the typology would silently clamp — a clamp
    /// would make the reported parameters differ from the geometry actually evaluated.
    /// </summary>
    public class ShadingParameter : IJSAMObject, ISolarObject
    {
        private string name;
        private double minimum = double.NaN;
        private double maximum = double.NaN;
        private double step = double.NaN;

        public ShadingParameter(string name, double minimum, double maximum, double step)
        {
            this.name = name;
            this.minimum = minimum;
            this.maximum = maximum;
            this.step = step;
        }

        public ShadingParameter(ShadingParameter shadingParameter)
        {
            if (shadingParameter != null)
            {
                name = shadingParameter.name;
                minimum = shadingParameter.minimum;
                maximum = shadingParameter.maximum;
                step = shadingParameter.step;
            }
        }

        public ShadingParameter(JsonObject jObject)
        {
            FromJsonObject(jObject);
        }

        public string Name { get { return name; } }

        public double Minimum { get { return minimum; } }

        public double Maximum { get { return maximum; } }

        /// <summary>Smallest meaningful change. Every proposal is snapped to a multiple of it.</summary>
        public double Step { get { return step; } }

        public double Range { get { return maximum - minimum; } }

        /// <summary>True when the range is a single point — nothing for the search to do.</summary>
        public bool IsFixed { get { return !(Range > 0); } }

        /// <summary>Clamps into range and snaps onto the step lattice measured from the minimum.</summary>
        public double Snap(double value)
        {
            if (double.IsNaN(value))
            {
                return minimum;
            }

            double clamped = Math.Min(maximum, Math.Max(minimum, value));
            if (double.IsNaN(step) || step <= 0)
            {
                return clamped;
            }

            double snapped = minimum + Math.Round((clamped - minimum) / step) * step;
            return Math.Min(maximum, Math.Max(minimum, snapped));
        }

        public bool FromJsonObject(JsonObject jObject)
        {
            if (jObject == null)
            {
                return false;
            }

            if (jObject.ContainsKey("Name")) { name = jObject["Name"]?.GetValue<string>(); }
            if (jObject.ContainsKey("Minimum")) { minimum = jObject["Minimum"]?.GetValue<double>() ?? double.NaN; }
            if (jObject.ContainsKey("Maximum")) { maximum = jObject["Maximum"]?.GetValue<double>() ?? double.NaN; }
            if (jObject.ContainsKey("Step")) { step = jObject["Step"]?.GetValue<double>() ?? double.NaN; }

            return true;
        }

        public JsonObject ToJsonObject()
        {
            JsonObject jObject = new JsonObject();
            jObject.Add("_type", Core.Query.FullTypeName(this));
            if (name != null) { jObject.Add("Name", name); }
            jObject.Add("Minimum", minimum);
            jObject.Add("Maximum", maximum);
            jObject.Add("Step", step);
            return jObject;
        }
    }
}
