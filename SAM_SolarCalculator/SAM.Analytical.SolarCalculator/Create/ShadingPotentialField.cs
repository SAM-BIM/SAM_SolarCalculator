// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SAM.Geometry.SolarCalculator;
using SAM.Geometry.Spatial;
using SAM.Weather.SolarCalculator;

namespace SAM.Analytical.SolarCalculator
{
    public static partial class Create
    {
        /// <summary>
        /// Stage 6: the shading potential field. Marches the direct sun of every sun group from
        /// every lit analysis cell through the ShadingVolume's voxel grid, accumulating the
        /// desirability-weighted beam energy each voxel would intercept:
        ///
        ///   for each sun group g:
        ///       uw = UnwantedEnergy[g], wn = WantedEnergy[g]   (Stage 5, kWh/m2, energy-weighted)
        ///       if |uw| + |wn| ~ 0: skip
        ///       for each analysis cell a lit at g (the Stage 2 lit bits — a cell already shaded
        ///       by existing context contributes NOTHING, so a voxel never gets credit for
        ///       blocking sun that context already blocks):
        ///           march the ray from the cell's interior point toward the sun through the grid
        ///           (Amanatides-Woo DDA, NOT per-voxel intersection tests)
        ///           for each voxel v entered:
        ///               Unwanted[v] += area(a) x uw
        ///               Wanted[v]   += area(a) x wn
        ///
        /// Raw voxel units are kWh of aperture-plane beam energy over the desirability-weighted
        /// year. Each entered voxel receives the FULL cell contribution: the score answers "if
        /// material were placed at v, how much (un)wanted energy would it intercept", which is
        /// independent per voxel. Positive Score = worth filling; negative = must stay open.
        ///
        /// Determinism: sun groups are partitioned into contiguous, ordered ranges; each range
        /// accumulates into its own sparse accumulator; the accumulators are merged in range
        /// order — the field is bit-identical across runs and thread counts (no per-voxel locking,
        /// no Interlocked). Voxel candidate ordering is the ShadingVolume's fixed linear index.
        ///
        /// The field inherits the analysis resolution: gridSize in the aperture plane (one ray per
        /// analysis cell, from its guaranteed-interior point, exactly as the visibility cache
        /// tests it) and sunAngleStep in direction space (group-centre directions).
        /// </summary>
        /// <param name="target">The aperture (cells + local frame come from it).</param>
        /// <param name="solarVisibilityCache">Sun groups + per-cell lit bits.</param>
        /// <param name="desirability">Stage 5 per-group unwanted/wanted energies for this aperture.</param>
        /// <param name="volume">The candidate voxel grid.</param>
        /// <param name="cellIndexOffset">First cache cell index belonging to this target (0 when the cache was built for this target alone; the offset within a whole-model cache otherwise).</param>
        public static ShadingPotentialField ShadingPotentialField(this ApertureSolarTarget target, SolarVisibilityCache solarVisibilityCache, ApertureDesirability desirability, ShadingVolume volume, int cellIndexOffset = 0)
        {
            List<AnalysisCell> cells = target?.AnalysisCells;
            if (cells == null || cells.Count == 0 || solarVisibilityCache == null || desirability == null || volume == null || volume.VoxelCount <= 0)
            {
                return null;
            }

            List<SunBin> bins = solarVisibilityCache.Bins;
            double[] unwantedGroups = desirability.UnwantedEnergyPerGroup;
            double[] wantedGroups = desirability.WantedEnergyPerGroup;
            if (bins == null || unwantedGroups == null || wantedGroups == null || unwantedGroups.Length != bins.Count || wantedGroups.Length != bins.Count)
            {
                return null;
            }

            int voxelCount = volume.VoxelCount;
            int cellCount = cells.Count;
            if (cellIndexOffset < 0 || cellIndexOffset + cellCount > solarVisibilityCache.CellCount)
            {
                return null;
            }

            Point3D[] cellPoints = new Point3D[cellCount];
            double[] cellAreas = new double[cellCount];
            for (int c = 0; c < cellCount; c++)
            {
                cellPoints[c] = cells[c]?.InternalPoint3D;
                cellAreas[c] = cells[c]?.Area ?? 0;
            }

            // Ordered, contiguous bin ranges: parallelism changes nothing about the result.
            int rangeCount = Math.Min(bins.Count, Math.Max(1, Environment.ProcessorCount * 2));
            List<Tuple<int, int>> ranges = new List<Tuple<int, int>>(rangeCount);
            int perRange = (int)Math.Ceiling((double)bins.Count / rangeCount);
            for (int start = 0; start < bins.Count; start += perRange)
            {
                ranges.Add(new Tuple<int, int>(start, Math.Min(bins.Count, start + perRange)));
            }

            Dictionary<int, double>[] rangeUnwanted = new Dictionary<int, double>[ranges.Count];
            Dictionary<int, double>[] rangeWanted = new Dictionary<int, double>[ranges.Count];

            ShadingVolume volume_Local = volume;
            SolarVisibilityCache cache_Local = solarVisibilityCache;

            Parallel.For(0, ranges.Count, r =>
            {
                Dictionary<int, double> localUnwanted = new Dictionary<int, double>();
                Dictionary<int, double> localWanted = new Dictionary<int, double>();

                for (int b = ranges[r].Item1; b < ranges[r].Item2; b++)
                {
                    double uw = unwantedGroups[b];
                    double wn = wantedGroups[b];
                    if (Math.Abs(uw) + Math.Abs(wn) < 1e-12)
                    {
                        continue;
                    }

                    Vector3D towardSun = bins[b]?.RepresentativeDirection?.GetNegated();
                    if (towardSun == null || !towardSun.IsValid())
                    {
                        continue;
                    }

                    towardSun = towardSun.Unit;

                    // March direction in the volume-local frame (orthonormal axes: dot products).
                    double dx = towardSun.X * volume_Local.AxisX.X + towardSun.Y * volume_Local.AxisX.Y + towardSun.Z * volume_Local.AxisX.Z;
                    double dy = towardSun.X * volume_Local.AxisY.X + towardSun.Y * volume_Local.AxisY.Y + towardSun.Z * volume_Local.AxisY.Z;
                    double dz = towardSun.X * volume_Local.AxisZ.X + towardSun.Y * volume_Local.AxisZ.Y + towardSun.Z * volume_Local.AxisZ.Z;

                    for (int c = 0; c < cellCount; c++)
                    {
                        if (cellPoints[c] == null || !cache_Local.IsLit(b, cellIndexOffset + c))
                        {
                            continue;
                        }

                        if (!volume_Local.TryToLocal(cellPoints[c], out double sx, out double sy, out double sz))
                        {
                            continue;
                        }

                        double area = cellAreas[c];
                        double contributionUnwanted = area * uw;
                        double contributionWanted = area * wn;

                        March(volume_Local, sx, sy, sz, dx, dy, dz, voxelIndex =>
                        {
                            localUnwanted.TryGetValue(voxelIndex, out double u);
                            localUnwanted[voxelIndex] = u + contributionUnwanted;
                            localWanted.TryGetValue(voxelIndex, out double w);
                            localWanted[voxelIndex] = w + contributionWanted;
                        });
                    }
                }

                rangeUnwanted[r] = localUnwanted;
                rangeWanted[r] = localWanted;
            });

            // Ordered reduction: bit-identical across runs and thread counts.
            double[] unwanted = new double[voxelCount];
            double[] wanted = new double[voxelCount];
            for (int r = 0; r < ranges.Count; r++)
            {
                foreach (KeyValuePair<int, double> pair in rangeUnwanted[r])
                {
                    unwanted[pair.Key] += pair.Value;
                }

                foreach (KeyValuePair<int, double> pair in rangeWanted[r])
                {
                    wanted[pair.Key] += pair.Value;
                }
            }

            return new ShadingPotentialField(target.ApertureGuid, desirability.DesirabilityStrategyName, solarVisibilityCache.CellSize, solarVisibilityCache.BinSizeDegrees, solarVisibilityCache.SunPositionShiftInMinutes, volume, unwanted, wanted);
        }

