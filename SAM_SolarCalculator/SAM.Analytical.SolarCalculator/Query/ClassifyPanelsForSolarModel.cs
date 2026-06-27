// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using SAM.Geometry.Spatial;
using System.Collections.Generic;

namespace SAM.Analytical.SolarCalculator
{
    public static partial class Query
    {
        /// <summary>
        /// Classifies every panel of an AnalyticalModel by the SAME rule
        /// <see cref="Convert.ToSAM_SolarModel(AnalyticalModel)"/> uses to decide which panels enter
        /// the SolarModel — but instead of silently dropping panels, it records the verdict and reason
        /// for each. Use it to explain why SAM's coverage covers fewer surfaces than a TAS import:
        /// a panel is kept only when it is single-space (not adjacent to ≥2 spaces) AND of a
        /// sun-exposed <see cref="PanelType"/>.
        /// </summary>
        public static List<PanelSolarClassification> ClassifyPanelsForSolarModel(this AnalyticalModel analyticalModel)
        {
            if (analyticalModel == null)
            {
                return null;
            }

            AdjacencyCluster adjacencyCluster = analyticalModel.AdjacencyCluster;
            if (adjacencyCluster == null)
            {
                return null;
            }

            List<Panel> panels = adjacencyCluster.GetPanels();
            if (panels == null)
            {
                return null;
            }

            List<PanelSolarClassification> result = new List<PanelSolarClassification>();
            foreach (Panel panel in panels)
            {
                if (panel == null)
                {
                    continue;
                }

                Point3D internalPoint3D = panel.Face3D?.InternalPoint3D();

                List<Space> spaces = adjacencyCluster.GetSpaces(panel);
                bool isInternal = spaces != null && spaces.Count >= 2;
                bool isExposed = panel.IsExposedToSun();

                bool kept = !isInternal && isExposed;

                // Mirror ToSAM_SolarModel's order of checks: internal is tested first, so an internal
                // panel is reported as internal even if its PanelType would also be non-sun-exposed.
                string dropReason;
                if (kept)
                {
                    dropReason = PanelSolarClassification.Reason_Kept;
                }
                else if (isInternal)
                {
                    dropReason = PanelSolarClassification.Reason_Internal;
                }
                else
                {
                    dropReason = PanelSolarClassification.Reason_NotSunExposed;
                }

                result.Add(new PanelSolarClassification(panel.Guid, panel.PanelType, internalPoint3D, kept, dropReason));
            }

            return result;
        }
    }

    /// <summary>
    /// One panel's verdict from <see cref="Query.ClassifyPanelsForSolarModel(AnalyticalModel)"/>:
    /// whether it would enter the SolarModel and, if not, why.
    /// </summary>
    public class PanelSolarClassification
    {
        public const string Reason_Kept = "kept";
        public const string Reason_Internal = "internal (>= 2 spaces)";
        public const string Reason_NotSunExposed = "non-sun-exposed PanelType";

        public PanelSolarClassification(System.Guid guid, PanelType panelType, Point3D internalPoint3D, bool kept, string dropReason)
        {
            Guid = guid;
            PanelType = panelType;
            InternalPoint3D = internalPoint3D;
            Kept = kept;
            DropReason = dropReason;
        }

        public System.Guid Guid { get; }

        public PanelType PanelType { get; }

        public Point3D InternalPoint3D { get; }

        public bool Kept { get; }

        public string DropReason { get; }
    }
}
