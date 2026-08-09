using Innovative.Geometry;
using Innovative.SolarCalculator;
using System;

namespace SAM.Geometry.SolarCalculator
{
    public static partial class Create
    {
        /// <summary>
        /// Isotropic (Liu-Jordan) irradiance on a surface. Legacy convention, kept byte-for-byte
        /// compatible: tilt_Temp = 180 - tilt and solarAzimuth = (radians + pi/2) * 180/pi, matching
        /// the historical behaviour of this method (see remarks on the SkyModel overload).
        /// </summary>
        /// <param name="solarTimes"></param>
        /// <param name="tilt">Surface tilt angle in degrees</param>
        /// <param name="surfaceAzimuth">Surface azimuth (e.g., south-facing)</param>
        /// <param name="directNormalIrradiance"></param>
        /// <param name="diffuseHorizontalIrradiance"></param>
        /// <param name="globalHorizontalIrradiance"></param>
        /// <param name="skyViewFactor"></param>
        /// <param name="groundViewFactor"></param>
        /// <param name="albedo"></param>
        /// <returns></returns>
        public static Radiation Radiation(this SolarTimes solarTimes, double tilt, double surfaceAzimuth, double directNormalIrradiance, double diffuseHorizontalIrradiance, double globalHorizontalIrradiance, double skyViewFactor = 1, double groundViewFactor = 1, double albedo = 0.2)
        {
            if (solarTimes == null || double.IsNaN(directNormalIrradiance) || double.IsNaN(diffuseHorizontalIrradiance) || double.IsNaN(globalHorizontalIrradiance))
            {
                return null;
            }

            Angle angle_SolarElevation = solarTimes.SolarElevation;
            if (angle_SolarElevation == null)
            {
                return null;
            }

            Angle angle_SolarAzimuth = solarTimes.SolarAzimuth;
            if (angle_SolarAzimuth == null)
            {
                return null;
            }

            double tilt_Temp = 180 - tilt;

            // 1. Get solar position
            double solarElevation = Convert.ToDouble(angle_SolarElevation.Radians);
            double solarAzimuth = (Convert.ToDouble(angle_SolarAzimuth.Radians) + (Math.PI / 2)) * 180 / Math.PI;

            // 2. Calculate angle of incidence
            double cosThetaI = Math.Sin(solarElevation) * Math.Cos(tilt_Temp * Math.PI / 180) +
                               Math.Cos(solarElevation) * Math.Sin(tilt_Temp * Math.PI / 180) *
                               Math.Cos((solarAzimuth - surfaceAzimuth) * Math.PI / 180);

            // 3. Calculate direct, diffuse, and reflected radiation
            double directNormalRadiance = directNormalIrradiance * Math.Max(0, cosThetaI);
            double diffuseHorizontalRadiance = diffuseHorizontalIrradiance * skyViewFactor * Math.Pow(Math.Cos(tilt_Temp * Math.PI / 360), 2); // Assume skyViewFactor is predefined
            double globalHorizontalRadiance = globalHorizontalIrradiance * albedo * groundViewFactor * Math.Pow(Math.Sin(tilt_Temp * Math.PI / 360), 2); // Assume albedo is predefined

            // 4. Total incident radiation
            //double I_total = I_direct + I_diffuse + I_reflected;

            return new Radiation(directNormalRadiance, diffuseHorizontalRadiance, globalHorizontalRadiance);
        }

