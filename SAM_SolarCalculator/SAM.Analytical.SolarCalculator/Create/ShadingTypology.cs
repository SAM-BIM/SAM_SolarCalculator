// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;

namespace SAM.Analytical.SolarCalculator
{
    public static partial class Create
    {
        /// <summary>The buildable families Stage 9 can optimise, in a fixed reporting order.</summary>
        public static readonly string[] ShadingTypologyNames = new string[]
        {
            "Overhang",
            "HorizontalLouvres",
            "VerticalFins",
            "EggCrate",
        };

        /// <summary>
        /// A default-parameterised typology by family name, or null for an unknown name.
        ///
        /// This is what lets an OptimisedShadingResult rebuild its winning device from a stored
        /// name and parameter list rather than carrying geometry, and what lets the multi-typology
        /// search enumerate families without a switch at every call site.
        /// </summary>
        public static IShadingTypology ShadingTypology(string typologyName)
        {
            switch (typologyName)
            {
                case "Overhang": return new Overhang();
                case "HorizontalLouvres": return new HorizontalLouvres();
                case "VerticalFins": return new VerticalFins();
                case "EggCrate": return new EggCrate();
                default: return null;
            }
        }

        /// <summary>
        /// The Stage 9 search variables for a family: which parameters may move, over what range,
        /// and at what granularity.
        ///
        /// Ranges are the typology's OWN declared bounds, narrowed where Stage 9 knows better.
        /// Granularity is set to what the parameter physically means: counts are integers because
        /// the typology rounds them before building geometry, depths are millimetre-scale because
        /// finer than that is not a buildable distinction, and blade angles move in 5 degree steps
        /// because that is a manufacturable increment.
        ///
        /// TiltDegrees is included for the louvre and fin families and NOT for the overhang, which
        /// has no tilt parameter. Nothing here invents a variable the geometry cannot model — there
        /// is no porosity, no transmittance and no blade thickness, because the ray engine
        /// underneath is binary and would silently produce numbers that look like a screen's
        /// without being one.
        /// </summary>
        /// ELEMENT COUNTS ARE CAPPED BY THE ANALYSIS RESOLUTION when a target and grid size are
        /// supplied, and this is not a tuning choice. Blades spaced more finely than the analysis
        /// grid are shading between the sample points: with a 1 m aperture sampled at 0.5 m there
        /// are two rows of cells, and an eleven-blade array can sit so that every sample sits just
        /// under a blade while the gaps between samples stay open. The measured result then reads
        /// "100 % of unwanted solar blocked, 100 % of wanted solar retained", which is an artefact
        /// of where the samples happen to fall and not a property of the device. Capping the count
        /// so blade pitch is at least the grid size means a candidate can only be credited for
        /// shading the analysis can actually see. Refine the grid to justify a finer device.
        /// </summary>
        /// <param name="typology">The family whose bounds the ranges are intersected with.</param>
        /// <param name="maximumDepth">Optional cap on any depth-like parameter, m. NaN for none.</param>
        /// <param name="target">Aperture, for the span a blade array is spread over. Null to skip the resolution cap.</param>
        /// <param name="gridSize">Analysis cell size, m. NaN to skip the resolution cap.</param>
        public static List<ShadingParameter> ShadingParameters(this IShadingTypology typology, double maximumDepth = double.NaN, ApertureSolarTarget target = null, double gridSize = double.NaN)
        {
            if (typology == null)
            {
                return null;
            }

            double maximumAcross = double.NaN;
            double maximumUp = double.NaN;
            if (target != null && !double.IsNaN(gridSize) && gridSize > 0
                && Query.TryGetApertureLocalBounds(target, out double minX, out double maxX, out double minY, out double maxY))
            {
                // count <= span / gridSize + 1 keeps the pitch span/(count-1) at or above the grid.
                maximumAcross = (maxX - minX) / gridSize + 1.0;
                maximumUp = (maxY - minY) / gridSize + 1.0;
            }

            List<ShadingParameter> result = new List<ShadingParameter>();
            foreach (string name in typology.ParameterNames)
            {
                if (!typology.TryGetBounds(name, out double minimum, out double maximum))
                {
                    continue;
                }

                double step;
                switch (name)
                {
                    case "Depth":
                    case "RiseAboveHead":
                    case "ExtensionBeyondJambs":
                        step = 0.01; // 10 mm: below this is not a buildable distinction
                        break;
                    case "TiltDegrees":
                        step = 5.0; // a manufacturable blade increment
                        break;
                    default:
                        step = 1.0; // element counts are integers before any geometry is built
                        break;
                }

                if (name == "Depth" && !double.IsNaN(maximumDepth) && maximumDepth > minimum)
                {
                    maximum = Math.Min(maximum, maximumDepth);
                }

                // Blade arrays run UP the aperture; fin arrays run ACROSS it.
                if (name == "Count")
                {
                    double span = typology.Name == "VerticalFins" ? maximumAcross : maximumUp;
                    maximum = ResolutionCap(maximum, span);
                }
                else if (name == "LouvreCount")
                {
                    maximum = ResolutionCap(maximum, maximumUp);
                }
                else if (name == "FinCount")
                {
                    maximum = ResolutionCap(maximum, maximumAcross);
                }

                result.Add(new ShadingParameter(name, minimum, maximum, step));
            }

            return result;
        }

        /// <summary>
        /// The declared maximum, narrowed to what the analysis grid can resolve. Never drops below
        /// 1 — a single blade is always representable — and leaves the maximum alone when no
        /// resolution was supplied.
        /// </summary>
        private static double ResolutionCap(double maximum, double resolutionLimit)
        {
            if (double.IsNaN(resolutionLimit))
            {
                return maximum;
            }

            return Math.Max(1.0, Math.Min(maximum, Math.Floor(resolutionLimit)));
        }
    }
}