        /// <summary>
        /// Amanatides-Woo 3-D DDA traversal of the volume grid by a ray given in local
        /// coordinates. Every voxel the ray TRAVERSES (positive path length inside the voxel) is
        /// reported exactly once, in march order, until the ray leaves the grid. Rays starting
        /// outside are advanced to the grid entry point (slab test); rays missing the grid, or
        /// merely touching it tangentially at a corner/edge, report nothing.
        ///
        /// Lattice-degenerate starts are handled explicitly. Analysis-cell ray origins routinely
        /// land EXACTLY on a voxel plane (any gridSize that is a multiple of voxelSize does it —
        /// 0.5 / 0.25 is the obvious case), and there the naive floor() start voxel is wrong
        /// whenever the ray steps negatively on that axis: floor() names the voxel on the far side
        /// of the plane, which the ray only touches with zero path length. StartVoxel steps such an
        /// axis back by one so the first reported voxel is the one actually entered. Without this
        /// the field over-credits a shell of zero-thickness grazes along the aperture lattice.
        /// </summary>
        private static void March(ShadingVolume volume, double sx, double sy, double sz, double dx, double dy, double dz, Action<int> visit)
        {
            double voxelSize = volume.VoxelSize;
            int countX = volume.CountX;
            int countY = volume.CountY;
            int countZ = volume.CountZ;

            double extentX = countX * voxelSize;
            double extentY = countY * voxelSize;
            double extentZ = countZ * voxelSize;

            // Slab entry/exit against the grid box [0, extent] on each axis.
            double tEnter = 0.0;
            double tExit = double.PositiveInfinity;
            if (!Slab(sx, dx, extentX, ref tEnter, ref tExit) ||
                !Slab(sy, dy, extentY, ref tEnter, ref tExit) ||
                !Slab(sz, dz, extentZ, ref tEnter, ref tExit))
            {
                return;
            }

            double t = Math.Max(tEnter, 0.0);

            // A ray that only grazes the grid (corner/edge touch) traverses no voxel at all.
            if (tExit - t <= LatticeTolerance)
            {
                return;
            }

            double px = sx + dx * t;
            double py = sy + dy * t;
            double pz = sz + dz * t;

            int stepX = dx > 0 ? 1 : -1;
            int stepY = dy > 0 ? 1 : -1;
            int stepZ = dz > 0 ? 1 : -1;

            int i = StartVoxel(px, voxelSize, countX, stepX);
            int j = StartVoxel(py, voxelSize, countY, stepY);
            int k = StartVoxel(pz, voxelSize, countZ, stepZ);

            double tMaxX = FirstBoundary(px, dx, i, voxelSize, stepX);
            double tMaxY = FirstBoundary(py, dy, j, voxelSize, stepY);
            double tMaxZ = FirstBoundary(pz, dz, k, voxelSize, stepZ);

            double tDeltaX = dx == 0 ? double.PositiveInfinity : voxelSize / Math.Abs(dx);
            double tDeltaY = dy == 0 ? double.PositiveInfinity : voxelSize / Math.Abs(dy);
            double tDeltaZ = dz == 0 ? double.PositiveInfinity : voxelSize / Math.Abs(dz);

            double tRemaining = tExit - t;

            while (true)
            {
                visit(volume.VoxelIndex(i, j, k));

                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ)
                    {
                        if (tMaxX > tRemaining) { break; }
                        i += stepX;
                        t = tMaxX;
                        tMaxX += tDeltaX;
                        if (i < 0 || i >= countX) { break; }
                    }
                    else
                    {
                        if (tMaxZ > tRemaining) { break; }
                        k += stepZ;
                        t = tMaxZ;
                        tMaxZ += tDeltaZ;
                        if (k < 0 || k >= countZ) { break; }
                    }
                }
                else
                {
                    if (tMaxY < tMaxZ)
                    {
                        if (tMaxY > tRemaining) { break; }
                        j += stepY;
                        t = tMaxY;
                        tMaxY += tDeltaY;
                        if (j < 0 || j >= countY) { break; }
                    }
                    else
                    {
                        if (tMaxZ > tRemaining) { break; }
                        k += stepZ;
                        t = tMaxZ;
                        tMaxZ += tDeltaZ;
                        if (k < 0 || k >= countZ) { break; }
                    }
                }
            }
        }

        private static bool Slab(double s, double d, double extent, ref double tEnter, ref double tExit)
        {
            const double epsilon = 1e-15;
            if (Math.Abs(d) < epsilon)
            {
                return s >= 0.0 && s <= extent;
            }

            double t1 = (0.0 - s) / d;
            double t2 = (extent - s) / d;
            if (t1 > t2)
            {
                double temp = t1;
                t1 = t2;
                t2 = temp;
            }

            if (t1 > tEnter) { tEnter = t1; }
            if (t2 < tExit) { tExit = t2; }
            return tEnter <= tExit;
        }

        /// <summary>Lattice-coincidence tolerance, in voxel-size units (dimensionless).</summary>
        private const double LatticeTolerance = 1e-9;

        /// <summary>
        /// The voxel a ray actually ENTERS at its grid-entry point, given the per-axis step sign.
        /// Ordinarily floor(p / voxelSize). When p lies exactly on a voxel plane and the ray steps
        /// negatively on that axis, floor() names the voxel the ray is leaving rather than the one
        /// it enters, so the index is decremented. Clamped into range against float drift at the
        /// slab-entry point.
        /// </summary>
        private static int StartVoxel(double p, double voxelSize, int count, int step)
        {
            double q = p / voxelSize;
            int index = (int)Math.Floor(q);

            if (step < 0)
            {
                double nearest = Math.Round(q);
                if (Math.Abs(q - nearest) < LatticeTolerance)
                {
                    index = (int)nearest - 1;
                }
            }

            if (index < 0) { return 0; }
            if (index >= count) { return count - 1; }
            return index;
        }

        private static double FirstBoundary(double p, double d, int index, double voxelSize, int step)
        {
            if (d == 0)
            {
                return double.PositiveInfinity;
            }

            double boundary = step > 0 ? (index + 1) * voxelSize : index * voxelSize;
            return (boundary - p) / d;
        }
    }
}
