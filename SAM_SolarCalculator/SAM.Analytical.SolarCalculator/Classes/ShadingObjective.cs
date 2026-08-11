// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Text.Json.Nodes;
using SAM.Core;
using SAM.Core.SolarCalculator;

namespace SAM.Analytical.SolarCalculator
{
    /// <summary>
    /// Which admitted energy the material cost is measured against. The cost term has to be an
    /// ENERGY so it can be added to the benefit and harm terms; this says which one.
    /// </summary>
    public enum MaterialCostReference
    {
        /// <summary>
        /// The whole admitted direct beam. The Stage 9 default. Non-zero whenever any device could
        /// do anything at all, so material always carries a real cost.
        /// </summary>
        AdmittedDirectEnergy,

        /// <summary>
        /// The admitted UNWANTED beam — what Stage 8's ShadingFitScore uses. Retained for
        /// comparability with Stage 8 results, but note that it collapses to zero on an aperture
        /// with no unwanted solar, taking the whole cost term with it.
        /// </summary>
        AdmittedUnwantedEnergy,
    }

    /// <summary>
    /// The Stage 9 objective: the explicit statement of what "better" means, kept separate from the
    /// search so the two can be reasoned about independently.
    ///
    /// <code>
    ///   Benefit = UnwantedSolarIntercepted                       [kWh]
    ///   Harm    = WantedSolarBlocked                             [kWh]
    ///   Cost    = MaterialFraction x ReferenceEnergy             [kWh]
    ///
    ///   Score   = Benefit - WantedSolarPenalty x Harm - MaterialPenalty x Cost    [kWh]
    /// </code>
    ///
    /// SIGNS. All three components are POSITIVE quantities and are named for what they are rather
    /// than which way they point. Benefit is added; Harm and Cost are subtracted. Adding Harm would
    /// reward a device for destroying the winter sun, which is the single easiest error to make
    /// here and the reason the term is called WantedSolarBlocked and not something neutral.
    ///
    /// UNITS. Every term is kWh, so the score is kWh and the two penalties are dimensionless
    /// weights. WantedSolarPenalty is an exchange rate: how many kWh of unwanted solar blocked is
    /// worth one kWh of wanted solar lost. MaterialPenalty is the share of the aperture's admitted
    /// beam a designer is willing to forgo per unit of MaterialFraction.
    ///
    /// NORMALISATION. The cost is scaled by an admitted energy rather than left as a bare fraction,
    /// which makes the whole objective scale linearly with the site's radiation. Two identical
    /// buildings under weather differing only in magnitude therefore get the same device, and the
    /// cost-to-benefit weighting stays a property of the brief instead of a property of how sunny
    /// it is. Measured in MaterialPenaltyTests: a 300x change in radiation moves the energy-scaled
    /// cost/benefit ratio by 0 %, and a bare-fraction cost/benefit ratio by 300x.
    ///
    /// WHY THE REFERENCE ENERGY DIFFERS FROM STAGE 8. Stage 8's ShadingFitScore scales material by
    /// AdmittedUnwantedEnergy. That is dimensionally fine and behaves well whenever there IS
    /// unwanted solar, but it goes to zero exactly when there is none — and an aperture with no
    /// unwanted solar is the case where Stage 9 most needs the cost term, because it is the case
    /// where the right answer is "build nothing". Stage 9 therefore defaults to
    /// AdmittedDirectEnergy. Stage 8's own behaviour is left unchanged.
    ///
    /// ZERO DENOMINATORS. The score is an absolute energy, so it needs no denominator and stays
    /// defined when there is no unwanted or no wanted solar. The reported PERCENTAGES on
    /// ShadingPerformance keep their own NaN-on-zero-denominator rule; nothing here converts a NaN
    /// percentage into a number.
    ///
    /// THE NULL DEVICE. Building nothing scores exactly 0: no benefit, no harm, no cost. Any
    /// candidate scoring below zero is worse than leaving the aperture alone, which is what lets
    /// Stage 9 answer "no shading is worth building" instead of returning the least-bad geometry.
    /// </summary>
    public class ShadingObjective : IJSAMObject, ISolarObject
    {
        private double wantedSolarPenalty = 1.0;
        private double materialPenalty = 0.1;
        private MaterialCostReference materialCostReference = MaterialCostReference.AdmittedDirectEnergy;

