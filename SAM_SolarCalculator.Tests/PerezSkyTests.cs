// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020â€“2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using SAM.Core;
using SAM.Geometry.Object.Spatial;
using SAM.Geometry.SolarCalculator;
using SAM.Geometry.Spatial;
using SAM.Weather.SolarCalculator;
using Innovative.SolarCalculator;
using Innovative.Geometry;

namespace SAM.SolarCalculator.Tests
{
    /// <summary>
    /// Stage 3 tests: Tregenza patch sets, Perez 1990 conventions, and the per-cell
    /// component-aware sky/horizon/ground visibility.
    /// </summary>
    public class PerezSkyTests
    {
        private readonly ITestOutputHelper output;

        public PerezSkyTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static Face3D WindowFace()
        {
            // South-facing (outward normal (0,-1,0)), 2 m x 1 m, sill at z = 1.
            return new Face3D(new Polygon3D(new List<Point3D>
            {
                new Point3D(0, 0, 1),
                new Point3D(2, 0, 1),
                new Point3D(2, 0, 2),
                new Point3D(0, 0, 2),
            }));
        }

        private static SolarTimes NoonSummer()
        {
            return Geometry.SolarCalculator.Create.SolarTimes(TestHelpers.London(), new DateTime(2018, 6, 21, 12, 0, 0));
        }

        [Fact]
        public void Tregenza145_PatchSet()
        {
            List<SkyPatch> patches = Geometry.SolarCalculator.Query.SkyPatchDirections(SkyPatchSubdivision.Tregenza145);
            Assert.NotNull(patches);
            Assert.Equal(145, patches.Count);
            Assert.Equal(2 * Math.PI, patches.Sum(x => x.SolidAngle), 9);
            Assert.Equal(30, patches.Count(x => x.IsHorizonBand));
            Assert.All(patches, p => Assert.True(p.Direction.Z >= -1e-9));
            Assert.All(patches, p => Assert.True(Math.Abs(p.Direction.Length - 1.0) < 1e-9));

            // Ground mirror: same count, all below horizon, same total solid angle.
            List<SkyPatch> ground = Geometry.SolarCalculator.Query.GroundPatchDirections(SkyPatchSubdivision.Tregenza145);
            Assert.Equal(145, ground.Count);
            Assert.All(ground, p => Assert.True(p.Direction.Z <= 1e-9));
            Assert.Equal(2 * Math.PI, ground.Sum(x => x.SolidAngle), 9);

            List<SkyPatch> reinhart = Geometry.SolarCalculator.Query.SkyPatchDirections(SkyPatchSubdivision.Reinhart577);
            Assert.Equal(577, reinhart.Count);
            Assert.Equal(2 * Math.PI, reinhart.Sum(x => x.SolidAngle), 9);
        }

        [Fact]
        public void Perez_Overcast_Agrees_With_Isotropic_On_Horizontal()
        {
            // Overcast (epsilon ~ 1): DNI ~ 0. On a horizontal surface Perez and isotropic must agree.
            SolarTimes solarTimes = NoonSummer();

            double dni = 0;
            double dhi = 200;
            double ghi = 200;

            // Legacy isotropic convention: inward (down) normal for a roof -> tilt = 180.
            Radiation isotropic = Geometry.SolarCalculator.Create.Radiation(solarTimes, 180, 0, dni, dhi, ghi);
            // Perez physical convention: receiving side up -> tilt = 0.
            Radiation perez = Geometry.SolarCalculator.Create.Radiation(solarTimes, 0, 0, dni, dhi, ghi, SkyModel.PerezAnisotropic);

            Assert.NotNull(isotropic);
            Assert.NotNull(perez);
            output.WriteLine($"overcast horizontal: isotropic diffuse={isotropic.DiffuseHorizontal:0.##} perez diffuse={perez.DiffuseHorizontal:0.##}");
            Assert.Equal(isotropic.DiffuseHorizontal, perez.DiffuseHorizontal, 1); // well within 5%
        }

