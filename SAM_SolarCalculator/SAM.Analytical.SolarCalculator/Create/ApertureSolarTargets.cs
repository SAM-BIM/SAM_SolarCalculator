// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using SAM.Core.SolarCalculator;
using SAM.Geometry.SolarCalculator;
using SAM.Geometry.Spatial;
using System;
using System.Collections.Generic;

namespace SAM.Analytical.SolarCalculator
{
    public static partial class Create
    {
        /// <summary>
        /// Builds the aperture-centric analysis targets for a model. When apertureGuids is null or
        /// empty, every aperture on a sun-exposed external (single-space) panel is selected —
        /// mirroring the panel filter in Convert.ToSAM_SolarModel. An explicit selection returns
        /// exactly the resolvable subset, minus apertures on two-space (interior) panels, which are
        /// always rejected (an internal aperture receives no direct sun and would produce
        /// physically meaningless results). Each target's Face3D is re-oriented to the resolved
        /// OUTWARD normal, so flipped aperture winding cannot invert the analysis.
        /// </summary>
        public static List<ApertureSolarTarget> ApertureSolarTargets(this AnalyticalModel analyticalModel, IEnumerable<Guid> apertureGuids = null, double cellSize = 0.5, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Distance = Core.Tolerance.Distance)
        {
            AdjacencyCluster adjacencyCluster = analyticalModel?.AdjacencyCluster;
            if (adjacencyCluster == null)
            {
                return null;
            }

            HashSet<Guid> selection = null;
            if (apertureGuids != null)
            {
                selection = new HashSet<Guid>(apertureGuids);
                if (selection.Count == 0)
                {
                    selection = null;
                }
            }

            List<Panel> panels = adjacencyCluster.GetPanels();
            if (panels == null || panels.Count == 0)
            {
                return null;
            }

            List<ApertureSolarTarget> result = new List<ApertureSolarTarget>();
            foreach (Panel panel in panels)
            {
                if (panel == null || !panel.HasApertures)
                {
                    continue;
                }

                bool explicitSelection = selection != null;

                // Two-space (interior) panels never produce targets: their apertures receive no
                // direct sun and would yield physically meaningless results. Applies to explicit
                // selections too — an explicitly requested internal aperture is rejected, not
                // silently analysed.
                List<Space> spaces = adjacencyCluster.GetSpaces(panel);
                if (spaces != null && spaces.Count >= 2)
                {
                    continue;
                }

                if (!explicitSelection)
                {
                    // Default selection: mirror Convert.ToSAM_SolarModel — sun-exposed panels that
                    // are not shared by two spaces.
                    if (!panel.IsExposedToSun())
                    {
                        continue;
                    }
                }

                List<Aperture> apertures = panel.Apertures;
                foreach (Aperture aperture in apertures)
                {
                    if (aperture == null)
                    {
                        continue;
                    }

                    if (explicitSelection && !selection.Contains(aperture.Guid))
                    {
                        continue;
                    }

                    IClosedPlanar3D externalEdge3D = aperture.GetExternalEdge3D();
                    if (externalEdge3D == null)
                    {
                        continue;
                    }

                    Vector3D outward = adjacencyCluster.OutwardNormal(panel, aperture, tolerance_Area, tolerance_Distance);
                    if (outward == null)
                    {
                        continue;
                    }

                    Face3D face3D = new Face3D(externalEdge3D);
                    Plane plane = face3D?.GetPlane();
                    if (plane?.Normal == null)
                    {
                        continue;
                    }

                    if (plane.Normal.DotProduct(outward) < 0)
                    {
                        face3D.FlipNormal(true);
                    }

                    List<AnalysisCell> analysisCells = Geometry.SolarCalculator.Query.AnalysisCells(face3D, cellSize, tolerance_Area, tolerance_Distance);
                    if (analysisCells == null || analysisCells.Count == 0)
                    {
                        continue;
                    }

                    result.Add(new ApertureSolarTarget(aperture.Guid, panel.Guid, face3D, analysisCells));
                }
            }

            return result;
        }
    }
}
