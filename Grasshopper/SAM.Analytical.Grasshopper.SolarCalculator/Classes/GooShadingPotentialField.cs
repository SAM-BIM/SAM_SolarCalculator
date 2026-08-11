// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Display;
using Rhino.Geometry;
using SAM.Analytical.Grasshopper.SolarCalculator.Properties;
using SAM.Analytical.SolarCalculator;
using SAM.Core.Grasshopper;
using SAM.Geometry.Spatial;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SAM.Analytical.Grasshopper.SolarCalculator
{
    /// <summary>
    /// The shading potential field in front of one aperture: for every point in the space where
    /// shading could go, how much solar energy material placed there would intercept.
    ///
    /// PREVIEW. One coloured dot per point, at the point's own centre:
    ///   RED   — shading here blocks unwanted solar. The stronger the red, the more it blocks.
    ///   BLUE  — shading here destroys solar you asked to keep. Leave it open.
    ///   GREY  — neither: nothing much passes through, so material here does nothing.
    ///
    /// SAM has no shared diverging colour ramp (its colour queries are keyed to panel and aperture
    /// types), so the ramp is defined here and stays local to this preview rather than inventing a
    /// shared convention other components would then have to honour.
    ///
    /// Near-zero points are not drawn at all: a full grid of grey dots hides the answer. The
    /// threshold is a share of the field's own strongest value, so it scales with the project.
    /// </summary>
    public class GooShadingPotentialField : GooJSAMObject<ShadingPotentialField>, IGH_PreviewData
    {
        /// <summary>Points weaker than this share of the field's own extreme are not drawn.</summary>
        private const double PreviewCutoff = 0.02;

        private const int PreviewPointSize = 3;

        public GooShadingPotentialField()
            : base()
        {
        }

        public GooShadingPotentialField(ShadingPotentialField shadingPotentialField)
            : base(shadingPotentialField)
        {
        }

        public override IGH_Goo Duplicate()
        {
            return new GooShadingPotentialField(Value);
        }

        public override string ToString()
        {
            ShadingPotentialField field = Value;
            if (field == null)
            {
                return typeof(ShadingPotentialField).Name;
            }

            return string.Format("ShadingPotentialField [{0} points, benefit {1:0.#} kWh, jeopardy {2:0.#} kWh]", field.Volume?.VoxelCount ?? 0, field.PositiveTotal(), field.NegativeTotal());
        }

        /// <summary>
        /// The red/blue/grey colour for a normalised score in [-1, 1]. Kept public so a component
        /// can colour its own preview geometry the same way this Goo does.
        /// </summary>
        public static System.Drawing.Color Color(double normalizedScore)
        {
            if (double.IsNaN(normalizedScore))
            {
                return System.Drawing.Color.FromArgb(160, 160, 160);
            }

            double value = Math.Max(-1.0, Math.Min(1.0, normalizedScore));
            if (value > 0)
            {
                // Pale to saturated red as the benefit rises.
                int fade = (int)Math.Round(200.0 * (1.0 - value));
                return System.Drawing.Color.FromArgb(220, fade, fade);
            }

            if (value < 0)
            {
                int fade = (int)Math.Round(200.0 * (1.0 + value));
                return System.Drawing.Color.FromArgb(fade, fade, 220);
            }

            return System.Drawing.Color.FromArgb(160, 160, 160);
        }

        /// <summary>The drawable points of a field, with their colours. Empty rather than null when there is nothing to draw.</summary>
        public static List<Tuple<Point3d, System.Drawing.Color>> PreviewPoints(ShadingPotentialField field, double wantedSolarPenalty = 1.0)
        {
            List<Tuple<Point3d, System.Drawing.Color>> result = new List<Tuple<Point3d, System.Drawing.Color>>();

            ShadingVolume volume = field?.Volume;
            if (volume == null)
            {
                return result;
            }

            for (int i = 0; i < volume.VoxelCount; i++)
            {
                double normalized = field.NormalizedScore(i, wantedSolarPenalty);
                if (double.IsNaN(normalized) || Math.Abs(normalized) < PreviewCutoff)
                {
                    continue;
                }

                Point3D centre = volume.GetCentre(i);
                if (centre == null)
                {
                    continue;
                }

                result.Add(new Tuple<Point3d, System.Drawing.Color>(Geometry.Rhino.Convert.ToRhino(centre), Color(normalized)));
            }

            return result;
        }

        public BoundingBox ClippingBox
        {
            get
            {
                ShadingVolume volume = Value?.Volume;
                if (volume == null)
                {
                    return BoundingBox.Empty;
                }

                BoundingBox result = BoundingBox.Empty;
                for (int i = 0; i < volume.VoxelCount; i++)
                {
                    Point3D centre = volume.GetCentre(i);
                    if (centre != null)
                    {
                        result.Union(Geometry.Rhino.Convert.ToRhino(centre));
                    }
                }

                return result;
            }
        }

        public void DrawViewportWires(GH_PreviewWireArgs args)
        {
            foreach (Tuple<Point3d, System.Drawing.Color> point in PreviewPoints(Value))
            {
                args.Pipeline.DrawPoint(point.Item1, PointStyle.RoundSimple, PreviewPointSize, point.Item2);
            }
        }

        public void DrawViewportMeshes(GH_PreviewMeshArgs args)
        {
            // Points only: a shaded solid would imply a surface the field does not have.
        }
    }

    public class GooShadingPotentialFieldParam : GH_PersistentParam<GooShadingPotentialField>, IGH_PreviewObject
    {
        public override Guid ComponentGuid => new Guid("8d5e1b02-3a44-4c7e-9e6b-2b1f0d54c002");

        protected override System.Drawing.Bitmap Icon => Resources.SAM_SolarCalculator;

        bool IGH_PreviewObject.Hidden { get; set; }

        bool IGH_PreviewObject.IsPreviewCapable => !VolatileData.IsEmpty;

        BoundingBox IGH_PreviewObject.ClippingBox => Preview_ComputeClippingBox();

        void IGH_PreviewObject.DrawViewportMeshes(IGH_PreviewArgs args) => Preview_DrawMeshes(args);

        void IGH_PreviewObject.DrawViewportWires(IGH_PreviewArgs args) => Preview_DrawWires(args);

        public GooShadingPotentialFieldParam()
            : base(typeof(ShadingPotentialField).Name, typeof(ShadingPotentialField).Name, "Where shading in front of an aperture would help and where it would harm, in kWh", "Params", "SAM")
        {
        }

        protected override GH_GetterResult Prompt_Plural(ref List<GooShadingPotentialField> values)
        {
            return GH_GetterResult.cancel;
        }

        protected override GH_GetterResult Prompt_Singular(ref GooShadingPotentialField value)
        {
            return GH_GetterResult.cancel;
        }

        public override void AppendAdditionalMenuItems(System.Windows.Forms.ToolStripDropDown menu)
        {
            Menu_AppendItem(menu, "Save As...", Menu_SaveAs, VolatileData.AllData(true).Any());

            base.AppendAdditionalMenuItems(menu);
        }

        private void Menu_SaveAs(object sender, EventArgs e)
        {
            Core.Grasshopper.Query.SaveAs(VolatileData);
        }
    }
}
