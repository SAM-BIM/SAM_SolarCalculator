// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;
using SAM.Core.SolarCalculator;
using SAM.Geometry.Object.Spatial;
using SAM.Geometry.SolarCalculator;
using SAM.Weather.SolarCalculator;

namespace SAM.Analytical.SolarCalculator
{
    public static partial class Create
    {
        /// <summary>
        /// Stage 8: the energy performance of a set of shading elements against one aperture,
        /// attributed to the element the sun physically reaches FIRST.
        ///
        /// The accounting rule, and the reason the baseline cache is a required argument:
        ///
        ///   V_A = the rays admitted with existing context in place and NO candidate
        ///         (exactly the Stage 0-4 lit bits of baseVisibilityCache)
        ///
        /// Only rays in V_A can be credited. For each of them the attribution cache — built over
        /// context PLUS the candidate elements — says which face the sun hits first. If it is one
        /// of the candidate's elements, that element is credited with the ray's energy. If it is a
        /// context face, the baseline and the attribution disagree about the same geometry, which
        /// should be impossible; that energy goes to UnattributedInterceptedEnergy rather than
        /// being folded into a total, so the discrepancy is visible instead of hidden.
        ///
        /// Because attribution is first-hit, overlapping elements never double-count: a ray stopped
        /// by a louvre that would also have met the fin behind it credits the louvre alone, and the
        /// per-element sum plus the residual reconciles exactly with DirectSolarIntercepted.
        ///
        /// Units: Stage 5 energies are kWh/m2 per sun group, multiplied here by analysis-cell area,
        /// so every energy is kWh.
        /// </summary>
        /// <param name="target">The aperture.</param>
        /// <param name="baseVisibilityCache">Visibility with CONTEXT ONLY: defines what the candidate may be credited for.</param>
        /// <param name="attributionCache">First hit over context PLUS the candidate elements, same sun groups and cells.</param>
        /// <param name="desirability">Stage 5 per-group energies for this aperture.</param>
        /// <param name="shadingElements">The candidate. Only these Guids may be credited.</param>
        /// <param name="typologyName">Provenance label.</param>
        /// <param name="materialFraction">Device area / aperture gross area.</param>
        /// <param name="cellIndexOffset">First cache cell index belonging to this target.</param>
        public static ShadingPerformance ShadingPerformance(this ApertureSolarTarget target, SolarVisibilityCache baseVisibilityCache, SolarAttributionCache attributionCache, ApertureDesirability desirability, IEnumerable<ShadingElement> shadingElements, string typologyName, double materialFraction, int cellIndexOffset = 0)
        {
            List<AnalysisCell> cells = target?.AnalysisCells;
            if (cells == null || cells.Count == 0 || baseVisibilityCache == null || attributionCache == null || desirability == null)
            {
                return null;
            }

            List<SunBin> bins = baseVisibilityCache.Bins;
            double[] directGroups = desirability.DirectEnergyPerGroup;
            double[] unwantedGroups = desirability.UnwantedEnergyPerGroup;
            double[] wantedGroups = desirability.WantedEnergyPerGroup;
            if (bins == null || directGroups == null || unwantedGroups == null || wantedGroups == null
                || directGroups.Length != bins.Count || unwantedGroups.Length != bins.Count || wantedGroups.Length != bins.Count)
            {
                return null;
            }

            if (attributionCache.BinCount != bins.Count)
            {
                return null;
            }

            // Only these Guids may receive credit. Anything else hit first is context.
            Dictionary<Guid, double> energyPerElement = new Dictionary<Guid, double>();
            Dictionary<Guid, string> namePerElement = new Dictionary<Guid, string>();
            HashSet<Guid> candidateGuids = new HashSet<Guid>();
            foreach (ShadingElement element in shadingElements ?? new List<ShadingElement>())
            {
                if (element == null)
                {
                    continue;
                }

                candidateGuids.Add(element.Guid);
                energyPerElement[element.Guid] = 0.0;
                namePerElement[element.Guid] = element.Name;
            }

            double admittedDirect = 0, admittedUnwanted = 0, admittedWanted = 0;
            double interceptedDirect = 0, interceptedUnwanted = 0, blockedWanted = 0;
            double unattributed = 0;

            int cellCount = cells.Count;
            for (int b = 0; b < bins.Count; b++)
            {
                double direct = directGroups[b];
                double unwanted = unwantedGroups[b];
                double wanted = wantedGroups[b];
                if (Math.Abs(direct) + Math.Abs(unwanted) + Math.Abs(wanted) < 1e-12)
                {
                    continue;
                }

                for (int c = 0; c < cellCount; c++)
                {
                    int cacheCell = cellIndexOffset + c;
                    if (!baseVisibilityCache.IsLit(b, cacheCell))
                    {
                        continue; // context already blocked it: not the candidate's to claim
                    }

                    double area = cells[c]?.Area ?? 0;
                    if (area <= 0)
                    {
                        continue;
                    }

                    admittedDirect += area * direct;
                    admittedUnwanted += area * unwanted;
                    admittedWanted += area * wanted;

                    int firstHit = attributionCache.FirstHitIndex(b, cacheCell);
                    if (firstHit < 0)
                    {
                        continue; // still reaches the aperture
                    }

                    interceptedDirect += area * direct;
                    interceptedUnwanted += area * unwanted;
                    blockedWanted += area * wanted;

                    Guid guid = attributionCache.FirstHitGuid(b, cacheCell);
                    if (candidateGuids.Contains(guid))
                    {
                        energyPerElement[guid] += area * direct;
                    }
                    else
                    {
                        // A base-admitted ray stopped by something that is not the candidate.
                        unattributed += area * direct;
                    }
                }
            }

            return new ShadingPerformance(
                target.ApertureGuid, typologyName,
                admittedDirect, admittedUnwanted, admittedWanted,
                interceptedDirect, interceptedUnwanted, blockedWanted,
                unattributed, materialFraction, energyPerElement, namePerElement);
        }

        /// <summary>
        /// Convenience overload: builds the attribution cache for context plus the candidate and
        /// evaluates in one call. The element order is context first, then the candidate, so the
        /// occluder table is stable across candidates that share the same context.
        /// </summary>
        public static ShadingPerformance ShadingPerformance(this ApertureSolarTarget target, SolarVisibilityCache baseVisibilityCache, ApertureDesirability desirability, List<LinkedFace3D> contextOccluders, IShadingTypology typology)
        {
            List<ShadingElement> elements = typology?.ShadingElements(target);
            if (elements == null)
            {
                return null;
            }

            List<LinkedFace3D> occluders = new List<LinkedFace3D>(contextOccluders ?? new List<LinkedFace3D>());
            foreach (ShadingElement element in elements)
            {
                LinkedFace3D linkedFace3D = element?.LinkedFace3D;
                if (linkedFace3D != null)
                {
                    occluders.Add(linkedFace3D);
                }
            }

            SolarAttributionCache attributionCache = Weather.SolarCalculator.Create.SolarAttributionCache(baseVisibilityCache, occluders, target.AnalysisCells);
            if (attributionCache == null)
            {
                return null;
            }

            return ShadingPerformance(target, baseVisibilityCache, attributionCache, desirability, elements, typology.Name, typology.MaterialFraction(target));
        }
    }
}
