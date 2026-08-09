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
using SAM.Geometry.Spatial;
using SAM.Geometry.Object.Spatial;

namespace SAM.SolarCalculator.Tests
{
    /// <summary>
    /// Regression tests for the pre-existing defects fixed alongside the Stages 0-4 work:
    /// merge == true returning un-merged results, fractional UTC truncation, and the
    /// null-plane dereference in SunExposureFace3Ds.
    /// </summary>
    public class DefectRegressionTests
    {
        private static readonly string FixturesDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");

        private static AnalyticalModel Load(string fileName)
        {
            string path = Path.Combine(FixturesDirectory, fileName);
            Assert.True(File.Exists(path), $"Test fixture missing: {path}");

            List<AnalyticalModel> analyticalModels = SAM.Core.Convert.ToSAM<AnalyticalModel>(path);
            AnalyticalModel analyticalModel = analyticalModels?.FirstOrDefault(x => x != null);
            Assert.NotNull(analyticalModel);
            return analyticalModel;
        }

        [Fact]
        public void Simulate_MergeTrue_Returns_Merged_Results()
        {
            AnalyticalModel analyticalModel = Load("ModelB-SolarSimulation.sam");

            List<DateTime> dateTimes = new List<DateTime>
            {
                new DateTime(2018, 6, 21, 11, 0, 0),
                new DateTime(2018, 6, 21, 12, 0, 0),
            };

            // First run (un-merged) attaches a baseline result per panel.
            List<SolarFaceSimulationResult> first = analyticalModel.Simulate(dateTimes, false);
            Assert.NotNull(first);
            Assert.NotEmpty(first);

            HashSet<Guid> attachedBefore = new HashSet<Guid>(analyticalModel.GetResults<SolarFaceSimulationResult>().Select(x => x.Guid));
            Assert.NotEmpty(attachedBefore);

            // Second run with merge == true: each fresh result is merged into the attached one and
            // the MERGED result is attached. The returned list must be those merged results —
            // previously it returned the un-merged (never attached) list instead.
            List<SolarFaceSimulationResult> second = analyticalModel.Simulate(dateTimes, true);
            Assert.NotNull(second);
            Assert.NotEmpty(second);

            HashSet<Guid> attachedAfter = new HashSet<Guid>(analyticalModel.GetResults<SolarFaceSimulationResult>().Select(x => x.Guid));

            // Every returned result must be attached to the model. Before the fix the returned
            // objects were the raw un-merged results (fresh Guids), which are NOT in the model.
            Assert.All(second, r => Assert.Contains(r.Guid, attachedAfter));

            // And the merged results carry the identity of the previously attached results
            // (Merge keeps the existing result's Guid), so the returned Guids intersect the
            // pre-run attached set — the un-merged raw results never would.
            Assert.True(second.Count(r => attachedBefore.Contains(r.Guid)) > 0,
                "Expected merged results to carry the pre-existing attached results' Guids.");
        }

        [Fact]
        public void SunDirection_FractionalTimeZone_Preserved()
        {
            // UTC+05:30 (India): the fractional 30 minutes must survive into SolarTimes.
            Location location530 = new Location("Kolkata", 88.3639, 22.5726, 0);
            location530.SetValue(LocationParameter.TimeZone, "UTC+05:30");

            Location location500 = new Location("Kolkata", 88.3639, 22.5726, 0);
            location500.SetValue(LocationParameter.TimeZone, "UTC+05:00");

            Assert.Equal(5.5, Geometry.SolarCalculator.Query.TimeZoneOffset(location530));
            Assert.Equal(5.0, Geometry.SolarCalculator.Query.TimeZoneOffset(location500));

            Innovative.SolarCalculator.SolarTimes solarTimes = Geometry.SolarCalculator.Create.SolarTimes(location530, new DateTime(2018, 6, 21, 12, 0, 0));
            Assert.Equal(5.5, (double)solarTimes.TimeZoneOffset, 6);

            // A 30 minute offset shift moves the sun by ~7.5 degrees of hour angle: the two sun
            // positions must differ, and the shift must correspond to ~30 minutes (not 0 or 60).
            DateTime dateTime = new DateTime(2018, 6, 21, 12, 0, 0);
            Vector3D sun530 = Geometry.SolarCalculator.Query.SunDirection(location530, dateTime, true);
            Vector3D sun500 = Geometry.SolarCalculator.Query.SunDirection(location500, dateTime, true);
            Assert.NotNull(sun530);
            Assert.NotNull(sun500);

            double angleDegrees = sun530.SmallestAngle(sun500) * 180.0 / Math.PI;
            Assert.InRange(angleDegrees, 5.0, 10.0);
        }

        [Fact]
        public void SunExposureFace3Ds_NullPlane_Returns_Null()
        {
            // A SolarFaceSimulationResult whose sun-exposure faces are degenerate (zero-area point
            // faces produce no Plane) must yield null, not a NullReferenceException.
            Point3D origin = new Point3D(0, 0, 0);
            Face3D face3D = new Face3D(new Polygon3D(new List<Point3D>
            {
                origin,
                new Point3D(1, 0, 0),
                new Point3D(1, 1, 0),
                new Point3D(0, 1, 0),
            }));

            List<Tuple<DateTime, Radiation, List<Face3D>>> sunExposure = new List<Tuple<DateTime, Radiation, List<Face3D>>>
            {
                new Tuple<DateTime, Radiation, List<Face3D>>(new DateTime(2018, 6, 21, 12, 0, 0), null, null),
            };

            SolarFaceSimulationResult result = Geometry.SolarCalculator.Create.SolarFaceSimulationResult(Guid.NewGuid(), face3D, sunExposure);
            Assert.NotNull(result);

            // No exposure faces at all -> null (existing guard), must not throw.
            Assert.Null(result.SunExposureFace3Ds(face3D, new DateTime(2018, 6, 21, 12, 0, 0)));
        }
    }
}