        public ShadingObjective()
        {
        }

        public ShadingObjective(double wantedSolarPenalty, double materialPenalty = 0.1, MaterialCostReference materialCostReference = MaterialCostReference.AdmittedDirectEnergy)
        {
            this.wantedSolarPenalty = wantedSolarPenalty;
            this.materialPenalty = materialPenalty;
            this.materialCostReference = materialCostReference;
        }

        public ShadingObjective(ShadingObjective shadingObjective)
        {
            if (shadingObjective != null)
            {
                wantedSolarPenalty = shadingObjective.wantedSolarPenalty;
                materialPenalty = shadingObjective.materialPenalty;
                materialCostReference = shadingObjective.materialCostReference;
            }
        }

        public ShadingObjective(JsonObject jObject)
        {
            FromJsonObject(jObject);
        }

        /// <summary>Lambda: kWh of unwanted solar blocked considered worth one kWh of wanted solar lost.</summary>
        public double WantedSolarPenalty { get { return wantedSolarPenalty; } }

        /// <summary>Mu: share of the reference energy charged per unit of MaterialFraction.</summary>
        public double MaterialPenalty { get { return materialPenalty; } }

        public MaterialCostReference MaterialCostReference { get { return materialCostReference; } }

        /// <summary>The admitted energy the material cost is measured against, kWh.</summary>
        public double ReferenceEnergy(ShadingPerformance performance)
        {
            if (performance == null)
            {
                return double.NaN;
            }

            return materialCostReference == MaterialCostReference.AdmittedUnwantedEnergy
                ? performance.AdmittedUnwantedEnergy
                : performance.AdmittedDirectEnergy;
        }

        /// <summary>Benefit: the unwanted beam the candidate intercepts, kWh. Always &gt;= 0.</summary>
        public double Benefit(ShadingPerformance performance)
        {
            return performance == null ? double.NaN : performance.UnwantedSolarIntercepted;
        }

        /// <summary>Harm: the wanted beam the candidate destroys, kWh. Always &gt;= 0.</summary>
        public double Harm(ShadingPerformance performance)
        {
            return performance == null ? double.NaN : performance.WantedSolarBlocked;
        }

        /// <summary>Cost: material as a share of the aperture, priced in kWh. Always &gt;= 0.</summary>
        public double Cost(ShadingPerformance performance)
        {
            if (performance == null)
            {
                return double.NaN;
            }

            double materialFraction = performance.MaterialFraction;
            if (double.IsNaN(materialFraction))
            {
                // An unmeasurable device is not a free one, but nothing here can price it, so it is
                // reported as no cost rather than guessed at. Callers comparing a device with a
                // known material quantity against one without are comparing unlike things.
                materialFraction = 0.0;
            }

            double reference = ReferenceEnergy(performance);
            return double.IsNaN(reference) ? 0.0 : materialFraction * reference;
        }

        /// <summary>The scalar objective, kWh. Higher is better; 0 is exactly "build nothing".</summary>
        public double Score(ShadingPerformance performance)
        {
            if (performance == null)
            {
                return double.NaN;
            }

            return Benefit(performance) - wantedSolarPenalty * Harm(performance) - materialPenalty * Cost(performance);
        }

        public bool FromJsonObject(JsonObject jObject)
        {
            if (jObject == null)
            {
                return false;
            }

            if (jObject.ContainsKey("WantedSolarPenalty")) { wantedSolarPenalty = jObject["WantedSolarPenalty"]?.GetValue<double>() ?? 1.0; }
            if (jObject.ContainsKey("MaterialPenalty")) { materialPenalty = jObject["MaterialPenalty"]?.GetValue<double>() ?? 0.1; }
            if (jObject.ContainsKey("MaterialCostReference"))
            {
                Enum.TryParse(jObject["MaterialCostReference"]?.GetValue<string>(), out materialCostReference);
            }

            return true;
        }

        public JsonObject ToJsonObject()
        {
            JsonObject jObject = new JsonObject();
            jObject.Add("_type", Core.Query.FullTypeName(this));
            jObject.Add("WantedSolarPenalty", wantedSolarPenalty);
            jObject.Add("MaterialPenalty", materialPenalty);
            jObject.Add("MaterialCostReference", materialCostReference.ToString());
            return jObject;
        }
    }
}
