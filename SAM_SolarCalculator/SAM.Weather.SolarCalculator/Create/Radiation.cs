using Innovative.SolarCalculator;
using SAM.Core;
using SAM.Geometry.SolarCalculator;
using SAM.Geometry.Spatial;
using System;
using System.Collections.Generic;

namespace SAM.Weather.SolarCalculator
{
    public static partial class Create
    {
        public static Radiation Radiation(WeatherData weatherData, DateTime dateTime, Plane plane, double skyViewFactor = 1, double groundViewFactor = 1, double albedo = 0.2, bool includeNight = false)
        {
            if (weatherData == null || plane == null)
            {
                return null;
            }

            Location location = weatherData.Location;

            SolarTimes solarTimes = Geometry.SolarCalculator.Create.SolarTimes(location, dateTime);
            if (solarTimes == null)
            {
                return null;
            }

            if (!includeNight && (dateTime < solarTimes.Sunrise || dateTime > solarTimes.Sunset))
            {
                return null;
            }

            WeatherHour weatherHour = weatherData.GetWeatherHour(dateTime);
            if(weatherHour == null)
            {
                return null;
            }

            double directSolarRadiation = weatherHour.CalculatedDirectSolarRadiation();
            if(double.IsNaN(directSolarRadiation))
            {
                return null;
            }

            double diffuseSolarRadiation = weatherHour.CalculatedDiffuseSolarRadiation();
            if (double.IsNaN(diffuseSolarRadiation))
            {
                return null;
            }

            double globalSolarRadiation = weatherHour.CalculatedGlobalSolarRadiation();
            if (double.IsNaN(globalSolarRadiation))
            {
                return null;
            }

            double tilt = Geometry.Spatial.Query.Tilt(plane);
            double surfaceAzimuth = Geometry.Spatial.Query.Azimuth(plane, Vector3D.WorldY); 
           
            return Geometry.SolarCalculator.Create.Radiation(solarTimes, tilt, surfaceAzimuth, directSolarRadiation, diffuseSolarRadiation, globalSolarRadiation, skyViewFactor, groundViewFactor, albedo);
        }

        /// <summary>
        /// Irradiance on a surface under an explicit sky model. SkyModel.Isotropic delegates to the
        /// legacy overload, unchanged. SkyModel.PerezAnisotropic uses physical conventions: the plane
        /// must be the OUTWARD-oriented receiving plane, and the beam horizontal irradiance from the
        /// weather data (CalculatedDirectSolarRadiation = global - diffuse) is converted to true
        /// direct normal irradiance via the solar elevation (capped at 5 degrees elevation).
        /// </summary>
        public static Radiation Radiation(WeatherData weatherData, DateTime dateTime, Plane plane, SkyModel skyModel, double skyViewFactor = 1, double groundViewFactor = 1, double albedo = 0.2, bool includeNight = false)
        {
            if (skyModel == SkyModel.Isotropic)
            {
                return Radiation(weatherData, dateTime, plane, skyViewFactor, groundViewFactor, albedo, includeNight);
            }

            if (skyModel != SkyModel.PerezAnisotropic || weatherData == null || plane == null)
            {
                return null;
            }

            Location location = weatherData.Location;

            SolarTimes solarTimes = Geometry.SolarCalculator.Create.SolarTimes(location, dateTime);
            if (solarTimes == null)
            {
                return null;
            }

            if (!includeNight && (dateTime < solarTimes.Sunrise || dateTime > solarTimes.Sunset))
            {
                return null;
            }

            WeatherHour weatherHour = weatherData.GetWeatherHour(dateTime);
            if (weatherHour == null)
            {
                return null;
            }

            double beamHorizontal = weatherHour.CalculatedDirectSolarRadiation();
            double diffuseSolarRadiation = weatherHour.CalculatedDiffuseSolarRadiation();
            double globalSolarRadiation = weatherHour.CalculatedGlobalSolarRadiation();
            if (double.IsNaN(beamHorizontal) || double.IsNaN(diffuseSolarRadiation) || double.IsNaN(globalSolarRadiation))
            {
                return null;
            }

            // Convert beam-on-horizontal to direct normal: DNI = beamH / sin(elevation), capped at
            // 5 degrees elevation where the division becomes unstable.
            double solarElevation = System.Convert.ToDouble(solarTimes.SolarElevation.Radians);
            double sinElevation = System.Math.Max(System.Math.Sin(solarElevation), System.Math.Sin(5.0 * System.Math.PI / 180.0));
            double directNormalIrradiance = System.Math.Max(0.0, beamHorizontal) / sinElevation;

            double tilt = Geometry.Spatial.Query.Tilt(plane);
            double surfaceAzimuth = Geometry.Spatial.Query.Azimuth(plane, Vector3D.WorldY);

            return Geometry.SolarCalculator.Create.Radiation(solarTimes, tilt, surfaceAzimuth, directNormalIrradiance, diffuseSolarRadiation, globalSolarRadiation, SkyModel.PerezAnisotropic, skyViewFactor, groundViewFactor, albedo);
        }
    }
}
