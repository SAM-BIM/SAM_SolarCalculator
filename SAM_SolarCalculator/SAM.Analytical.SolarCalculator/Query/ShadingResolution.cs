// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;

namespace SAM.Analytical.SolarCalculator
{
    /// <summary>How a device's element spacing compares with the analysis grid it was measured on.</summary>
    public enum ShadingResolutionState
    {
        /// <summary>Nothing to say: either the device has no repeated elements, or the spacing is comfortably resolved.</summary>
        Resolved,

        /// <summary>Element spacing is within twice the grid: the element-level numbers are near the limit of what the analysis can see.</summary>
        NearResolutionLimit,

        /// <summary>Element spacing is at or below the grid: the analysis cannot resolve the gaps between elements at all.</summary>
        BelowResolutionLimit,
    }

    public static partial class Query
    {
        /// <summary>
        /// Whether a device's element spacing is something the analysis grid can actually see, and
        /// the sentence to show the engineer when it is not.
        ///
        /// Stage 9 found that this failure looks like a triumph: blades spaced more finely than the
        /// grid shade BETWEEN the sample points, and the measured result reads "100 % of unwanted
        /// solar blocked, 100 % of wanted solar retained" — a property of where the samples happen
        /// to fall, not of the device. Stage 9's parameter cap
        /// (<see cref="Create.ShadingParameters(IShadingTypology, double, ApertureSolarTarget, double)"/>)
        /// keeps an OPTIMISED device at or above one grid spacing. This query is the reporting side
        /// of the same rule, and it also covers devices a user typed in by hand, which nothing caps.
        ///
        /// Element pitch is span / (count - 1) — the spacing the typologies actually build to — over
        /// the aperture's up-slope extent for louvres and its across-facade extent for fins. A
        /// single element has no pitch and is always resolved.
        ///
        /// The rule is deliberately NOT redesigned here: at or below one grid spacing is the Stage 9
        /// limit; within two is close enough to warn about.
        /// </summary>
        /// <param name="typology">The device. Null returns Resolved with no message.</param>
        /// <param name="target">The aperture the device sits on, for its spans.</param>
        /// <param name="gridSize">The analysis grid size the numbers were produced at, m.</param>
        /// <param name="message">An actionable sentence, or null when there is nothing to report.</param>
        /// <param name="minimumPitch">The tightest element spacing found, m (NaN when there is none).</param>
        public static ShadingResolutionState ShadingResolution(this IShadingTypology typology, ApertureSolarTarget target, double gridSize, out string message, out double minimumPitch)
        {
            message = null;
            minimumPitch = double.NaN;

            if (typology == null || target == null || double.IsNaN(gridSize) || gridSize <= 0)
            {
                return ShadingResolutionState.Resolved;
            }

            if (!Query.TryGetApertureLocalBounds(target, out double minX, out double maxX, out double minY, out double maxY))
            {
                return ShadingResolutionState.Resolved;
            }

            double spanAcross = maxX - minX;
            double spanUp = maxY - minY;

            // Which counted parameter runs over which span. Louvre arrays stack UP the aperture;
            // fin arrays run ACROSS it. Anything else has no repeated element to space.
            List<Tuple<string, double>> counted = new List<Tuple<string, double>>();
            foreach (string name in typology.ParameterNames ?? new List<string>())
            {
                if (name == "Count")
                {
                    counted.Add(new Tuple<string, double>(name, typology.Name == "VerticalFins" ? spanAcross : spanUp));
                }
                else if (name == "LouvreCount")
                {
                    counted.Add(new Tuple<string, double>(name, spanUp));
                }
                else if (name == "FinCount")
                {
                    counted.Add(new Tuple<string, double>(name, spanAcross));
                }
            }

            foreach (Tuple<string, double> pair in counted)
            {
                double count = Math.Round(typology.GetParameter(pair.Item1));
                if (double.IsNaN(count) || count <= 1 || double.IsNaN(pair.Item2) || pair.Item2 <= 0)
                {
                    continue;
                }

                double pitch = pair.Item2 / (count - 1);
                if (double.IsNaN(minimumPitch) || pitch < minimumPitch)
                {
                    minimumPitch = pitch;
                }
            }

            if (double.IsNaN(minimumPitch))
            {
                return ShadingResolutionState.Resolved;
            }

            if (minimumPitch <= gridSize)
            {
                message = string.Format(
                    "The proposed shading spacing ({0:0.###} m) is below the reliable analysis resolution (GridSize {1:0.###} m). Reduce GridSize and recalculate before trusting the element-level results.",
                    minimumPitch, gridSize);
                return ShadingResolutionState.BelowResolutionLimit;
            }

            if (minimumPitch < 2.0 * gridSize)
            {
                message = string.Format(
                    "The proposed shading spacing ({0:0.###} m) is close to the solar-analysis grid resolution (GridSize {1:0.###} m). Reduce GridSize for more reliable element-level results.",
                    minimumPitch, gridSize);
                return ShadingResolutionState.NearResolutionLimit;
            }

            return ShadingResolutionState.Resolved;
        }
    }
}
