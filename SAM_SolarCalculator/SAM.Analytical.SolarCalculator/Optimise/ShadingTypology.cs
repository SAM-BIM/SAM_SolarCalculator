// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SAM.Geometry.Object.Spatial;
using SAM.Weather.SolarCalculator;

namespace SAM.Analytical.SolarCalculator
{
    /// <summary>
    /// Stage 9: search a buildable shading family's parameter space against the Stage 9 objective,
    /// using the Stage 5 desirability and the Stage 8 first-hit energy accounting as the measure.
    ///
    /// This is optimisation against PHYSICS, not against shape. No candidate is scored on how much
    /// it resembles the Stage 7 ideal mesh; every candidate is built, ray-traced through the same
    /// first-hit engine as any other device, and judged on kWh. A design that looks like the ideal
    /// and performs worse loses.
    /// </summary>
    public static partial class Optimise
    {
        /// <summary>
        /// Scores that differ by less than this RELATIVE amount are treated as equal, so the
        /// tie-break below decides rather than the last bit of a floating-point sum. Comparisons
        /// stay deterministic because the tolerance is a pure function of the two values.
        /// </summary>
        private const double ScoreTolerance = 1e-12;

        /// <summary>
        /// Optimises one family against one aperture.
        ///
        /// SEARCH. A deterministic two-phase derivative-free search, chosen over a population
        /// method because it is inspectable, needs no random seed, and the objective here is cheap
        /// enough per evaluation but not free (a full attribution rebuild per candidate — see
        /// AttributionCacheScaleTests) that a few dozen evaluations is the right budget:
        ///
        ///   1. COARSE LATTICE. Every free parameter is sampled at a fixed number of levels across
        ///      its bounds, in ascending parameter order. This is what stops the search from
        ///      committing to the basin the seed happens to sit in — the depth response of a real
        ///      device is not unimodal once counts and tilts are in play.
        ///   2. COMPASS REFINEMENT. From the best coarse point, each parameter is probed at plus
        ///      and minus the current step in fixed order; when no probe improves, every step is
        ///      halved. It terminates when the step falls below the parameter's own granularity,
        ///      which is a real physical limit (a 10 mm depth change, one whole louvre) rather than
        ///      an arbitrary epsilon.
        ///
        /// The seed INFORMS but does not decide: it is one lattice point among many, and it wins
        /// only if it scores best. Stage 7 can supply it (a depth read off the field's own zero
        /// crossing), which usually saves iterations and never changes the answer's basis.
        ///
        /// DETERMINISM. Parameters are visited in the typology's fixed order; every proposal is
        /// snapped onto its parameter's step lattice; evaluated points are memoised by an exact
        /// string key so a revisit cannot produce a different answer; ties are broken by a total
        /// order (score, then lower material, then lexicographically smaller parameters). There is
        /// no randomness and no parallel reduction at this level — the parallelism lives inside the
        /// attribution build, where each sun group writes its own row.
        ///
        /// THE NULL DEVICE. The score of building nothing is exactly zero. If no candidate beats
        /// it, the result says so through RecommendsNoShading rather than returning the least-bad
        /// geometry as though it were a recommendation.
        /// </summary>
        /// <param name="target">The aperture.</param>
        /// <param name="baseVisibilityCache">Visibility with CONTEXT ONLY. Defines what a candidate may be credited for, and is reused for every candidate.</param>
        /// <param name="desirability">Stage 5 per-group energies for this aperture.</param>
        /// <param name="contextOccluders">Existing context. Present in every candidate's occluder set.</param>
        /// <param name="typologyName">Family to optimise.</param>
        /// <param name="objective">The objective. Null for the Stage 9 default.</param>
        /// <param name="parameters">Search variables. Null for the family defaults.</param>
        /// <param name="seed">Starting device. Null for the family default.</param>
        /// <param name="maximumEvaluations">Hard budget on distinct candidate evaluations.</param>
        /// <param name="coarseLevels">Samples per free parameter in the coarse phase.</param>
        public static OptimisedShadingResult ShadingTypology(
            this ApertureSolarTarget target,
            SolarVisibilityCache baseVisibilityCache,
            ApertureDesirability desirability,
            List<LinkedFace3D> contextOccluders,
            string typologyName,
            ShadingObjective objective = null,
            List<ShadingParameter> parameters = null,
            IShadingTypology seed = null,
            int maximumEvaluations = 400,
            int coarseLevels = 3)
        {
            if (target == null || baseVisibilityCache == null || desirability == null)
            {
                return null;
            }

            IShadingTypology prototype = Create.ShadingTypology(typologyName);
            if (prototype == null)
            {
                return null;
            }

            ShadingObjective objective_Local = objective ?? new ShadingObjective();

            // The default variable set is capped by the analysis resolution the visibility cache
            // was built at, so a candidate can never be credited for shading finer than the
            // analysis can see. See Create.ShadingParameters for why that is a correctness
            // constraint rather than a convenience.
            List<ShadingParameter> parameters_Local = parameters
                ?? Create.ShadingParameters(prototype, double.NaN, target, baseVisibilityCache.CellSize);
            if (parameters_Local == null || parameters_Local.Count == 0)
            {
                return null;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            Evaluator evaluator = new Evaluator(target, baseVisibilityCache, desirability, contextOccluders, typologyName, objective_Local, parameters_Local);

            // --- the starting vector: the caller's seed where given, the family default otherwise.
            double[] seedVector = new double[parameters_Local.Count];
            for (int i = 0; i < parameters_Local.Count; i++)
            {
                double value = seed == null ? prototype.GetParameter(parameters_Local[i].Name) : seed.GetParameter(parameters_Local[i].Name);
                if (double.IsNaN(value))
                {
                    value = prototype.GetParameter(parameters_Local[i].Name);
                }

                seedVector[i] = parameters_Local[i].Snap(value);
            }

            Candidate best = evaluator.Evaluate(seedVector);
            Candidate seedCandidate = best;

            List<int> free = new List<int>();
            for (int i = 0; i < parameters_Local.Count; i++)
            {
                if (!parameters_Local[i].IsFixed)
                {
                    free.Add(i);
                }
            }

            int iterations = 0;
            ShadingOptimisationTermination termination = ShadingOptimisationTermination.NothingToSearch;

            if (best != null && free.Count > 0)
            {
                // --- phase 1: coarse lattice.
                foreach (double[] point in Lattice(parameters_Local, free, seedVector, Math.Max(2, coarseLevels)))
                {
                    if (evaluator.Evaluations >= maximumEvaluations)
                    {
                        break;
                    }

                    Candidate candidate = evaluator.Evaluate(point);
                    if (IsBetter(candidate, best))
                    {
                        best = candidate;
                    }
                }

                // --- phase 2: compass refinement from the coarse winner.
                double[] step = new double[parameters_Local.Count];
                for (int i = 0; i < parameters_Local.Count; i++)
                {
                    // Half the coarse spacing: fine enough to resolve between lattice points,
                    // coarse enough not to start from the granularity floor.
                    step[i] = 0.5 * parameters_Local[i].Range / Math.Max(1, coarseLevels - 1);
                }

                termination = ShadingOptimisationTermination.StepBelowGranularity;
                while (true)
                {
                    if (evaluator.Evaluations >= maximumEvaluations)
                    {
                        termination = ShadingOptimisationTermination.EvaluationBudgetExhausted;
                        break;
                    }

                    if (Converged(parameters_Local, free, step))
                    {
                        break;
                    }

                    iterations++;
                    bool improved = false;

                    foreach (int i in free)
                    {
                        foreach (int sign in new int[] { 1, -1 })
                        {
                            if (evaluator.Evaluations >= maximumEvaluations)
                            {
                                break;
                            }

                            double[] probe = (double[])best.Parameters.Clone();
                            probe[i] = parameters_Local[i].Snap(probe[i] + sign * step[i]);
                            if (probe[i] == best.Parameters[i])
                            {
                                continue; // the step snapped back onto the incumbent
                            }

                            Candidate candidate = evaluator.Evaluate(probe);
                            if (IsBetter(candidate, best))
                            {
                                best = candidate;
                                improved = true;
                                break; // accept and move to the next parameter: a fixed, reproducible order
                            }
                        }
                    }

                    if (!improved)
                    {
                        for (int i = 0; i < step.Length; i++)
                        {
                            step[i] *= 0.5;
                        }
                    }
                }
            }

            stopwatch.Stop();

            if (best == null)
            {
                return null;
            }

            // --- the null device scores exactly zero: no benefit, no harm, no material.
            bool recommendsNoShading = !(best.Score > 0);
            if (recommendsNoShading)
            {
                termination = ShadingOptimisationTermination.NoBeneficialCandidate;
            }

            return evaluator.Result(best, seedCandidate, iterations, stopwatch.Elapsed.TotalMilliseconds, termination, recommendsNoShading);
        }

        /// <summary>True when every free parameter's step has fallen below its own granularity.</summary>
        private static bool Converged(List<ShadingParameter> parameters, List<int> free, double[] step)
        {
            foreach (int i in free)
            {
                double granularity = parameters[i].Step;
                if (double.IsNaN(granularity) || granularity <= 0)
                {
                    granularity = 1e-4 * parameters[i].Range;
                }

                if (step[i] >= granularity)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The coarse lattice: every free parameter at evenly spaced levels across its bounds,
        /// enumerated in a fixed odometer order so two runs visit the same points in the same
        /// sequence. Fixed parameters hold their seed value.
        /// </summary>
        private static IEnumerable<double[]> Lattice(List<ShadingParameter> parameters, List<int> free, double[] seedVector, int levels)
        {
            int[] counter = new int[free.Count];
            while (true)
            {
                double[] point = (double[])seedVector.Clone();
                for (int f = 0; f < free.Count; f++)
                {
                    int i = free[f];
                    double fraction = levels <= 1 ? 0.5 : (double)counter[f] / (levels - 1);
                    point[i] = parameters[i].Snap(parameters[i].Minimum + fraction * parameters[i].Range);
                }

                yield return point;

                int digit = free.Count - 1;
                while (digit >= 0)
                {
                    counter[digit]++;
                    if (counter[digit] < levels)
                    {
                        break;
                    }

                    counter[digit] = 0;
                    digit--;
                }

                if (digit < 0)
                {
                    yield break;
                }
            }
        }

        /// <summary>
        /// The total order that makes the winner unique. Strictly better score wins; on a tie
        /// within the score tolerance, less material wins; on a tie there too, the
        /// lexicographically smaller parameter vector wins. Without the last two rules two runs
        /// could return different devices with identical scores, which is exactly what a
        /// reproducibility requirement forbids.
        /// </summary>
        private static bool IsBetter(Candidate candidate, Candidate incumbent)
        {
            if (candidate == null || double.IsNaN(candidate.Score))
            {
                return false;
            }

            if (incumbent == null || double.IsNaN(incumbent.Score))
            {
                return true;
            }

            double tolerance = ScoreTolerance * Math.Max(1.0, Math.Max(Math.Abs(candidate.Score), Math.Abs(incumbent.Score)));
            if (candidate.Score > incumbent.Score + tolerance) { return true; }
            if (candidate.Score < incumbent.Score - tolerance) { return false; }

            double candidateCost = double.IsNaN(candidate.MaterialFraction) ? double.PositiveInfinity : candidate.MaterialFraction;
            double incumbentCost = double.IsNaN(incumbent.MaterialFraction) ? double.PositiveInfinity : incumbent.MaterialFraction;
            if (candidateCost < incumbentCost) { return true; }
            if (candidateCost > incumbentCost) { return false; }

            for (int i = 0; i < candidate.Parameters.Length && i < incumbent.Parameters.Length; i++)
            {
                if (candidate.Parameters[i] < incumbent.Parameters[i]) { return true; }
                if (candidate.Parameters[i] > incumbent.Parameters[i]) { return false; }
            }

            return false;
        }

        /// <summary>One evaluated point: its parameters, its score and everything needed to report it.</summary>
        private class Candidate
        {
            public double[] Parameters;
            public double Score = double.NaN;
            public double MaterialFraction = double.NaN;
            public ShadingPerformance Performance;
            public List<Guid> ElementGuids;
            public string AttributionTableHash;
        }

        /// <summary>
        /// Builds and measures candidates, memoising by snapped parameter vector so a revisited
        /// point costs nothing and — more importantly — can never return a different answer.
        /// </summary>
        private class Evaluator
        {
            private readonly ApertureSolarTarget target;
            private readonly SolarVisibilityCache baseVisibilityCache;
            private readonly ApertureDesirability desirability;
            private readonly List<LinkedFace3D> contextOccluders;
            private readonly string typologyName;
            private readonly ShadingObjective objective;
            private readonly List<ShadingParameter> parameters;
            private readonly Dictionary<string, Candidate> cache = new Dictionary<string, Candidate>();

            private double geometryMilliseconds;
            private double evaluationMilliseconds;

            public Evaluator(ApertureSolarTarget target, SolarVisibilityCache baseVisibilityCache, ApertureDesirability desirability, List<LinkedFace3D> contextOccluders, string typologyName, ShadingObjective objective, List<ShadingParameter> parameters)
            {
                this.target = target;
                this.baseVisibilityCache = baseVisibilityCache;
                this.desirability = desirability;
                this.contextOccluders = contextOccluders ?? new List<LinkedFace3D>();
                this.typologyName = typologyName;
                this.objective = objective;
                this.parameters = parameters;
            }

            /// <summary>Distinct geometries actually built and ray-traced (cache hits excluded).</summary>
            public int Evaluations { get { return cache.Count; } }

            public double GeometryMilliseconds { get { return geometryMilliseconds; } }

            public double EvaluationMilliseconds { get { return evaluationMilliseconds; } }

            public Candidate Evaluate(double[] values)
            {
                string key = Key(values);
                if (cache.TryGetValue(key, out Candidate cached))
                {
                    return cached;
                }

                Stopwatch stopwatch = Stopwatch.StartNew();

                IShadingTypology typology = Create.ShadingTypology(typologyName);
                for (int i = 0; i < parameters.Count; i++)
                {
                    typology.SetParameter(parameters[i].Name, values[i]);
                }

                List<ShadingElement> elements = typology.ShadingElements(target);
                List<LinkedFace3D> occluders = new List<LinkedFace3D>(contextOccluders);
                List<Guid> guids = new List<Guid>();
                if (elements != null)
                {
                    foreach (ShadingElement element in elements)
                    {
                        LinkedFace3D linkedFace3D = element?.LinkedFace3D;
                        if (linkedFace3D != null)
                        {
                            occluders.Add(linkedFace3D);
                            guids.Add(element.Guid);
                        }
                    }
                }

                stopwatch.Stop();
                geometryMilliseconds += stopwatch.Elapsed.TotalMilliseconds;

                Candidate result = new Candidate { Parameters = (double[])values.Clone(), ElementGuids = guids };

                if (elements == null || elements.Count == 0)
                {
                    cache[key] = result;
                    return result;
                }

                stopwatch = Stopwatch.StartNew();

                SolarAttributionCache attributionCache = Weather.SolarCalculator.Create.SolarAttributionCache(baseVisibilityCache, occluders, target.AnalysisCells);
                if (attributionCache != null)
                {
                    ShadingPerformance performance = Create.ShadingPerformance(
                        target, baseVisibilityCache, attributionCache, desirability, elements,
                        typologyName, typology.MaterialFraction(target));

                    if (performance != null)
                    {
                        result.Performance = performance;
                        result.MaterialFraction = performance.MaterialFraction;
                        result.Score = objective.Score(performance);
                        result.AttributionTableHash = attributionCache.AttributionTableHash;
                    }
                }

                stopwatch.Stop();
                evaluationMilliseconds += stopwatch.Elapsed.TotalMilliseconds;

                cache[key] = result;
                return result;
            }

            /// <summary>
            /// Round-trip exact key. "R" formatting means two parameter vectors that are equal as
            /// doubles produce the same key and one that differs in the last bit does not, so the
            /// memo can never conflate two distinct geometries.
            /// </summary>
            private string Key(double[] values)
            {
                StringBuilder stringBuilder = new StringBuilder();
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0) { stringBuilder.Append('|'); }
                    stringBuilder.Append(values[i].ToString("R", CultureInfo.InvariantCulture));
                }

                return stringBuilder.ToString();
            }

            public OptimisedShadingResult Result(Candidate best, Candidate seed, int iterations, double elapsedMilliseconds, ShadingOptimisationTermination termination, bool recommendsNoShading)
            {
                OptimisedShadingResult result = new OptimisedShadingResult();
                result.TypologyName = typologyName;
                result.Objective = objective;

                List<string> names = new List<string>();
                Dictionary<string, double> values = new Dictionary<string, double>();
                Dictionary<string, double> seeds = new Dictionary<string, double>();
                for (int i = 0; i < parameters.Count; i++)
                {
                    names.Add(parameters[i].Name);
                    values[parameters[i].Name] = best.Parameters[i];
                    if (seed != null) { seeds[parameters[i].Name] = seed.Parameters[i]; }
                }

                result.SetParameters(names, values, seeds, parameters);
                result.SetPerformance(best.Performance, objective);
                result.SetRun(cache.Count, iterations, elapsedMilliseconds, termination, recommendsNoShading, seed == null ? double.NaN : seed.Score);
                result.SetTiming(geometryMilliseconds, evaluationMilliseconds);
                result.SetProvenance(
                    target.ApertureGuid,
                    desirability.DesirabilityStrategyName,
                    baseVisibilityCache.CellSize,
                    baseVisibilityCache.BinSizeDegrees,
                    baseVisibilityCache.SunPositionShiftInMinutes,
                    baseVisibilityCache.Year,
                    baseVisibilityCache.ContextGeometryHash,
                    baseVisibilityCache.TargetGeometryHash,
                    best.AttributionTableHash,
                    best.ElementGuids);

                return result;
            }
        }
    }
}
