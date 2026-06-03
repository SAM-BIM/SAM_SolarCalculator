// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using SAM.Geometry.SolarCalculator;
using SAM.Geometry.Spatial;
using System;
using System.Collections.Generic;

namespace SAM.Analytical.SolarCalculator
{
    public static partial class Modify
    {
        public static List<SolarFaceSimulationResult> Simulate(this AnalyticalModel analyticalModel, IEnumerable<DateTime> dateTimes, bool merge = false, double minHorizonAngle = Core.Tolerance.Angle, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance, double sampleSize = double.NaN)
        {
            if(analyticalModel == null || dateTimes == null)
            {
                return null;
            }

            Core.Location location = analyticalModel.Location;
            if (location == null)
            {
                return null;
            }

            Dictionary<DateTime, Vector3D> directionDictionary = new Dictionary<DateTime, Vector3D>();
            foreach (DateTime dateTime in dateTimes)
            {
                directionDictionary[dateTime] = Geometry.SolarCalculator.Query.SunDirection(location, dateTime, false);
            }

            return Simulate(analyticalModel, directionDictionary, merge, minHorizonAngle, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance, sampleSize);
        }

        public static List<SolarFaceSimulationResult> Simulate(this AnalyticalModel analyticalModel, Dictionary<DateTime, Vector3D> directionDictionary, bool merge = false, double minHorizonAngle = Core.Tolerance.Angle, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance, double sampleSize = double.NaN)
        {
            if (analyticalModel == null || directionDictionary == null)
            {
                return null;
            }

            SolarModel solarModel = Convert.ToSAM_SolarModel(analyticalModel);
            if (solarModel == null)
            {
                return null;
            }

            List<SolarFaceSimulationResult> result = null;

            List<SolarFaceSimulationResult> solarFaceSimulationResults = Weather.SolarCalculator.Modify.Simulate(solarModel, directionDictionary, true, minHorizonAngle, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance, sampleSize);
            if (solarFaceSimulationResults != null && solarFaceSimulationResults.Count != 0)
            {
                result = new List<SolarFaceSimulationResult>();

                List<Panel> panels = analyticalModel.GetPanels();
                foreach (SolarFaceSimulationResult solarFaceSimulationResult in solarFaceSimulationResults)
                {
                    Guid guid = Guid.Empty;

                    Panel panel = panels.Find(x => x.Guid.ToString().Equals(solarFaceSimulationResult.Reference));
                    if (panel != null)
                    {
                        guid = panel.Guid;
                    }

                    if(!merge)
                    {
                        analyticalModel.AddResult<Panel>(solarFaceSimulationResult, guid);
                        result.Add(solarFaceSimulationResult);
                        continue;
                    }

                    List<SolarFaceSimulationResult> solarFaceSimulationResuls_Panel = analyticalModel.GetRelatedObjects<SolarFaceSimulationResult>(panel);
                    if(solarFaceSimulationResuls_Panel == null || solarFaceSimulationResuls_Panel.Count == 0)
                    {
                        analyticalModel.AddResult<Panel>(solarFaceSimulationResult, guid);
                        result.Add(solarFaceSimulationResult);
                        continue;
                    }

                    foreach(SolarFaceSimulationResult solarFaceSimulationResult_Panel in solarFaceSimulationResuls_Panel)
                    {
                        SolarFaceSimulationResult solarFaceSimulationResult_New = solarFaceSimulationResult_Panel.Merge(solarFaceSimulationResult);
                        if(solarFaceSimulationResult_New == null)
                        {
                            continue;
                        }

                        analyticalModel.AddResult<Panel>(solarFaceSimulationResult_New, guid);
                        result.Add(solarFaceSimulationResult_New);
                    }
                }
            }

            return solarFaceSimulationResults;
        }

