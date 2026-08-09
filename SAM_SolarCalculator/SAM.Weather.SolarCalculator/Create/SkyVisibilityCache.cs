// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using SAM.Core;
using SAM.Geometry.Object.Spatial;
using SAM.Geometry.SolarCalculator;
using SAM.Geometry.Spatial;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SAM.Weather.SolarCalculator
{
    public static partial class Create
    {
        /// <summary>
        /// Builds the per-cell sky/horizon/ground visibility cache by ray-casting every patch
        /// direction of the chosen subdivision once against the context. Accumulation is done in
        /// per-thread buffers and reduced, so there is no locking in the inner loop. Deterministic.
        /// </summary>
        /// <param name="occluders">Context faces (every face occludes, regardless of orientation).</param>
        /// <param name="analysisCells">Target cells (outward-oriented).</param>
        /// <param name="cellSize">Cell size the targets were subdivided with (identity only).</param>
        /// <param name="skyPatchSubdivision">Patch set (Tregenza145 default; Reinhart577 for validation).</param>
        public static SkyVisibilityCache SkyVisibilityCache(List<LinkedFace3D> occluders, List<AnalysisCell> analysisCells, double cellSize, SkyPatchSubdivision skyPatchSubdivision = SkyPatchSubdivision.Tregenza145, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance)
        {
            if (analysisCells == null || analysisCells.Count == 0)
            {
                return null;
            }

            List<SkyPatch> skyPatches = Geometry.SolarCalculator.Query.SkyPatchDirections(skyPatchSubdivision);
            List<SkyPatch> groundPatches = Geometry.SolarCalculator.Query.GroundPatchDirections(skyPatchSubdivision);
            if (skyPatches == null || skyPatches.Count == 0 || groundPatches == null || groundPatches.Count == 0)
            {
                return null;
            }

            List<Point3D> points = new List<Point3D>(analysisCells.Count);
            List<Vector3D> normals = new List<Vector3D>(analysisCells.Count);
            List<Face3D> cellFaces = new List<Face3D>(analysisCells.Count);
            foreach (AnalysisCell analysisCell in analysisCells)
            {
                points.Add(analysisCell?.InternalPoint3D);
                Face3D face3D = analysisCell?.Face3D;
                cellFaces.Add(face3D);
                normals.Add(face3D?.GetPlane()?.Normal?.Unit);
            }

            string geometryHash = Geometry.SolarCalculator.Query.GeometryHash(occluders, cellFaces, tolerance_Distance);

            int cellCount = analysisCells.Count;

            // Per-patch cosine x solid-angle weights, precomputed per cell on demand inside the pass.
            List<SkyPatch> allPatches = new List<SkyPatch>(skyPatches.Count + groundPatches.Count);
            allPatches.AddRange(skyPatches);
            allPatches.AddRange(groundPatches);

            double[] skyViewFactors = new double[cellCount];
            double[] horizonNumerators = new double[cellCount];
            double[] horizonDenominators = new double[cellCount];
            double[] groundViewFactors = new double[cellCount];

            object lockObject = new object();
            Parallel.For(0, allPatches.Count,
                () => new double[4 * cellCount],
                (p, state, buffer) =>
                {
                    SkyPatch skyPatch = allPatches[p];
                    Vector3D direction = skyPatch?.Direction;
                    if (direction == null || !direction.IsValid())
                    {
                        return buffer;
                    }

                    bool isGround = direction.Z < 0;

                    bool[] visible = Query.CellVisibility(occluders, points, normals, direction, tolerance_Area, tolerance_Angle, tolerance_Distance, tolerance_Snap);
                    if (visible == null)
                    {
                        return buffer;
                    }

                    double weight = skyPatch.SolidAngle;
                    for (int c = 0; c < cellCount; c++)
                    {
                        Vector3D normal = normals[c];
                        if (normal == null)
                        {
                            continue;
                        }

                        double cosTheta = normal.DotProduct(direction);
                        if (cosTheta <= 0)
                        {
                            continue;
                        }

                        double w = cosTheta * weight;
                        if (!isGround && skyPatch.IsHorizonBand)
                        {
                            buffer[3 * cellCount + c] += w;   // horizon denominator (all band patches)
                        }

                        if (!visible[c])
                        {
                            continue;
                        }

                        if (isGround)
                        {
                            buffer[2 * cellCount + c] += w;
                        }
                        else
                        {
                            buffer[0 * cellCount + c] += w;   // sky numerator
                            if (skyPatch.IsHorizonBand)
                            {
                                buffer[1 * cellCount + c] += w;   // horizon numerator (visible only)
                            }
                        }
                    }

                    return buffer;
                },
                buffer =>
                {
                    lock (lockObject)
                    {
                        for (int c = 0; c < cellCount; c++)
                        {
                            skyViewFactors[c] += buffer[0 * cellCount + c];
                            horizonNumerators[c] += buffer[1 * cellCount + c];
                            groundViewFactors[c] += buffer[2 * cellCount + c];
                            horizonDenominators[c] += buffer[3 * cellCount + c];
                        }
                    }
                });

            // Normalise: view factors are defined against the full cosine-weighted dome (pi
            // steradians per hemisphere). The horizon factor is normalised against the cell's own
            // visible-weighted band total so an unobstructed cell gets exactly 1.
            for (int c = 0; c < cellCount; c++)
            {
                skyViewFactors[c] /= Math.PI;
                groundViewFactors[c] /= Math.PI;
            }

            double[] horizonViewFactors = new double[cellCount];
            for (int c = 0; c < cellCount; c++)
            {
                horizonViewFactors[c] = horizonDenominators[c] > 0 ? horizonNumerators[c] / horizonDenominators[c] : 0;
            }

            return new SkyVisibilityCache(geometryHash, cellSize, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance, skyPatchSubdivision, cellCount, skyViewFactors, horizonViewFactors, groundViewFactors);
        }
    }
}
