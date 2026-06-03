// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using SAM.Core;
using SAM.Analytical;
using SAM.Analytical.SolarCalculator;
using SAM.Geometry.SolarCalculator;
using SAM.Geometry.Object.Spatial;
using SAM.Geometry.Spatial;

namespace SAM.SolarCalculator.Tests
{
    /// <summary>
    /// Macro tests over two real exported AnalyticalModels:
    ///   ModelA.sam                 = SAMAnalytical.FromTBD with _importSurfaceShades_ = true  (TAS shade surfaces)
    ///   ModelB-SolarSimulation.sam = SAMAnalytical.FromTBD (shades=false) -> SAMAnalytical.SolarSimulation
    /// Fixtures are stored in SAM's native compressed .sam (zip) format and loaded via Convert.ToSAM.
    /// They reproduce the surface-set mismatch that motivated the _useModelSolarModel_ toggle on
    /// SAMAnalytical.SolarSimulation, and prove the toggle makes SAM evaluate the SAME surfaces as
    /// the TAS import (a 1:1 benchmark set) instead of the AdjacencyCluster-filtered panel set.
    /// </summary>
    public class CompareSolarCoverageTests
    {
        // Surface counts baked into the committed fixtures (== SAMAnalytical.CompareSolarCoverage's
        // "panels total" diagnostic for these two models).
        private const int ExpectedSurfaces_A = 36;
        private const int ExpectedSurfaces_B = 8;

        private const double GeometryTolerance = 1e-6;

        private static readonly List<DateTime> SampleDateTimes = new List<DateTime>
        {
            // A few daylight hours on the summer solstice — enough to exercise the full
            // sun-visibility pipeline without the cost of a whole-year run.
            new DateTime(2018, 6, 21, 9, 0, 0),
            new DateTime(2018, 6, 21, 12, 0, 0),
            new DateTime(2018, 6, 21, 15, 0, 0),
        };

        private static AnalyticalModel Load(string fileName)
        {
            string path = Path.Combine(AppContext.BaseDirectory, fileName);
            Assert.True(File.Exists(path), $"Test fixture missing: {path}");

            List<AnalyticalModel> analyticalModels = SAM.Core.Convert.ToSAM<AnalyticalModel>(path);
            Assert.NotNull(analyticalModels);

            AnalyticalModel analyticalModel = analyticalModels.FirstOrDefault(x => x != null);
            Assert.NotNull(analyticalModel);
            return analyticalModel;
        }

        private static SolarModel GetSolarModel(AnalyticalModel analyticalModel)
        {
            return analyticalModel.GetValue<SolarModel>(AnalyticalModelParameter.SolarModel);
        }

        private static List<Point3D> InternalPoints(SolarModel solarModel)
        {
            return solarModel.GetLinkedFace3Ds()
                .Where(x => x?.Face3D != null)
                .Select(x => x.Face3D.InternalPoint3D())
                .Where(x => x != null)
                .ToList();
        }

        [Fact]
        public void Fixtures_have_mismatched_surface_sets()
        {
            SolarModel solarModel_A = GetSolarModel(Load("ModelA.sam"));
            SolarModel solarModel_B = GetSolarModel(Load("ModelB-SolarSimulation.sam"));

            Assert.NotNull(solarModel_A);
            Assert.NotNull(solarModel_B);

            Assert.Equal(ExpectedSurfaces_A, solarModel_A.GetLinkedFace3Ds().Count);
            Assert.Equal(ExpectedSurfaces_B, solarModel_B.GetLinkedFace3Ds().Count);

            // The problem in one assertion: the default SolarSimulation path covers far fewer
            // surfaces than the TAS import, so comparing the two directly is not 1:1.
            Assert.True(
                solarModel_B.GetLinkedFace3Ds().Count < solarModel_A.GetLinkedFace3Ds().Count,
                "Expected the SolarSimulation model to carry fewer surfaces than the TAS import.");
        }

