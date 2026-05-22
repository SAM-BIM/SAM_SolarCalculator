// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using Grasshopper.Kernel;
using SAM.Analytical.Grasshopper.SolarCalculator.Properties;
using SAM.Core;
using SAM.Core.Grasshopper;
using SAM.Geometry.Object.Spatial;
using SAM.Geometry.SolarCalculator;
using SAM.Geometry.Spatial;
using System;
using System.Collections.Generic;

namespace SAM.Analytical.Grasshopper.SolarCalculator
{
    public class SAMAnalyticalCompareSolarCoverage : GH_SAMVariableOutputParameterComponent
    {
        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid => new Guid("0e3a2cf4-1d23-4f5e-9b7a-1c5e8d3f7a91");

        /// <summary>
        /// The latest version of this component
        /// </summary>
        public override string LatestComponentVersion => "1.0.0";

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon => Resources.SAM_SolarCalculator;

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        public SAMAnalyticalCompareSolarCoverage()
          : base("SAMAnalytical.CompareSolarCoverage", "SAMAnalytical.CompareSolarCoverage",
              "Compare SolarCoverageSimulationResults between two AnalyticalModels — typically one TAS-imported and one SAM-computed — to benchmark SAM's solar engine against TAS shade-proportion data.\nFaces are matched by Face3D centroid proximity within _tolerance_; unmatched faces are reported separately.\nPer matched pair, the per-DateTime delta (sam − tas) is computed and reduced to mean absolute, max absolute, and RMSE.",
              "SAM", "Solar")
        {
        }

        protected override GH_SAMParam[] Inputs
        {
            get
            {
                List<GH_SAMParam> result = new List<GH_SAMParam>();
                result.Add(new GH_SAMParam(new GooAnalyticalModelParam() { Name = "_analyticalModel_A", NickName = "_analyticalModel_A", Description = "First AnalyticalModel (typically TAS-imported, with SolarModel attached via AnalyticalModelParameter.SolarModel)", Access = GH_ParamAccess.item }, ParamVisibility.Binding));
                result.Add(new GH_SAMParam(new GooAnalyticalModelParam() { Name = "_analyticalModel_B", NickName = "_analyticalModel_B", Description = "Second AnalyticalModel (typically SAM-computed via SAMAnalytical.SolarSimulation with _coverageOnly_ = true)", Access = GH_ParamAccess.item }, ParamVisibility.Binding));

                global::Grasshopper.Kernel.Parameters.Param_Number tolerance = new global::Grasshopper.Kernel.Parameters.Param_Number() { Name = "_tolerance_", NickName = "_tolerance_", Description = "Centroid-match distance tolerance in metres. A face from model A is paired with the nearest face in model B whose centroid is within this distance. Default 0.5 m.", Access = GH_ParamAccess.item };
                tolerance.SetPersistentData(0.5);
                result.Add(new GH_SAMParam(tolerance, ParamVisibility.Voluntary));

                global::Grasshopper.Kernel.Parameters.Param_Boolean run = new global::Grasshopper.Kernel.Parameters.Param_Boolean() { Name = "_run", NickName = "_run", Description = "Run", Access = GH_ParamAccess.item };
                run.SetPersistentData(false);
                result.Add(new GH_SAMParam(run, ParamVisibility.Binding));

                return result.ToArray();
            }
        }