        /// <summary>
        /// Coverage-only simulate: emits <see cref="SolarCoverageSimulationResult"/> instead of
        /// <see cref="SolarFaceSimulationResult"/> and attaches them to the AnalyticalModel.
        /// Designed for apples-to-apples comparison against TAS-imported shade coverage.
        /// </summary>
        /// <remarks>
        /// Binary-compatibility overload: preserves the original pre-<c>useModelSolarModel</c>
        /// signature so plugins/apps already compiled against it keep resolving at runtime
        /// (appending the optional flag in-place would be a binary break — MissingMethodException).
        /// Delegates with <c>useModelSolarModel = false</c>.
        /// </remarks>
        public static List<SolarCoverageSimulationResult> Simulate_Coverage(this AnalyticalModel analyticalModel, IEnumerable<DateTime> dateTimes, double minHorizonAngle = Core.Tolerance.Angle, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance, double sampleSize = double.NaN)
        {
            return Simulate_Coverage(analyticalModel, dateTimes, minHorizonAngle, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance, sampleSize, false);
        }

        /// <remarks>
        /// Binary-compatibility overload — see the IEnumerable&lt;DateTime&gt; overload above. Delegates
        /// with <c>useModelSolarModel = false</c>.
        /// </remarks>
        public static List<SolarCoverageSimulationResult> Simulate_Coverage(this AnalyticalModel analyticalModel, Dictionary<DateTime, Vector3D> directionDictionary, double minHorizonAngle = Core.Tolerance.Angle, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance, double sampleSize = double.NaN)
        {
            return Simulate_Coverage(analyticalModel, directionDictionary, minHorizonAngle, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance, sampleSize, false);
        }

        /// <summary>
        /// Coverage-only simulate with control over the surface set. When <paramref name="useModelSolarModel"/>
        /// is true, SAM recomputes coverage on the SolarModel already attached to the AnalyticalModel
        /// (e.g. the TAS-imported surfaces); otherwise it derives panels from the AdjacencyCluster as usual.
        /// </summary>
        public static List<SolarCoverageSimulationResult> Simulate_Coverage(this AnalyticalModel analyticalModel, IEnumerable<DateTime> dateTimes, double minHorizonAngle = Core.Tolerance.Angle, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance, double sampleSize = double.NaN, bool useModelSolarModel = false)
        {
            if (analyticalModel == null || dateTimes == null)
            {
                return null;
            }

            Core.Location location = analyticalModel.Location;
            if (location == null)
            {
                return null;
            }

            Dictionary<DateTime, Vector3D> directionDictionary = new Dictionary<DateTime, Vector3D>();
            foreach (DateTime dateTime in dateTimes)
            {
                directionDictionary[dateTime] = Geometry.SolarCalculator.Query.SunDirection(location, dateTime, false);
            }

            return Simulate_Coverage(analyticalModel, directionDictionary, minHorizonAngle, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance, sampleSize, useModelSolarModel);
        }