        [Fact]
        public void Perez_ClearSky_VerticalSouth_Diverges_From_Isotropic()
        {
            // Clear sky (epsilon > 6) at summer noon, sun due south: circumsolar brightening must
            // push the south vertical diffuse well above the isotropic estimate.
            SolarTimes solarTimes = NoonSummer();
            Assert.True(System.Convert.ToDouble(solarTimes.SolarAzimuth.Degrees) > 150); // sun in the south

            double dni = 800;
            double dhi = 80;
            double ghi = 800 * Math.Sin(System.Convert.ToDouble(solarTimes.SolarElevation.Radians)) + 80;

            bool ok = Geometry.SolarCalculator.Query.TryGetPerezCoefficients(dni, dhi, System.Convert.ToDouble(solarTimes.SolarElevation.Degrees), solarTimes.ForDate.DayOfYear, out double f1, out double f2, out double epsilon, out double delta);
            Assert.True(ok);
            output.WriteLine($"clear sky: epsilon={epsilon:0.##} delta={delta:0.###} F1={f1:0.###} F2={f2:0.###}");
            Assert.True(epsilon > 6.0, $"expected clear-sky epsilon > 6, got {epsilon:0.##}");

            // Legacy isotropic for the inward-normal vertical plane (tilt = 90, azimuth = 0).
            Radiation isotropic = Geometry.SolarCalculator.Create.Radiation(solarTimes, 90, 0, dni, dhi, ghi);
            // Perez for the outward south-facing plane (tilt = 90, azimuth = 180).
            Radiation perez = Geometry.SolarCalculator.Create.Radiation(solarTimes, 90, 180, dni, dhi, ghi, SkyModel.PerezAnisotropic);

            output.WriteLine($"clear vertical south: isotropic diffuse={isotropic.DiffuseHorizontal:0.##} perez diffuse={perez.DiffuseHorizontal:0.##}");
            double difference = Math.Abs(perez.DiffuseHorizontal - isotropic.DiffuseHorizontal) / isotropic.DiffuseHorizontal;
            Assert.True(difference > 0.10, $"expected >10% clear-sky vertical difference, got {difference:0.###}");

            // Beam on the surface must be physically correct: DNI * cos(incidence), with the sun
            // essentially due south (azimuth ~178 deg) at solar noon.
            double elevationRad = System.Convert.ToDouble(solarTimes.SolarElevation.Radians);
            double azimuthDeg = System.Convert.ToDouble(solarTimes.SolarAzimuth.Degrees);
            double cosTheta = Math.Cos(elevationRad) * Math.Cos((azimuthDeg - 180.0) * Math.PI / 180.0);
            Assert.Equal(dni * cosTheta, perez.DirectNormal, 6);
        }

        [Fact]
        public void Perez_Beam_Orientation_Is_Physical()
        {
            // Guard for the convention fix: at summer noon a SOUTH vertical surface gets strong beam
            // and an EAST vertical surface gets little (sun due south). The legacy formula (with its
            // +90 deg rotation) swaps these; the Perez overload must not.
            SolarTimes solarTimes = NoonSummer();
            double dni = 800, dhi = 80, ghi = 600;

            Radiation south = Geometry.SolarCalculator.Create.Radiation(solarTimes, 90, 180, dni, dhi, ghi, SkyModel.PerezAnisotropic);
            Radiation east = Geometry.SolarCalculator.Create.Radiation(solarTimes, 90, 90, dni, dhi, ghi, SkyModel.PerezAnisotropic);
            Radiation north = Geometry.SolarCalculator.Create.Radiation(solarTimes, 90, 0, dni, dhi, ghi, SkyModel.PerezAnisotropic);
            Radiation roof = Geometry.SolarCalculator.Create.Radiation(solarTimes, 0, 0, dni, dhi, ghi, SkyModel.PerezAnisotropic);

            Assert.True(south.DirectNormal > 300, $"south beam {south.DirectNormal:0.#}");
            // Sun essentially due south: an east facade is at grazing incidence (beam ~ 0 but not
            // exactly, as the sun is ~2 deg off the south point at noon) and the north gets none.
            Assert.True(east.DirectNormal < 30, $"east beam {east.DirectNormal:0.#}");
            Assert.True(south.DirectNormal > 10 * east.DirectNormal);
            Assert.Equal(0, north.DirectNormal, 6);
            Assert.True(roof.DirectNormal > 600, $"roof beam {roof.DirectNormal:0.#}");

            // Ground-reflected: up-facing roof sees no ground; vertical sees half.
            Assert.Equal(0, roof.GlobalHorizontal, 6);
            Assert.True(south.GlobalHorizontal > 0);
        }

        [Fact]
        public void SkyVisibility_Unobstructed_Vertical_SVF_Half()
        {
            List<AnalysisCell> cells = Geometry.SolarCalculator.Query.AnalysisCells(WindowFace(), 0.5);
            SkyVisibilityCache cache = Weather.SolarCalculator.Create.SkyVisibilityCache(new List<LinkedFace3D>(), cells, 0.5);
            Assert.NotNull(cache);

            for (int c = 0; c < cache.CellCount; c++)
            {
                output.WriteLine($"cell {c}: SVF={cache.SkyViewFactor(c):0.####} HVF={cache.HorizonViewFactor(c):0.####} GVF={cache.GroundViewFactor(c):0.####}");
                Assert.Equal(0.5, cache.SkyViewFactor(c), 2);       // vertical unobstructed
                Assert.Equal(1.0, cache.HorizonViewFactor(c), 6);   // full horizon band visible
                Assert.Equal(0.5, cache.GroundViewFactor(c), 2);    // vertical unobstructed ground
            }
        }