        protected override GH_SAMParam[] Outputs
        {
            get
            {
                List<GH_SAMParam> result = new List<GH_SAMParam>();
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_GenericObject() { Name = "linkedFace3Ds_A", NickName = "linkedFace3Ds_A", Description = "LinkedFace3Ds from model A that were successfully matched", Access = GH_ParamAccess.list }, ParamVisibility.Binding));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_GenericObject() { Name = "linkedFace3Ds_B", NickName = "linkedFace3Ds_B", Description = "LinkedFace3Ds from model B paired 1:1 with linkedFace3Ds_A", Access = GH_ParamAccess.list }, ParamVisibility.Binding));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_Number() { Name = "meanAbsDelta", NickName = "meanAbsDelta", Description = "Mean absolute coverage difference per matched pair, averaged over overlapping DateTimes", Access = GH_ParamAccess.list }, ParamVisibility.Binding));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_Number() { Name = "maxAbsDelta", NickName = "maxAbsDelta", Description = "Max absolute coverage difference per matched pair", Access = GH_ParamAccess.list }, ParamVisibility.Binding));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_Number() { Name = "rmse", NickName = "rmse", Description = "Root-mean-square error of (B − A) per matched pair", Access = GH_ParamAccess.list }, ParamVisibility.Binding));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_Number() { Name = "overlapCount", NickName = "overlapCount", Description = "Number of DateTime keys present in BOTH coverage results, per matched pair", Access = GH_ParamAccess.list }, ParamVisibility.Voluntary));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_GenericObject() { Name = "unmatched_A", NickName = "unmatched_A", Description = "LinkedFace3Ds from model A that had no neighbour within _tolerance_ in model B", Access = GH_ParamAccess.list }, ParamVisibility.Voluntary));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_Number() { Name = "overallMeanAbsDelta", NickName = "overallMeanAbsDelta", Description = "Mean absolute delta across ALL matched pairs and ALL overlapping DateTimes — a single benchmark scalar", Access = GH_ParamAccess.item }, ParamVisibility.Binding));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_Boolean() { Name = "successful", NickName = "successful", Description = "Successful?", Access = GH_ParamAccess.item }, ParamVisibility.Binding));
                return result.ToArray();
            }
        }

        protected override void SolveInstance(IGH_DataAccess dataAccess)
        {
            int index;
            int index_Successful = Params.IndexOfOutputParam("successful");
            if (index_Successful != -1)
                dataAccess.SetData(index_Successful, false);

            bool run = false;
            index = Params.IndexOfInputParam("_run");
            if (!dataAccess.GetData(index, ref run) || !run)
            {
                return;
            }

            AnalyticalModel analyticalModel_A = null;
            index = Params.IndexOfInputParam("_analyticalModel_A");
            if (index == -1 || !dataAccess.GetData(index, ref analyticalModel_A) || analyticalModel_A == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid analyticalModel_A");
                return;
            }

            AnalyticalModel analyticalModel_B = null;
            index = Params.IndexOfInputParam("_analyticalModel_B");
            if (index == -1 || !dataAccess.GetData(index, ref analyticalModel_B) || analyticalModel_B == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid analyticalModel_B");
                return;
            }

            double tolerance = 0.5;
            index = Params.IndexOfInputParam("_tolerance_");
            if (index != -1)
            {
                double tolerance_Temp = tolerance;
                if (dataAccess.GetData(index, ref tolerance_Temp) && !double.IsNaN(tolerance_Temp) && tolerance_Temp > 0)
                {
                    tolerance = tolerance_Temp;
                }
            }

            SolarModel solarModel_A = analyticalModel_A.GetValue<SolarModel>(AnalyticalModelParameter.SolarModel);
            SolarModel solarModel_B = analyticalModel_B.GetValue<SolarModel>(AnalyticalModelParameter.SolarModel);

            if (solarModel_A == null || solarModel_B == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Both AnalyticalModels must carry a SolarModel under AnalyticalModelParameter.SolarModel (run SAMAnalytical.FromTBD with _importSurfaceShades_ = true on one side, and SAMAnalytical.SolarSimulation with _coverageOnly_ = true on the other).");
                return;
            }

            List<Pair> pairs_A = ExtractPairs(solarModel_A);
            List<Pair> pairs_B = ExtractPairs(solarModel_B);

            if (pairs_A.Count == 0 || pairs_B.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "One or both SolarModels contain no SolarCoverageSimulationResults.");
                return;
            }

            // Match each pair from A to nearest (by centroid distance) in B, within tolerance.
            // Each B face can only be claimed once — greedy first-come matching, which is fine
            // when models share geometry.
            HashSet<int> usedB = new HashSet<int>();
            List<LinkedFace3D> matched_A = new List<LinkedFace3D>();
            List<LinkedFace3D> matched_B = new List<LinkedFace3D>();
            List<double> meanAbsDeltas = new List<double>();
            List<double> maxAbsDeltas = new List<double>();
            List<double> rmses = new List<double>();
            List<int> overlapCounts = new List<int>();
            List<LinkedFace3D> unmatched_A = new List<LinkedFace3D>();

            double sumAbsDelta_All = 0;
            int sumOverlap_All = 0;

            foreach (Pair pair_A in pairs_A)
            {
                int bestIndex = -1;
                double bestDistance = tolerance;
                for (int j = 0; j < pairs_B.Count; j++)
                {
                    if (usedB.Contains(j))
                    {
                        continue;
                    }

                    double distance = pair_A.Centroid.Distance(pairs_B[j].Centroid);
                    if (distance <= bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = j;
                    }
                }

                if (bestIndex == -1)
                {
                    unmatched_A.Add(pair_A.LinkedFace3D);
                    continue;
                }

                Pair pair_B = pairs_B[bestIndex];

                ComputeDeltaStats(pair_A.Result, pair_B.Result, out double meanAbs, out double maxAbs, out double rmse, out int overlap, out double sumAbs_Pair);
                if (overlap == 0)
                {
                    // B face is not consumed — another A face with overlapping DateTimes
                    // may still match it.
                    unmatched_A.Add(pair_A.LinkedFace3D);
                    continue;
                }

                usedB.Add(bestIndex);

                matched_A.Add(pair_A.LinkedFace3D);
                matched_B.Add(pair_B.LinkedFace3D);
                meanAbsDeltas.Add(meanAbs);
                maxAbsDeltas.Add(maxAbs);
                rmses.Add(rmse);
                overlapCounts.Add(overlap);
                sumAbsDelta_All += sumAbs_Pair;
                sumOverlap_All += overlap;
            }

            double overallMeanAbsDelta = sumOverlap_All == 0 ? double.NaN : sumAbsDelta_All / sumOverlap_All;

            index = Params.IndexOfOutputParam("linkedFace3Ds_A");
            if (index != -1) dataAccess.SetDataList(index, matched_A);

            index = Params.IndexOfOutputParam("linkedFace3Ds_B");
            if (index != -1) dataAccess.SetDataList(index, matched_B);

            index = Params.IndexOfOutputParam("meanAbsDelta");
            if (index != -1) dataAccess.SetDataList(index, meanAbsDeltas);

            index = Params.IndexOfOutputParam("maxAbsDelta");
            if (index != -1) dataAccess.SetDataList(index, maxAbsDeltas);

            index = Params.IndexOfOutputParam("rmse");
            if (index != -1) dataAccess.SetDataList(index, rmses);

            index = Params.IndexOfOutputParam("overlapCount");
            if (index != -1) dataAccess.SetDataList(index, overlapCounts);

            index = Params.IndexOfOutputParam("unmatched_A");
            if (index != -1) dataAccess.SetDataList(index, unmatched_A);

            index = Params.IndexOfOutputParam("overallMeanAbsDelta");
            if (index != -1) dataAccess.SetData(index, overallMeanAbsDelta);

            if (index_Successful != -1)
            {
                dataAccess.SetData(index_Successful, matched_A.Count > 0);
            }
        }

        /// <summary>
        /// Build a list of (LinkedFace3D, centroid, SolarCoverageSimulationResult) tuples from a SolarModel.
        /// Match is by Reference == linkedFace3D.Guid.ToString() — the convention used by both
        /// the TAS-import path (Create.SolarModel) and the SAM coverage-simulate path.
        /// </summary>
        private static List<Pair> ExtractPairs(SolarModel solarModel)
        {
            List<Pair> result = new List<Pair>();
            if (solarModel == null) return result;

            List<LinkedFace3D> linkedFace3Ds = solarModel.GetLinkedFace3Ds();
            List<SolarCoverageSimulationResult> coverageResults = solarModel.SolarCoverageSimulationResults;
            if (linkedFace3Ds == null || coverageResults == null) return result;

            Dictionary<string, SolarCoverageSimulationResult> dictionary_Result = new Dictionary<string, SolarCoverageSimulationResult>();
            foreach (SolarCoverageSimulationResult coverageResult in coverageResults)
            {
                if (coverageResult?.Reference == null) continue;
                dictionary_Result[coverageResult.Reference] = coverageResult;
            }

            foreach (LinkedFace3D linkedFace3D in linkedFace3Ds)
            {
                if (linkedFace3D?.Face3D == null) continue;
                if (!dictionary_Result.TryGetValue(linkedFace3D.Guid.ToString(), out SolarCoverageSimulationResult coverageResult)) continue;

                Point3D centroid = linkedFace3D.Face3D.GetCentroid();
                if (centroid == null) continue;

                result.Add(new Pair(linkedFace3D, centroid, coverageResult));
            }

            return result;
        }

        private static void ComputeDeltaStats(SolarCoverageSimulationResult a, SolarCoverageSimulationResult b, out double meanAbs, out double maxAbs, out double rmse, out int overlap, out double sumAbs)
        {
            meanAbs = double.NaN;
            maxAbs = double.NaN;
            rmse = double.NaN;
            overlap = 0;
            sumAbs = 0;

            if (a == null || b == null) return;

            List<DateTime> dateTimes_A = a.DateTimes;
            if (dateTimes_A == null || dateTimes_A.Count == 0) return;

            double sumSq = 0;
            double max = 0;
            foreach (DateTime dateTime in dateTimes_A)
            {
                float valueA = a[dateTime];
                float valueB = b[dateTime];
                if (float.IsNaN(valueA) || float.IsNaN(valueB)) continue;

                double diff = valueB - valueA;
                double absDiff = Math.Abs(diff);
                sumAbs += absDiff;
                sumSq += diff * diff;
                if (absDiff > max) max = absDiff;
                overlap++;
            }

            if (overlap == 0) return;

            meanAbs = sumAbs / overlap;
            maxAbs = max;
            rmse = Math.Sqrt(sumSq / overlap);
        }

        private sealed class Pair
        {
            public Pair(LinkedFace3D linkedFace3D, Point3D centroid, SolarCoverageSimulationResult result)
            {
                LinkedFace3D = linkedFace3D;
                Centroid = centroid;
                Result = result;
            }

            public LinkedFace3D LinkedFace3D { get; }
            public Point3D Centroid { get; }
            public SolarCoverageSimulationResult Result { get; }
        }
    }
}
