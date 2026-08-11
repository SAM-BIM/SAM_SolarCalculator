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
        ///
        /// THE TWO CACHES ARE INDEXED DIFFERENTLY, and this is the only place the pairing is made.
        /// The visibility cache normally spans the WHOLE model's apertures, so this target's cells
        /// start at cellIndexOffset within it. The attribution cache is built for this target alone
        /// and is indexed locally from zero. Reading both at the same index — which is what this did
        /// before — silently scored one aperture against another aperture's admitted beam whenever a
        /// model had more than one window.
        /// </summary>
        /// <param name="target">The aperture.</param>
        /// <param name="baseVisibilityCache">Visibility with CONTEXT ONLY: defines what the candidate may be credited for. Addressed at cellIndexOffset + c.</param>
        /// <param name="attributionCache">First hit over context PLUS the candidate elements, same sun groups, THIS target's cells. Addressed at c.</param>
        /// <param name="desirability">Stage 5 per-group energies for this aperture.</param>
        /// <param name="shadingElements">The candidate. Only these Guids may be credited.</param>
        /// <param name="typologyName">Provenance label.</param>
        /// <param name="materialFraction">Device area / aperture gross area.</param>
        /// <param name="cellIndexOffset">This target's first cell index within the visibility cache's shared cell space.</param>
        public static ShadingPerformance ShadingPerformance(this ApertureSolarTarget target, SolarVisibilityCache baseVisibilityCache, SolarAttributionCache attributionCache, ApertureDesirability desirability, IEnumerable<ShadingElement> shadingElements, string typologyName, double materialFraction, int cellIndexOffset = 0)
        {
            List<AnalysisCell> cells = target?.AnalysisCells;
            if (cells == null || cells.Count == 0 || baseVisibilityCache == null || attributionCache == null || desirability == null)
            {
                return null;
            }

            // The target's window must lie inside the visibility cache, and the attribution cache
            // must cover exactly that window. Anything else and the two are not describing the same
            // samples, which would produce plausible numbers about the wrong aperture.
            if (cellIndexOffset < 0 || cellIndexOffset + cells.Count > baseVisibilityCache.CellCount)
            {
                return null;
            }

            if (attributionCache.CellCount != cells.Count)
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
                    // Shared cell space for the baseline, local cell space for the attribution.
                    if (!baseVisibilityCache.IsLit(b, cellIndexOffset + c))
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

                    int firstHit = attributionCache.FirstHitIndex(b, c);
                    if (firstHit < 0)
                    {
                        continue; // still reaches the aperture
                    }

                    interceptedDirect += area * direct;
                    interceptedUnwanted += area * unwanted;
                    blockedWanted += area * wanted;

                    Guid guid = attributionCache.FirstHitGuid(b, c);
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
        ///
        /// Attribution is built for THIS TARGET'S CELLS ONLY — the window starting at
        /// cellIndexOffset — while the occluder set stays the whole model. Scoping the samples is
        /// what makes analysing one window of a 2 000-window project affordable; scoping the
        /// occluders would silently delete the rest of the building from the sun path, so it is not
        /// done here and must not be done anywhere.
        ///
        /// A device with NO elements is a legitimate candidate — it is the null device, "build
        /// nothing" — and is measured like any other: nothing is intercepted, and the admitted
        /// baseline is reported honestly.
        ///
        /// CONTEXT IS HONOURED THROUGH THE BASE VISIBILITY CACHE, NOT RE-TRACED. contextOccluders is
        /// still the physical surroundings and still decides everything — but it decides it once,
        /// when baseVisibilityCache was built, and this call does not trace it again.
        ///
        /// The identity that makes that exact: attribution is only ever consulted at samples the
        /// base cache reports as LIT, and "lit" means the same primitive, with the same tolerances
        /// and the same ray-start offset, already found no context face on that ray
        /// (Query.CellVisibility and Query.CellFirstHit are deliberate mirrors of each other). For
        /// those samples the first hit over context PLUS candidate is therefore either a candidate
        /// element or nothing — the context faces cannot be first, because they are not on the ray
        /// at all. Tracing them again can only reproduce an answer already paid for.
        ///
        /// It is also the difference between usable and unusable on a real project. Each attribution
        /// build projects every occluder onto a plane per sun group, and an optimiser evaluates
        /// dozens of candidates: on a 8 800-panel model, re-projecting the whole building for every
        /// candidate cost about 300 s for a single family on a single window. Nothing is removed
        /// from the physics — the building still shades the window, through the cache that measured
        /// it — and MultiApertureShadingTests asserts the two routes agree numerically rather than
        /// leaving the argument above to stand on its own.
        /// </summary>
        /// <param name="target">The aperture.</param>
        /// <param name="baseVisibilityCache">Visibility with CONTEXT ONLY: what the candidate may be credited for.</param>
        /// <param name="desirability">Stage 5 per-group energies for this aperture.</param>
        /// <param name="contextOccluders">The physical surroundings. Present for API symmetry and for callers that pass an unrelated base cache; the shading answer takes context from baseVisibilityCache.</param>
        /// <param name="typology">The candidate device.</param>
        /// <param name="cellIndexOffset">This target's first cell index within baseVisibilityCache. Use ApertureShadingSetup.CellIndexOffset; 0 when the cache covers this target alone.</param>
        public static ShadingPerformance ShadingPerformance(this ApertureSolarTarget target, SolarVisibilityCache baseVisibilityCache, ApertureDesirability desirability, List<LinkedFace3D> contextOccluders, IShadingTypology typology, int cellIndexOffset = 0)
        {
            List<ShadingElement> elements = typology?.ShadingElements(target);
            if (elements == null)
            {
                return null;
            }

            SolarAttributionCache attributionCache = CandidateAttributionCache(baseVisibilityCache, elements, target.AnalysisCells, cellIndexOffset);
            if (attributionCache == null)
            {
                return null;
            }

            return ShadingPerformance(target, baseVisibilityCache, attributionCache, desirability, elements, typology.Name, typology.MaterialFraction(target), cellIndexOffset);
        }

        /// <summary>
        /// First-hit attribution over the CANDIDATE'S faces alone, for the cell window belonging to
        /// one target. See the note above for why the context faces are not part of it.
        /// </summary>
        internal static SolarAttributionCache CandidateAttributionCache(SolarVisibilityCache baseVisibilityCache, List<ShadingElement> elements, List<AnalysisCell> analysisCells, int cellIndexOffset)
        {
            List<LinkedFace3D> occluders = new List<LinkedFace3D>();
            foreach (ShadingElement element in elements ?? new List<ShadingElement>())
            {
                LinkedFace3D linkedFace3D = element?.LinkedFace3D;
                if (linkedFace3D != null)
                {
                    occluders.Add(linkedFace3D);
                }
            }

            return Weather.SolarCalculator.Create.SolarAttributionCache(baseVisibilityCache, occluders, analysisCells, cellIndexOffset);
        }
    }
}