        /// <summary>
        /// Irradiance on a surface under an explicit sky model.
        ///
        /// SkyModel.Isotropic delegates to the legacy overload above, unchanged (same signature
        /// conventions: tilt_Temp = 180 - tilt, solar azimuth rotated by +90 degrees).
        ///
        /// SkyModel.PerezAnisotropic implements Perez et al. 1990 (Solar Energy 44(5), 271-289) with
        /// PHYSICAL input conventions, which differ from the legacy isotropic path:
        ///   - tilt: tilt of the RECEIVING (outward) normal from horizontal, degrees
        ///           (0 = up-facing, 90 = vertical, 180 = down-facing);
        ///   - surfaceAzimuth: compass azimuth of the outward normal, degrees clockwise from north;
        ///   - directNormalIrradiance: true direct normal irradiance (DNI), W/m2.
        /// Components: beam = DNI * max(0, cosThetaI); diffuse = DHI * [(1-F1)*(1+cosB)/2*SVF +
        /// F1*(a/b) + F2*sinB*SVF]; ground = GHI * albedo * GVF * (1-cosB)/2.
        /// With scalar view factors (no directional visibility), skyViewFactor scales the isotropic
        /// and horizon terms and the circumsolar term is kept whenever the sun is in front of the
        /// surface. For component-aware obstruction (circumsolar removed when the sun is obstructed,
        /// horizon term scaled by horizon-band visibility) use the SolarVisibilityCache /
        /// SkyVisibilityCache evaluation path.
        /// </summary>
        public static Radiation Radiation(this SolarTimes solarTimes, double tilt, double surfaceAzimuth, double directNormalIrradiance, double diffuseHorizontalIrradiance, double globalHorizontalIrradiance, SkyModel skyModel, double skyViewFactor = 1, double groundViewFactor = 1, double albedo = 0.2)
        {
            if (skyModel == SkyModel.Isotropic)
            {
                return Radiation(solarTimes, tilt, surfaceAzimuth, directNormalIrradiance, diffuseHorizontalIrradiance, globalHorizontalIrradiance, skyViewFactor, groundViewFactor, albedo);
            }

            if (skyModel != SkyModel.PerezAnisotropic)
            {
                return null;
            }

            if (solarTimes == null || double.IsNaN(directNormalIrradiance) || double.IsNaN(diffuseHorizontalIrradiance) || double.IsNaN(globalHorizontalIrradiance))
            {
                return null;
            }

            Angle angle_SolarElevation = solarTimes.SolarElevation;
            Angle angle_SolarAzimuth = solarTimes.SolarAzimuth;
            if (angle_SolarElevation == null || angle_SolarAzimuth == null)
            {
                return null;
            }

            double solarElevation = Convert.ToDouble(angle_SolarElevation.Radians);   // radians
            double solarAzimuth = Convert.ToDouble(angle_SolarAzimuth.Degrees);       // compass degrees from north (NOAA)

            double tiltRadians = tilt * Math.PI / 180.0;   // receiving-side tilt from horizontal

            // Angle of incidence on the receiving side.
            double cosThetaI = Math.Sin(solarElevation) * Math.Cos(tiltRadians) +
                               Math.Cos(solarElevation) * Math.Sin(tiltRadians) *
                               Math.Cos((solarAzimuth - surfaceAzimuth) * Math.PI / 180);

            if (!Query.TryGetPerezCoefficients(directNormalIrradiance, diffuseHorizontalIrradiance, solarElevation * 180.0 / Math.PI, solarTimes.ForDate.DayOfYear, out double f1, out double f2, out _, out _))
            {
                return null;
            }

            double cosZenith = Math.Sin(solarElevation);
            double a = Math.Max(0.0, cosThetaI);
            double b = Math.Max(Math.Cos(85.0 * Math.PI / 180.0), cosZenith);

            double beam = directNormalIrradiance * a;
            double diffuse = diffuseHorizontalIrradiance *
                ((1.0 - f1) * (1.0 + Math.Cos(tiltRadians)) / 2.0 * skyViewFactor +
                 f1 * a / b +
                 f2 * Math.Sin(tiltRadians) * skyViewFactor);
            double ground = globalHorizontalIrradiance * albedo * groundViewFactor * (1.0 - Math.Cos(tiltRadians)) / 2.0;

            return new Radiation(beam, diffuse, ground);
        }
    }
}

