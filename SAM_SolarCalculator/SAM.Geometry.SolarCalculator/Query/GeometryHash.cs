// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020â€“2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using SAM.Geometry.Object.Spatial;
using SAM.Geometry.Spatial;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace SAM.Geometry.SolarCalculator
{
    public static partial class Query
    {
        /// <summary>
        /// Stable content hash over a set of context faces (occluders) and target faces (analysis
        /// cells). Vertex coordinates are rounded to the tolerance grid before hashing, per-face
        /// hashes are then combined order-independently, so the hash changes exactly when the
        /// geometry moves and is identical for identical geometry regardless of object order.
        /// Used for visibility-cache invalidation.
        /// </summary>
        public static string GeometryHash(this IEnumerable<LinkedFace3D> contextFaces, IEnumerable<Face3D> targetFaces, double tolerance = Core.Tolerance.Distance)
        {
            List<string> faceHashes = new List<string>();

            if (contextFaces != null)
            {
                foreach (LinkedFace3D linkedFace3D in contextFaces)
                {
                    faceHashes.Add(FaceHash(linkedFace3D?.Face3D, tolerance));
                }
            }

            if (targetFaces != null)
            {
                foreach (Face3D face3D in targetFaces)
                {
                    faceHashes.Add(FaceHash(face3D, tolerance));
                }
            }

            faceHashes.Sort(System.StringComparer.Ordinal);

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", faceHashes)));
                StringBuilder stringBuilder = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes)
                {
                    stringBuilder.Append(b.ToString("x2"));
                }

                return stringBuilder.ToString();
            }
        }

        private static string FaceHash(Face3D face3D, double tolerance)
        {
            if (face3D == null || tolerance <= 0 || double.IsNaN(tolerance))
            {
                return "null";
            }

            List<string> points = new List<string>();
            List<IClosedPlanar3D> closedPlanar3Ds = face3D.GetEdge3Ds();
            if (closedPlanar3Ds != null)
            {
                foreach (IClosedPlanar3D closedPlanar3D in closedPlanar3Ds)
                {
                    List<Point3D> point3Ds = (closedPlanar3D as ISegmentable3D)?.GetPoints();
                    if (point3Ds == null)
                    {
                        continue;
                    }

                    foreach (Point3D point3D in point3Ds)
                    {
                        if (point3D == null)
                        {
                            continue;
                        }

                        long x = (long)System.Math.Round(point3D.X / tolerance);
                        long y = (long)System.Math.Round(point3D.Y / tolerance);
                        long z = (long)System.Math.Round(point3D.Z / tolerance);
                        points.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2}", x, y, z));
                    }
                }
            }

            points.Sort(System.StringComparer.Ordinal);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "[{0}]{1}", points.Count, string.Join(";", points));
        }
    }
}
