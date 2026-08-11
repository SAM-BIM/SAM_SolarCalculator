// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SAM.Core;
using SAM.Core.SolarCalculator;
using SAM.Geometry.Object.Spatial;
using SAM.Geometry.SolarCalculator;
using SAM.Geometry.Spatial;

namespace SAM.Weather.SolarCalculator
{
    public static partial class Create
    {
        /// <summary>
        /// Builds the first-hit attribution cache over the SAME sun groups a visibility cache uses,
        /// so the two are index-compatible in the bin dimension: bin b means the same sun group in
        /// both.
        ///
        /// THE CELL DIMENSION IS A WINDOW, NOT A MIRROR. A visibility cache is normally built over
        /// the WHOLE model's apertures flattened into one cell space, while attribution is only ever
        /// wanted for the one aperture a candidate device sits in front of. So the cells given here
        /// are a CONTIGUOUS SLICE of that space, starting at cellIndexOffset, and the cache built
        /// from them is indexed LOCALLY: its cell 0 is the slice's first cell, i.e. the visibility
        /// cache's cell cellIndexOffset. A reader holding both must therefore address the visibility
        /// cache at cellIndexOffset + c and this cache at c — see Create.ShadingPerformance, which is
        /// the only place that pairing is made.
        ///
        /// Requiring the two cell counts to be EQUAL instead (which is what this did before) would
        /// mean attribution could only ever be built for a model with exactly one aperture, and
        /// would also cost a whole-model ray-cast per candidate device — thousands of times the work
        /// actually needed. The window is both the correct contract and the affordable one.
        ///
        /// The occluder GUID table is the DISTINCT occluder GUIDs in input order, and the stored
        /// index is an index into that table, not into the caller's list, so duplicate-GUID faces
        /// collapse onto one entry and the table hash pins the meaning of every stored value.
        ///
        /// Determinism: each sun group writes its own row and rows are never shared, so the
        /// Parallel.For has no accumulation order to disagree about.
        /// </summary>
        /// <param name="solarVisibilityCache">Supplies the sun groups and the sampling identity.</param>
        /// <param name="occluders">Context plus any candidate shading faces, in a stable order.</param>
        /// <param name="analysisCells">The window of cells to attribute, in visibility-cache order.</param>
        /// <param name="cellIndexOffset">Where that window starts in the visibility cache's cell space. 0 when the cache was built for these cells alone.</param>
        public static SolarAttributionCache SolarAttributionCache(this SolarVisibilityCache solarVisibilityCache, List<LinkedFace3D> occluders, List<AnalysisCell> analysisCells, int cellIndexOffset = 0, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance)
        {
            List<SunBin> bins = solarVisibilityCache?.Bins;
            if (bins == null || bins.Count == 0 || analysisCells == null || analysisCells.Count == 0)
            {
                return null;
            }

            // The window must lie inside the visibility cache's cell space, or the two could not be
            // read together at all.
            if (cellIndexOffset < 0 || cellIndexOffset + analysisCells.Count > solarVisibilityCache.CellCount)
            {
                return null;
            }

            List<Point3D> points = new List<Point3D>(analysisCells.Count);
            List<Vector3D> normals = new List<Vector3D>(analysisCells.Count);
            foreach (AnalysisCell analysisCell in analysisCells)
            {
                points.Add(analysisCell?.InternalPoint3D);
                normals.Add(analysisCell?.Face3D?.GetPlane()?.Normal?.Unit);
            }

            List<LinkedFace3D> occluders_Local = occluders ?? new List<LinkedFace3D>();

            // Distinct GUIDs in input order, plus the map from occluder position to table index.
            List<Guid> occluderGuids = new List<Guid>();
            Dictionary<Guid, int> guidToTableIndex = new Dictionary<Guid, int>();
            int[] occluderToTable = new int[occluders_Local.Count];
            for (int i = 0; i < occluders_Local.Count; i++)
            {
                Guid guid = occluders_Local[i] == null ? Guid.Empty : occluders_Local[i].Guid;
                if (!guidToTableIndex.TryGetValue(guid, out int tableIndex))
                {
                    tableIndex = occluderGuids.Count;
                    occluderGuids.Add(guid);
                    guidToTableIndex[guid] = tableIndex;
                }

                occluderToTable[i] = tableIndex;
            }

            int[][] firstHit = new int[bins.Count][];

            Parallel.For(0, bins.Count, b =>
            {
                Vector3D representativeDirection = bins[b]?.RepresentativeDirection;
                if (representativeDirection == null || !representativeDirection.IsValid())
                {
                    return;
                }

                int[] hits = Query.CellFirstHit(occluders_Local, points, normals, representativeDirection.GetNegated(), tolerance_Area, tolerance_Angle, tolerance_Distance, tolerance_Snap);
                if (hits == null)
                {
                    return;
                }

                // Translate occluder-list positions into stable table indices; sentinels pass through.
                for (int c = 0; c < hits.Length; c++)
                {
                    if (hits[c] >= 0)
                    {
                        hits[c] = occluderToTable[hits[c]];
                    }
                }

                firstHit[b] = hits;
            });

            string contextGeometryHash = Geometry.SolarCalculator.Query.GeometryHash(occluders_Local, tolerance_Distance);
            string targetGeometryHash = Geometry.SolarCalculator.Query.TargetHash(analysisCells, tolerance_Distance);

            return new SolarAttributionCache(
                contextGeometryHash, targetGeometryHash,
                solarVisibilityCache.CellSize, solarVisibilityCache.BinSizeDegrees,
                solarVisibilityCache.SunPositionShiftInMinutes, solarVisibilityCache.Year,
                analysisCells.Count, occluderGuids, firstHit, cellIndexOffset);
        }
    }
}
