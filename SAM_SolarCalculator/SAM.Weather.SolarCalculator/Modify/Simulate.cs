// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using SAM.Geometry.Object.Spatial;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using SAM.Geometry.SolarCalculator;
using SAM.Geometry.Spatial;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SAM.Weather.SolarCalculator
{
    public static partial class Modify
    {
        public static List<SolarFaceSimulationResult> Simulate(this SolarModel solarModel, Dictionary<DateTime, Vector3D> directionDictionary, bool calctulateRadiation, double minHorizonAngle = Core.Tolerance.Angle, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance, double sampleSize = double.NaN)
        {
            if (solarModel == null || directionDictionary == null)
            {
                return null;
            }

            List<LinkedFace3D> LinkedFace3Ds = solarModel.GetLinkedFace3Ds();
            if (LinkedFace3Ds == null)
            {
                return null;
            }

            Dictionary<LinkedFace3D, List<LinkedFace3D>> dictionary_Merge = Geometry.SolarCalculator.Query.Merge(LinkedFace3Ds, tolerance_Snap, tolerance_Area, tolerance_Distance);
            if (dictionary_Merge == null)
            {
                return null;
            }

            WeatherData weatherData = !calctulateRadiation ? null : solarModel.GetValue<WeatherData>(SolarModelParameter.WeatherData);

            List<LinkedFace3D> LinkedFace3Ds_Merge = new List<LinkedFace3D>(dictionary_Merge.Keys);

            Dictionary<Guid, LinkedFace3D> dictionary_LinkedFace3D_Merge = new Dictionary<Guid, LinkedFace3D>();
            foreach (LinkedFace3D linkedFace3D_Merge in LinkedFace3Ds_Merge)
            {
                dictionary_LinkedFace3D_Merge[linkedFace3D_Merge.Guid] = linkedFace3D_Merge;
            }

            KeyValuePair<DateTime, Vector3D>[] directionKeyValuePairs = directionDictionary.ToArray();
            if (!double.IsNaN(sampleSize) && sampleSize > tolerance_Distance)
            {
                return Simulate_Sampled(solarModel, LinkedFace3Ds, dictionary_Merge, weatherData, directionKeyValuePairs, sampleSize, minHorizonAngle, tolerance_Area, tolerance_Angle, tolerance_Distance);
            }

            List<Tuple<DateTime, List<LinkedFace3D>>> tuples = Enumerable.Repeat<Tuple<DateTime, List<LinkedFace3D>>>(null, directionKeyValuePairs.Length).ToList();
            Parallel.For(0, directionKeyValuePairs.Length, i =>
            //for (int i = 0; i < directionDictionary.Count(); i++)
            {
                DateTime dateTime = directionKeyValuePairs[i].Key;

                Vector3D sunDirection = directionKeyValuePairs[i].Value;
                if (sunDirection == null || !sunDirection.IsValid())
                {
                    return;
                    //continue;
                }

                if (sunDirection.Z > 0)
                {
                    return;
                    //continue;
                }

                //The 9th Hour is the position of the sun at 8:30 am.The sun rises at 8:11am.That time the sun will be on the horizon.We have a hedge so the sun needs to be above the hedge(just like a hedgerow) for it to be seen.So the sun should be above the horizon by 0.1 degrees.
                double angle = Plane.WorldXY.Project(sunDirection).SmallestAngle(sunDirection);
                if (angle < minHorizonAngle)// 0.1 radians
                {
                    return;
                    //continue;
                }

                List<LinkedFace3D> linkedFace3Ds_ExposedToSun = Geometry.Object.Spatial.Query.VisibleLinkedFace3Ds(LinkedFace3Ds_Merge, sunDirection, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance);
                if (linkedFace3Ds_ExposedToSun == null || linkedFace3Ds_ExposedToSun.Count == 0)
                {
                    return;
                    //continue;
                }

                List<LinkedFace3D> LinkedFace3Ds_DateTime = new List<LinkedFace3D>();
                foreach (LinkedFace3D linkedFace3D_ExposedToSun in linkedFace3Ds_ExposedToSun)
                {
                    if (!dictionary_LinkedFace3D_Merge.TryGetValue(linkedFace3D_ExposedToSun.Guid, out LinkedFace3D linkedFace3D_Merge))
                    {
                        continue;
                    }

                    if (!dictionary_Merge.TryGetValue(linkedFace3D_Merge, out List<LinkedFace3D> solarFaces_SolarModel) || solarFaces_SolarModel == null)
                    {
                        continue;
                    }

                    Face3D face3D_ExposedToSun = linkedFace3D_ExposedToSun.Face3D;
                    Plane plane = face3D_ExposedToSun.GetPlane();
                    if (plane == null)
                    {
                        continue;
                    }

                    Geometry.Planar.Face2D face2D_ExposedToSun = plane.Convert(face3D_ExposedToSun);
                    if (face2D_ExposedToSun == null)
                    {
                        continue;
                    }

                    foreach (LinkedFace3D linkedFace3D_SolarModel in solarFaces_SolarModel)
                    {
                        Face3D face3D_SolarModel = linkedFace3D_SolarModel?.Face3D;
                        if (face3D_SolarModel == null)
                        {
                            continue;
                        }

                        Geometry.Planar.Face2D face2D = plane.Convert(plane.Project(face3D_SolarModel));

                        List<Geometry.Planar.Face2D> face2Ds_Intersection = Geometry.Planar.Query.Intersection(face2D, face2D_ExposedToSun, tolerance_Distance);
                        if (face2Ds_Intersection == null || face2Ds_Intersection.Count == 0)
                        {
                            continue;
                        }

                        Plane plane_SolarModel = face3D_SolarModel.GetPlane();
                        if (plane_SolarModel == null)
                        {
                            continue;
                        }

                        foreach (Geometry.Planar.Face2D face2D_Intersection in face2Ds_Intersection)
                        {
                            Face3D face3D = plane.Convert(face2D_Intersection);
                            if (face3D == null)
                            {
                                continue;
                            }

                            LinkedFace3Ds_DateTime.Add(new LinkedFace3D(linkedFace3D_SolarModel.Guid, plane_SolarModel.Project(face3D)));
                        }
                    }
                }

                tuples[i] = new Tuple<DateTime, List<LinkedFace3D>>(dateTime, LinkedFace3Ds_DateTime);
            });

            Dictionary<Guid, List<Tuple<DateTime, Radiation, List<Face3D>>>> dictionary_SunExposure = new Dictionary<Guid, List<Tuple<DateTime, Radiation, List<Face3D>>>>();
            foreach (Tuple<DateTime, List<LinkedFace3D>> tuple in tuples)
            {
                if (tuple?.Item2 == null || tuple.Item2.Count == 0)
                {
                    continue;
                }

                foreach (IGrouping<Guid, LinkedFace3D> grouping in tuple.Item2.GroupBy(x => x.Guid))
                {
                    List<LinkedFace3D> linkedFace3Ds_Tuple = grouping.ToList();
                    Radiation radiation = null;
                    if (weatherData != null)
                    {
                        Plane plane = linkedFace3Ds_Tuple[0]?.Face3D?.GetPlane();
                        if (plane != null)
                        {
                            radiation = Create.Radiation(weatherData, tuple.Item1, plane);
                        }
                    }

                    if (!dictionary_SunExposure.TryGetValue(grouping.Key, out List<Tuple<DateTime, Radiation, List<Face3D>>> sunExposure))
                    {
                        sunExposure = new List<Tuple<DateTime, Radiation, List<Face3D>>>();
                        dictionary_SunExposure[grouping.Key] = sunExposure;
                    }

                    sunExposure.Add(new Tuple<DateTime, Radiation, List<Face3D>>(tuple.Item1, radiation, linkedFace3Ds_Tuple.ConvertAll(x => x.Face3D)));
                }
            }

            foreach (LinkedFace3D linkedFace3D in LinkedFace3Ds)
            {
                if (!dictionary_SunExposure.TryGetValue(linkedFace3D.Guid, out List<Tuple<DateTime, Radiation, List<Face3D>>> sunExposure) || sunExposure == null || sunExposure.Count == 0)
                {
                    continue;
                }

                SolarFaceSimulationResult solarFaceSimulationResult = Geometry.SolarCalculator.Create.SolarFaceSimulationResult(linkedFace3D, sunExposure);
                if (solarFaceSimulationResult == null)
                {
                    continue;
                }

                solarModel.Add(solarFaceSimulationResult, linkedFace3D.Guid);
            }

            return solarModel.GetSolarFaceSimulationResults();
        }


        public static List<SolarFaceSimulationResult> Simulate(this SolarModel solarModel, IEnumerable<DateTime> dateTimes, bool calctulateRadiation, double minHorizonAngle = Core.Tolerance.Angle, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance, double sampleSize = double.NaN)
        {
            if (solarModel == null || dateTimes == null)
            {
                return null;
            }

            Core.Location location = solarModel.Location;

            if (location == null)
            {
                return null;
            }

            Dictionary<DateTime, Vector3D> directionDictionary = new Dictionary<DateTime, Vector3D>();
            foreach (DateTime dateTime in dateTimes)
            {
                directionDictionary[dateTime] = SAM.Geometry.SolarCalculator.Query.SunDirection(location, dateTime, false);
            }

            return Simulate(solarModel, directionDictionary, calctulateRadiation, minHorizonAngle, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance, sampleSize);
        }

        /// <summary>
        /// Simulates SolarModel
        /// </summary>
        /// <param name="solarModel"></param>
        /// <param name="year"></param>
        /// <param name="hoursOfYear">hours of the year. Values starting from 0 to 8760</param>
        /// <param name="calctulateRadiation"></param>
        /// <param name="minHorizonAngle">Minimal Angle to Horizon</param>
        /// <param name="tolerance_Area"></param>
        /// <param name="tolerance_Snap"></param>
        /// <param name="tolerance_Angle"></param>
        /// <param name="tolerance_Distance"></param>
        /// <param name="sampleSize">Optional sample grid size. NaN or values less than tolerance_Distance use exact geometry.</param>
        /// <returns>SolarFaceSimulationResults</returns>
        public static List<SolarFaceSimulationResult> Simulate(this SolarModel solarModel, int year, List<int> hoursOfYear, bool calctulateRadiation, double minHorizonAngle = Core.Tolerance.Angle, double tolerance_Area = Core.Tolerance.MacroDistance, double tolerance_Snap = Core.Tolerance.MacroDistance, double tolerance_Angle = Core.Tolerance.Angle, double tolerance_Distance = Core.Tolerance.Distance, double sampleSize = double.NaN)
        {
            if (solarModel == null || hoursOfYear == null)
            {
                return null;
            }

            List<DateTime> dateTimes = new List<DateTime>();
            foreach (int hourOfYear in hoursOfYear)
            {
                DateTime dateTime = new DateTime(year, 1, 1);
                dateTime = dateTime.AddHours(hourOfYear);

                dateTimes.Add(dateTime);
            }

            return Simulate(solarModel, dateTimes, calctulateRadiation, minHorizonAngle, tolerance_Area, tolerance_Snap, tolerance_Angle, tolerance_Distance, sampleSize);
        }

        private static List<SolarFaceSimulationResult> Simulate_Sampled(this SolarModel solarModel, List<LinkedFace3D> linkedFace3Ds, Dictionary<LinkedFace3D, List<LinkedFace3D>> dictionary_Merge, WeatherData weatherData, KeyValuePair<DateTime, Vector3D>[] directionKeyValuePairs, double sampleSize, double minHorizonAngle, double tolerance_Area, double tolerance_Angle, double tolerance_Distance)
        {
            if (solarModel == null || linkedFace3Ds == null || dictionary_Merge == null || directionKeyValuePairs == null)
            {
                return null;
            }

            List<LinkedFace3D> linkedFace3Ds_Merge = new List<LinkedFace3D>(dictionary_Merge.Keys);
            List<SampleCell> sampleCells = SampleCells(dictionary_Merge, sampleSize, tolerance_Area, tolerance_Distance);
            if (sampleCells == null || sampleCells.Count == 0)
            {
                return solarModel.GetSolarFaceSimulationResults();
            }

            List<Tuple<DateTime, List<LinkedFace3D>>> tuples = Enumerable.Repeat<Tuple<DateTime, List<LinkedFace3D>>>(null, directionKeyValuePairs.Length).ToList();
            Parallel.For(0, directionKeyValuePairs.Length, i =>
            {
                DateTime dateTime = directionKeyValuePairs[i].Key;
                Vector3D sunDirection = directionKeyValuePairs[i].Value;
                if (!ValidSunDirection(sunDirection, minHorizonAngle))
                {
                    return;
                }

                List<LinkedFace3D> linkedFace3Ds_ExposedToSun = ExposedSampleLinkedFace3Ds(sampleCells, linkedFace3Ds_Merge, sunDirection, tolerance_Area, tolerance_Angle, tolerance_Distance);
                if (linkedFace3Ds_ExposedToSun == null || linkedFace3Ds_ExposedToSun.Count == 0)
                {
                    return;
                }

                tuples[i] = new Tuple<DateTime, List<LinkedFace3D>>(dateTime, linkedFace3Ds_ExposedToSun);
            });

            AddSimulationResults(solarModel, linkedFace3Ds, tuples, weatherData);
            return solarModel.GetSolarFaceSimulationResults();
        }

        private static bool ValidSunDirection(Vector3D sunDirection, double minHorizonAngle)
        {
            if (sunDirection == null || !sunDirection.IsValid())
            {
                return false;
            }

            if (sunDirection.Z > 0)
            {
                return false;
            }

            double angle = Plane.WorldXY.Project(sunDirection).SmallestAngle(sunDirection);
            return angle >= minHorizonAngle;
        }

        private static List<LinkedFace3D> ExposedSampleLinkedFace3Ds(List<SampleCell> sampleCells, List<LinkedFace3D> linkedFace3Ds_Merge, Vector3D sunDirection, double tolerance_Area, double tolerance_Angle, double tolerance_Distance)
        {
            if (sampleCells == null || sampleCells.Count == 0 || linkedFace3Ds_Merge == null || linkedFace3Ds_Merge.Count == 0)
            {
                return null;
            }

            Plane plane = SunPlane(linkedFace3Ds_Merge, sunDirection, out Vector3D vector3D, out Vector3D vector3D_Ray, tolerance_Distance);
            if (plane == null)
            {
                return null;
            }

            List<ProjectedFace> projectedFaces = ProjectedFaces(linkedFace3Ds_Merge, plane, vector3D, sunDirection, tolerance_Area, tolerance_Angle, tolerance_Distance);
            if (projectedFaces == null || projectedFaces.Count == 0)
            {
                return null;
            }

            STRtree<int> index = new STRtree<int>();
            for (int i = 0; i < projectedFaces.Count; i++)
            {
                Envelope envelope = ToEnvelope(projectedFaces[i].BoundingBox2D, tolerance_Distance);
                if (envelope != null)
                {
                    index.Insert(envelope, i);
                }
            }
            index.Build();

            List<LinkedFace3D> result = new List<LinkedFace3D>();
            foreach (SampleCell sampleCell in sampleCells)
            {
                Point3D point3D_Start = plane.Project(sampleCell.Point3D, vector3D, tolerance_Distance);
                if (point3D_Start == null)
                {
                    continue;
                }

                Geometry.Planar.Point2D point2D = plane.Convert(point3D_Start);
                if (point2D == null)
                {
                    continue;
                }

                Envelope envelope = ToEnvelope(point2D, tolerance_Distance);
                if (envelope == null)
                {
                    continue;
                }

                IList<int> indexes = index.Query(envelope);
                if (indexes == null || indexes.Count == 0)
                {
                    continue;
                }

                List<LinkedFace3D> candidates = new List<LinkedFace3D>();
                foreach (int index_Temp in indexes)
                {
                    ProjectedFace projectedFace = projectedFaces[index_Temp];
                    if (projectedFace?.BoundingBox2D == null || projectedFace.Face2D == null || projectedFace.LinkedFace3D == null)
                    {
                        continue;
                    }

                    if (!projectedFace.BoundingBox2D.InRange(point2D, tolerance_Distance))
                    {
                        continue;
                    }

                    if (!projectedFace.Face2D.Inside(point2D, tolerance_Distance) && !projectedFace.Face2D.On(point2D, tolerance_Distance))
                    {
                        continue;
                    }

                    candidates.Add(projectedFace.LinkedFace3D);
                }

                if (candidates.Count == 0)
                {
                    continue;
                }

                Point3D point3D_End = point3D_Start.GetMoved(vector3D_Ray) as Point3D;
                if (point3D_End == null)
                {
                    continue;
                }

                Segment3D segment3D = new Segment3D(point3D_Start, point3D_End);
                List<Tuple<LinkedFace3D, Point3D>> tuples_Intersection = Geometry.Object.Spatial.Query.IntersectionTuples(segment3D, candidates, true, tolerance_Distance);
                if (tuples_Intersection == null || tuples_Intersection.Count == 0)
                {
                    continue;
                }

                if (tuples_Intersection[0].Item1.Guid == sampleCell.MergedGuid)
                {
                    result.Add(new LinkedFace3D(sampleCell.Guid, sampleCell.Face3D));
                }
            }

            return result;
        }

        private static Plane SunPlane(List<LinkedFace3D> linkedFace3Ds, Vector3D sunDirection, out Vector3D vector3D, out Vector3D vector3D_Ray, double tolerance_Distance)
        {
            vector3D = null;
            vector3D_Ray = null;

            BoundingBox3D boundingBox3D = Geometry.Object.Spatial.Create.BoundingBox3D(linkedFace3Ds);
            if (boundingBox3D == null || !boundingBox3D.IsValid())
            {
                return null;
            }

            double distance = boundingBox3D.Min.Distance(boundingBox3D.Max);
            if (distance <= tolerance_Distance)
            {
                return null;
            }

            vector3D = new Vector3D(sunDirection).Unit * distance;
            Point3D point3D = boundingBox3D.GetCentroid().GetMoved(vector3D.GetNegated()) as Point3D;
            if (point3D == null)
            {
                return null;
            }

            vector3D_Ray = 2 * vector3D;
            return new Plane(point3D, vector3D.Unit);
        }

        private static List<ProjectedFace> ProjectedFaces(List<LinkedFace3D> linkedFace3Ds, Plane plane, Vector3D vector3D, Vector3D sunDirection, double tolerance_Area, double tolerance_Angle, double tolerance_Distance)
        {
            List<ProjectedFace> result = new List<ProjectedFace>();
            foreach (LinkedFace3D linkedFace3D in linkedFace3Ds)
            {
                Face3D face3D = linkedFace3D?.Face3D;
                if (face3D == null || !IsSolarCandidate(face3D, sunDirection, tolerance_Angle, tolerance_Distance))
                {
                    continue;
                }

                Face3D face3D_Project = plane.Project(face3D, vector3D, tolerance_Distance);
                if (face3D_Project == null || !face3D_Project.IsValid())
                {
                    continue;
                }

                Geometry.Planar.Face2D face2D = plane.Convert(face3D_Project);
                if (face2D == null || face2D.GetArea() < tolerance_Area)
                {
                    continue;
                }

                Geometry.Planar.BoundingBox2D boundingBox2D = face2D.GetBoundingBox();
                if (boundingBox2D == null)
                {
                    continue;
                }

                result.Add(new ProjectedFace(linkedFace3D, face2D, boundingBox2D));
            }

            return result;
        }

        private static bool IsSolarCandidate(Face3D face3D, Vector3D sunDirection, double tolerance_Angle, double tolerance_Distance)
        {
            Plane plane = face3D?.GetPlane();
            if (plane == null)
            {
                return false;
            }

            Vector3D vector3D_Project = plane.Project(sunDirection);
            if (vector3D_Project == null || !vector3D_Project.IsValid() || vector3D_Project.Length <= tolerance_Distance)
            {
                return true;
            }

            return sunDirection.SmallestAngle(vector3D_Project) >= tolerance_Angle;
        }

        private static List<SampleCell> SampleCells(Dictionary<LinkedFace3D, List<LinkedFace3D>> dictionary_Merge, double sampleSize, double tolerance_Area, double tolerance_Distance)
        {
            List<SampleCell> result = new List<SampleCell>();
            foreach (KeyValuePair<LinkedFace3D, List<LinkedFace3D>> keyValuePair in dictionary_Merge)
            {
                LinkedFace3D linkedFace3D_Merge = keyValuePair.Key;
                if (linkedFace3D_Merge == null)
                {
                    continue;
                }

                List<LinkedFace3D> linkedFace3Ds = keyValuePair.Value;
                if (linkedFace3Ds == null || linkedFace3Ds.Count == 0)
                {
                    linkedFace3Ds = new List<LinkedFace3D>() { linkedFace3D_Merge };
                }

                foreach (LinkedFace3D linkedFace3D in linkedFace3Ds)
                {
                    AddSampleCells(result, linkedFace3D, linkedFace3D_Merge.Guid, sampleSize, tolerance_Area, tolerance_Distance);
                }
            }

            return result;
        }

        private static void AddSampleCells(List<SampleCell> sampleCells, LinkedFace3D linkedFace3D, Guid mergedGuid, double sampleSize, double tolerance_Area, double tolerance_Distance)
        {
            Face3D face3D = linkedFace3D?.Face3D;
            Plane plane = face3D?.GetPlane();
            if (sampleCells == null || face3D == null || plane == null)
            {
                return;
            }

            Geometry.Planar.Face2D face2D = plane.Convert(face3D);
            Geometry.Planar.BoundingBox2D boundingBox2D = face2D?.GetBoundingBox();
            if (face2D == null || boundingBox2D == null)
            {
                return;
            }

            Geometry.Planar.Point2D min = boundingBox2D.Min;
            Geometry.Planar.Point2D max = boundingBox2D.Max;
            if (min == null || max == null)
            {
                return;
            }

            for (double x = min.X; x < max.X; x += sampleSize)
            {
                double width = System.Math.Min(sampleSize, max.X - x);
                if (width <= tolerance_Distance)
                {
                    continue;
                }

                for (double y = min.Y; y < max.Y; y += sampleSize)
                {
                    double height = System.Math.Min(sampleSize, max.Y - y);
                    if (height <= tolerance_Distance)
                    {
                        continue;
                    }

                    Geometry.Planar.Point2D point2D_Centre = new Geometry.Planar.Point2D(x + (0.5 * width), y + (0.5 * height));
                    if (!face2D.Inside(point2D_Centre, tolerance_Distance) && !face2D.On(point2D_Centre, tolerance_Distance))
                    {
                        continue;
                    }

                    Geometry.Planar.Rectangle2D rectangle2D = new Geometry.Planar.Rectangle2D(new Geometry.Planar.Point2D(x, y), width, height);
                    Geometry.Planar.Face2D face2D_Cell = rectangle2D;

                    List<Geometry.Planar.Face2D> face2Ds_Cell = null;
                    if (face2D.Inside(rectangle2D, tolerance_Distance))
                    {
                        face2Ds_Cell = new List<Geometry.Planar.Face2D>() { face2D_Cell };
                    }
                    else
                    {
                        face2Ds_Cell = Geometry.Planar.Query.Intersection(face2D_Cell, face2D, tolerance_Distance);
                    }

                    if (face2Ds_Cell == null || face2Ds_Cell.Count == 0)
                    {
                        continue;
                    }

                    foreach (Geometry.Planar.Face2D face2D_Temp in face2Ds_Cell)
                    {
                        if (face2D_Temp == null || face2D_Temp.GetArea() < tolerance_Area)
                        {
                            continue;
                        }

                        Geometry.Planar.Point2D point2D = face2D_Temp.GetInternalPoint2D(tolerance_Distance);
                        Point3D point3D = plane.Convert(point2D);
                        Face3D face3D_Cell = plane.Convert(face2D_Temp);
                        if (point3D == null || face3D_Cell == null || !face3D_Cell.IsValid())
                        {
                            continue;
                        }

                        sampleCells.Add(new SampleCell(linkedFace3D.Guid, mergedGuid, point3D, face3D_Cell));
                    }
                }
            }
        }

        private static void AddSimulationResults(SolarModel solarModel, List<LinkedFace3D> linkedFace3Ds, List<Tuple<DateTime, List<LinkedFace3D>>> tuples, WeatherData weatherData)
        {
            Dictionary<Guid, List<Tuple<DateTime, Radiation, List<Face3D>>>> dictionary_SunExposure = new Dictionary<Guid, List<Tuple<DateTime, Radiation, List<Face3D>>>>();
            foreach (Tuple<DateTime, List<LinkedFace3D>> tuple in tuples)
            {
                if (tuple?.Item2 == null || tuple.Item2.Count == 0)
                {
                    continue;
                }

                foreach (IGrouping<Guid, LinkedFace3D> grouping in tuple.Item2.GroupBy(x => x.Guid))
                {
                    List<LinkedFace3D> linkedFace3Ds_Tuple = grouping.ToList();
                    Radiation radiation = null;
                    if (weatherData != null)
                    {
                        Plane plane = linkedFace3Ds_Tuple[0]?.Face3D?.GetPlane();
                        if (plane != null)
                        {
                            radiation = Create.Radiation(weatherData, tuple.Item1, plane);
                        }
                    }

                    if (!dictionary_SunExposure.TryGetValue(grouping.Key, out List<Tuple<DateTime, Radiation, List<Face3D>>> sunExposure))
                    {
                        sunExposure = new List<Tuple<DateTime, Radiation, List<Face3D>>>();
                        dictionary_SunExposure[grouping.Key] = sunExposure;
                    }

                    sunExposure.Add(new Tuple<DateTime, Radiation, List<Face3D>>(tuple.Item1, radiation, linkedFace3Ds_Tuple.ConvertAll(x => x.Face3D)));
                }
            }

            foreach (LinkedFace3D linkedFace3D in linkedFace3Ds)
            {
                if (!dictionary_SunExposure.TryGetValue(linkedFace3D.Guid, out List<Tuple<DateTime, Radiation, List<Face3D>>> sunExposure) || sunExposure == null || sunExposure.Count == 0)
                {
                    continue;
                }

                SolarFaceSimulationResult solarFaceSimulationResult = Geometry.SolarCalculator.Create.SolarFaceSimulationResult(linkedFace3D, sunExposure);
                if (solarFaceSimulationResult == null)
                {
                    continue;
                }

                solarModel.Add(solarFaceSimulationResult, linkedFace3D.Guid);
            }
        }

        private static Envelope ToEnvelope(Geometry.Planar.BoundingBox2D boundingBox2D, double tolerance_Distance)
        {
            if (boundingBox2D == null)
            {
                return null;
            }

            Geometry.Planar.Point2D min = boundingBox2D.Min;
            Geometry.Planar.Point2D max = boundingBox2D.Max;
            if (min == null || max == null)
            {
                return null;
            }

            return new Envelope(min.X - tolerance_Distance, max.X + tolerance_Distance, min.Y - tolerance_Distance, max.Y + tolerance_Distance);
        }

        private static Envelope ToEnvelope(Geometry.Planar.Point2D point2D, double tolerance_Distance)
        {
            if (point2D == null)
            {
                return null;
            }

            return new Envelope(point2D.X - tolerance_Distance, point2D.X + tolerance_Distance, point2D.Y - tolerance_Distance, point2D.Y + tolerance_Distance);
        }

        private class ProjectedFace
        {
            public ProjectedFace(LinkedFace3D linkedFace3D, Geometry.Planar.Face2D face2D, Geometry.Planar.BoundingBox2D boundingBox2D)
            {
                LinkedFace3D = linkedFace3D;
                Face2D = face2D;
                BoundingBox2D = boundingBox2D;
            }

            public LinkedFace3D LinkedFace3D { get; }

            public Geometry.Planar.Face2D Face2D { get; }

            public Geometry.Planar.BoundingBox2D BoundingBox2D { get; }
        }

        private class SampleCell
        {
            public SampleCell(Guid guid, Guid mergedGuid, Point3D point3D, Face3D face3D)
            {
                Guid = guid;
                MergedGuid = mergedGuid;
                Point3D = point3D;
                Face3D = face3D;
            }

            public Guid Guid { get; }

            public Guid MergedGuid { get; }

            public Point3D Point3D { get; }

            public Face3D Face3D { get; }
        }
    }
}