        [Fact]
        public void UseModelSolarModel_recomputes_coverage_on_the_same_surfaces()
        {
            AnalyticalModel analyticalModel = Load("ModelA.sam");

            SolarModel solarModel_TAS = GetSolarModel(analyticalModel);
            Assert.NotNull(solarModel_TAS);

            List<Point3D> points_TAS = InternalPoints(solarModel_TAS);
            int surfaceCount = solarModel_TAS.GetLinkedFace3Ds().Count;
            Assert.Equal(ExpectedSurfaces_A, surfaceCount);

            List<SolarCoverageSimulationResult> results =
                analyticalModel.Simulate_Coverage(SampleDateTimes, SAM.Core.Tolerance.Angle, SAM.Core.Tolerance.MacroDistance, SAM.Core.Tolerance.MacroDistance, SAM.Core.Tolerance.Angle, SAM.Core.Tolerance.Distance, double.NaN, true);

            // SAM coverage now exists for the SAME surface set as the TAS import (1:1), not the
            // 8-panel AdjacencyCluster-filtered set.
            Assert.NotNull(results);
            Assert.Equal(surfaceCount, results.Count);

            SolarModel solarModel_SAM = GetSolarModel(analyticalModel);
            Assert.NotNull(solarModel_SAM);
            Assert.Equal(surfaceCount, solarModel_SAM.GetLinkedFace3Ds().Count);

            // Every TAS face has a geometrically-coincident SAM face (geometry preserved exactly).
            List<Point3D> points_SAM = InternalPoints(solarModel_SAM);
            int aligned = points_TAS.Count(point_TAS =>
                points_SAM.Any(point_SAM => point_SAM.Distance(point_TAS) <= GeometryTolerance));

            Assert.Equal(points_TAS.Count, aligned);
        }

        [Fact]
        public void ClassifyPanels_accounts_for_every_Model_A_surface()
        {
            AnalyticalModel analyticalModel_B = Load("ModelB-SolarSimulation.sam");

            // Classify Model B's analytical panels by the same rule the SolarModel builder uses.
            List<PanelSolarClassification> classifications = analyticalModel_B.ClassifyPanelsForSolarModel();
            Assert.NotNull(classifications);
            Assert.NotEmpty(classifications);

            // Map each of Model A's surfaces to the nearest Model B panel and tally the verdict.
            SolarModel solarModel_A = GetSolarModel(Load("ModelA.sam"));
            Assert.NotNull(solarModel_A);

            int surfaces = 0, kept = 0, dropped = 0, noPanel = 0;
            foreach (LinkedFace3D face_A in solarModel_A.GetLinkedFace3Ds())
            {
                Point3D point_A = face_A?.Face3D?.InternalPoint3D();
                if (point_A == null) continue;
                surfaces++;

                PanelSolarClassification nearest = null;
                double best = double.MaxValue;
                foreach (PanelSolarClassification c in classifications)
                {
                    if (c.InternalPoint3D == null) continue;
                    double distance = point_A.Distance(c.InternalPoint3D);
                    if (distance <= 0.5 && distance < best) { best = distance; nearest = c; }
                }

                if (nearest == null) noPanel++;
                else if (nearest.Kept) kept++;
                else dropped++;
            }

            // Accounting integrity: every Model A surface is explained — kept, dropped, or absent —
            // and the gap (surfaces SAM omits) is non-empty, i.e. the diagnostic explains the mismatch.
            Assert.Equal(ExpectedSurfaces_A, surfaces);
            Assert.Equal(surfaces, kept + dropped + noPanel);
            Assert.True(dropped + noPanel > 0, "diagnostic should explain why some Model A surfaces are not in SAM's SolarModel");
        }

        // 14 window apertures across the 8 exposed panels of the committed fixture.
        private const int ExpectedApertures = 14;

        [Fact]
        public void ToSAM_SolarModel_matches_TAS_surface_set_one_to_one()
        {
            AnalyticalModel analyticalModel = Load("ModelB-SolarSimulation.sam");

            SolarModel solarModel = analyticalModel.ToSAM_SolarModel();
            Assert.NotNull(solarModel);

            // 8 opaque exposed panels + per window an opening AND an inset glazing pane (14 each),
            // mirroring the TAS import's two-surface-per-window representation: 8 + 14 + 14 = 36.
            Assert.Equal(ExpectedSurfaces_B + 2 * ExpectedApertures, solarModel.GetLinkedFace3Ds().Count);
            Assert.Equal(ExpectedSurfaces_A, solarModel.GetLinkedFace3Ds().Count);
        }

        [Fact]
        public void Default_path_now_covers_the_full_TAS_equivalent_surface_set()
        {
            AnalyticalModel analyticalModel = Load("ModelA.sam");

            List<SolarCoverageSimulationResult> results =
                analyticalModel.Simulate_Coverage(SampleDateTimes);

            // With apertures included, the standalone (no-toggle) path now produces a coverage result
            // for every one of the 36 surfaces — opaque panels, window openings and glazing panes —
            // matching the TAS import 1:1.
            Assert.NotNull(results);
            Assert.Equal(ExpectedSurfaces_A, results.Count);
        }
    }
}