        [Fact]
        public void SkyVisibility_Enclosed_Cell_SVF_Zero()
        {
            // The hood enclosure from the cache tests: nothing of the sky is visible.
            List<LinkedFace3D> enclosure = new List<LinkedFace3D>
            {
                new LinkedFace3D(Guid.NewGuid(), new Face3D(new Polygon3D(new List<Point3D> { new Point3D(-1, -0.5, 0), new Point3D(3, -0.5, 0), new Point3D(3, -0.5, 3), new Point3D(-1, -0.5, 3) }))),
                new LinkedFace3D(Guid.NewGuid(), new Face3D(new Polygon3D(new List<Point3D> { new Point3D(-1, 0, 3), new Point3D(3, 0, 3), new Point3D(3, -0.5, 3), new Point3D(-1, -0.5, 3) }))),
                new LinkedFace3D(Guid.NewGuid(), new Face3D(new Polygon3D(new List<Point3D> { new Point3D(-1, -0.5, 0), new Point3D(3, -0.5, 0), new Point3D(3, 0, 0), new Point3D(-1, 0, 0) }))),
                new LinkedFace3D(Guid.NewGuid(), new Face3D(new Polygon3D(new List<Point3D> { new Point3D(-1, -0.5, 0), new Point3D(-1, 0, 0), new Point3D(-1, 0, 3), new Point3D(-1, -0.5, 3) }))),
                new LinkedFace3D(Guid.NewGuid(), new Face3D(new Polygon3D(new List<Point3D> { new Point3D(3, 0, 0), new Point3D(3, -0.5, 0), new Point3D(3, -0.5, 3), new Point3D(3, 0, 3) }))),
            };

            List<AnalysisCell> cells = Geometry.SolarCalculator.Query.AnalysisCells(WindowFace(), 0.5);
            SkyVisibilityCache cache = Weather.SolarCalculator.Create.SkyVisibilityCache(enclosure, cells, 0.5);

            for (int c = 0; c < cache.CellCount; c++)
            {
                output.WriteLine($"enclosed cell {c}: SVF={cache.SkyViewFactor(c):0.####} HVF={cache.HorizonViewFactor(c):0.####} GVF={cache.GroundViewFactor(c):0.####}");
                Assert.Equal(0.0, cache.SkyViewFactor(c), 3);
                Assert.Equal(0.0, cache.HorizonViewFactor(c), 6);
                Assert.Equal(0.0, cache.GroundViewFactor(c), 3);
            }
        }

        [Fact]
        public void SkyVisibility_HorizonObstruction_Drops_HVF_Before_SVF()
        {
            // A 4 m wall 2 m in front of the window, wide enough to cover all oblique horizon-band
            // rays: blocks the horizon band (6 deg patches) but leaves the upper sky visible.
            List<LinkedFace3D> occluders = new List<LinkedFace3D>
            {
                new LinkedFace3D(Guid.NewGuid(), new Face3D(new Polygon3D(new List<Point3D>
                {
                    new Point3D(-20, -2, 0), new Point3D(20, -2, 0), new Point3D(20, -2, 4), new Point3D(-20, -2, 4),
                }))),
            };

            List<AnalysisCell> cells = Geometry.SolarCalculator.Query.AnalysisCells(WindowFace(), 0.5);
            SkyVisibilityCache cache = Weather.SolarCalculator.Create.SkyVisibilityCache(occluders, cells, 0.5);

            for (int c = 0; c < cache.CellCount; c++)
            {
                double svf = cache.SkyViewFactor(c);
                double hvf = cache.HorizonViewFactor(c);
                output.WriteLine($"horizon-obstructed cell {c}: SVF={svf:0.####} HVF={hvf:0.####}");
                Assert.True(hvf < 0.05, $"horizon band should be blocked, HVF={hvf:0.####}");
                Assert.True(svf > 0.05, $"upper sky should remain visible, SVF={svf:0.####}");
                Assert.True(svf < 0.45, $"SVF should drop below the unobstructed 0.5, SVF={svf:0.####}");
            }
        }

        [Fact]
        public void SkyVisibility_Json_RoundTrip()
        {
            List<AnalysisCell> cells = Geometry.SolarCalculator.Query.AnalysisCells(WindowFace(), 0.5);
            SkyVisibilityCache cache = Weather.SolarCalculator.Create.SkyVisibilityCache(new List<LinkedFace3D>(), cells, 0.5);

            SkyVisibilityCache roundTripped = new SkyVisibilityCache(cache.ToJsonObject());
            Assert.Equal(cache.GetIdentity(), roundTripped.GetIdentity());
            for (int c = 0; c < cache.CellCount; c++)
            {
                Assert.Equal(cache.SkyViewFactor(c), roundTripped.SkyViewFactor(c), 12);
                Assert.Equal(cache.HorizonViewFactor(c), roundTripped.HorizonViewFactor(c), 12);
                Assert.Equal(cache.GroundViewFactor(c), roundTripped.GroundViewFactor(c), 12);
            }
        }
    }
}