        /// <summary>
        /// Coverage-only simulate with control over the surface set — see the IEnumerable&lt;DateTime&gt;
        /// overload. When <paramref name="useModelSolarModel"/> is true, reuses the attached SolarModel's
        /// geometry instead of the AdjacencyCluster-derived panel set.
        /// </summary>
        public static List<SolarCoverageSimulationResult> Simulate_Coverage(this AnalyticalModel analyticalModel, Dictionary<DateTime, Vector3D> directionDictionary, double minHorizonAngle = Core.Tolerance.Angle, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance, double sampleSize = double.NaN, bool useModelSolarModel = false)
        {
            if (analyticalModel == null || directionDictionary == null)
            {
                return null;
            }

            // useModelSolarModel: recompute coverage on the SolarModel ALREADY attached to the model
            // (e.g. the TAS-imported surfaces) instead of re-deriving panels from the AdjacencyCluster.
            // Guarantees SAM evaluates the exact same faces as the imported model — a 1:1 benchmark set.
            SolarModel solarModel = useModelSolarModel ? GeometryOnlySolarModel(analyticalModel) : Convert.ToSAM_SolarModel(analyticalModel);
            if (solarModel == null)
            {
                return null;
            }

            List<SolarCoverageSimulationResult> solarCoverageSimulationResults = Weather.SolarCalculator.Modify.Simulate_Coverage(solarModel, directionDictionary, minHorizonAngle, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance, sampleSize);

            // Attach the populated SolarModel to the AnalyticalModel so downstream nodes
            // (e.g. a comparison node) can pull it back out, regardless of whether the model
            // was TAS-imported or SAM-computed — both paths land in the same parameter slot.
            analyticalModel.SetValue(AnalyticalModelParameter.SolarModel, solarModel);

            if (solarCoverageSimulationResults == null || solarCoverageSimulationResults.Count == 0)
            {
                return solarCoverageSimulationResults;
            }

            List<Panel> panels = analyticalModel.GetPanels();
            foreach (SolarCoverageSimulationResult solarCoverageSimulationResult in solarCoverageSimulationResults)
            {
                Guid guid = Guid.Empty;
                Panel panel = panels?.Find(x => x.Guid.ToString().Equals(solarCoverageSimulationResult.Reference));
                if (panel != null)
                {
                    guid = panel.Guid;
                }

                analyticalModel.AddResult<Panel>(solarCoverageSimulationResult, guid);
            }

            return solarCoverageSimulationResults;
        }

        /// <summary>
        /// Builds a fresh SolarModel that carries ONLY the geometry (LinkedFace3Ds) of the SolarModel
        /// already attached to the AnalyticalModel under <see cref="AnalyticalModelParameter.SolarModel"/>
        /// — e.g. the TAS-imported surfaces. Any existing results are intentionally dropped so that a
        /// subsequent coverage simulation recomputes SAM coverage on the EXACT same faces, giving a
        /// 1:1 benchmark surface set instead of the AdjacencyCluster-filtered panel set. Returns null
        /// when no SolarModel is attached or it has no usable geometry.
        /// </summary>
        private static SolarModel GeometryOnlySolarModel(AnalyticalModel analyticalModel)
        {
            SolarModel existing = analyticalModel?.GetValue<SolarModel>(AnalyticalModelParameter.SolarModel);
            if (existing == null)
            {
                return null;
            }

            List<Geometry.Object.Spatial.LinkedFace3D> linkedFace3Ds = existing.GetLinkedFace3Ds();
            if (linkedFace3Ds == null || linkedFace3Ds.Count == 0)
            {
                return null;
            }

            SolarModel result = new SolarModel(analyticalModel.Location);
            foreach (Geometry.Object.Spatial.LinkedFace3D linkedFace3D in linkedFace3Ds)
            {
                if (linkedFace3D?.Face3D == null)
                {
                    continue;
                }

                result.Add(linkedFace3D);
            }

            return result;
        }

        public static List<SolarFaceSimulationResult> Simulate(this BuildingModel buildingModel, IEnumerable<DateTime> dateTimes, double minHorizonAngle = Core.Tolerance.Angle, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance, double sampleSize = double.NaN)
        {
            if (buildingModel == null || dateTimes == null)
            {
                return null;
            }

            SolarModel solarModel = Convert.ToSAM_SolarModel(buildingModel);
            if (solarModel == null)
            {
                return null;
            }

            List<SolarFaceSimulationResult> result = Weather.SolarCalculator.Modify.Simulate(solarModel, dateTimes, true ,minHorizonAngle, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance, sampleSize);
            if (result != null && result.Count != 0)
            {
                List<IPartition> partitions = buildingModel.GetPartitions();
                foreach (SolarFaceSimulationResult solarFaceSimulationResult in result)
                {
                    Guid guid = Guid.Empty;

                    IPartition partition = partitions.Find(x => x.Guid.ToString().Equals(solarFaceSimulationResult.Reference));
                    if (partition != null)
                    {
                        guid = partition.Guid;
                    }

                    buildingModel.Add<IPartition>(solarFaceSimulationResult, guid);
                }
            }

            return result;
        }
    }
}
