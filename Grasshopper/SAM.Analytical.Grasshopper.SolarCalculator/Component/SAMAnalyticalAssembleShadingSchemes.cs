// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using SAM.Analytical.Grasshopper.SolarCalculator.Properties;
using SAM.Analytical.SolarCalculator;
using SolarCreate = SAM.Analytical.SolarCalculator.Create;
using SolarQuery = SAM.Analytical.SolarCalculator.Query;
using SAM.Core.Grasshopper;
using System;
using System.Collections.Generic;

namespace SAM.Analytical.Grasshopper.SolarCalculator
{
    public class SAMAnalyticalAssembleShadingSchemes : GH_SAMVariableOutputParameterComponent
    {
        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid => new Guid("c1204451-fe40-475c-bf10-4dbf7fad06da");

        /// <summary>The latest version of this component.</summary>
        public override string LatestComponentVersion => "1.0.0";

        /// <summary>Provides an Icon for the component.</summary>
        protected override System.Drawing.Bitmap Icon => Resources.SAM_SolarCalculator;

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        public SAMAnalyticalAssembleShadingSchemes()
          : base("SAMAnalytical.AssembleShadingSchemes", "SAMAnalytical.AssembleShadingSchemes",
              "SUMMARY\nAssembles the COMPLETE shading schemes a comparison ranks.\n\nOne scheme is one physical proposal over the WHOLE aperture scope: every window of a conventional family (from SAMAnalytical.RationaliseShading), or one grouped awning run (from SAMAnalytical.RationaliseAwningGroup), or the zero-device No Shade baseline.\n\nEvery scheme covers exactly the same aperture set, which is what makes SAMAnalytical.CompareShading able to rank them on one basis.\n\nA family whose search returned no shading for some windows still produces a scheme - those windows carry the null device - and a family whose every window returned no shading is still emitted as a distinct option, because 'this family was searched and lost' is a different engineering statement from the baseline.\n\nOUTPUTS\n  shadingSchemes - the schemes, ready for SAMAnalytical.CompareShading.\n  schemeNames - one name per scheme.\n  apertureGuids - the windows of each scheme, one branch per scheme.\n  physicalDeviceCounts - how many separate physical units each scheme builds. One grouped awning over three windows is 1, not 3.\n  statuses - OK / NO SHADE / WARNING / NOT EVALUATED per scheme.\n  warnings - the warnings per scheme, one branch per scheme.\n  successful - True when the schemes were assembled.",
              "SAM", "Solar")
        {
        }

        protected override GH_SAMParam[] Inputs
        {
            get
            {
                List<GH_SAMParam> result = new List<GH_SAMParam>();
                result.Add(new GH_SAMParam(new GooApertureSolarTargetParam() { Name = "_apertureSolarTargets", NickName = "_apertureSolarTargets", Description = "The windows - every scheme will cover exactly this set", Access = GH_ParamAccess.list }, ParamVisibility.Binding));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_GenericObject() { Name = "_optimisedShadingResults_", NickName = "_optimisedShadingResults_", Description = "The results from SAMAnalytical.RationaliseShading.optimisedShadingResults, all apertures, all families", Access = GH_ParamAccess.list, Optional = true }, ParamVisibility.Binding));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_GenericObject() { Name = "_groupedAwningResults_", NickName = "_groupedAwningResults_", Description = "The results from SAMAnalytical.RationaliseAwningGroup.groupedAwningResults", Access = GH_ParamAccess.list, Optional = true }, ParamVisibility.Binding));

                global::Grasshopper.Kernel.Parameters.Param_Boolean includeNoShade = new global::Grasshopper.Kernel.Parameters.Param_Boolean() { Name = "_includeNoShade_", NickName = "_includeNoShade_", Description = "Add the zero-device No Shade baseline scheme. KEEP TRUE: it is the anchor every other scheme's baseline is checked against.\nDefault true", Access = GH_ParamAccess.item };
                includeNoShade.SetPersistentData(true);
                result.Add(new GH_SAMParam(includeNoShade, ParamVisibility.Binding));

                global::Grasshopper.Kernel.Parameters.Param_Boolean run = new global::Grasshopper.Kernel.Parameters.Param_Boolean() { Name = "_run", NickName = "_run", Description = "Nothing is calculated until this is true", Access = GH_ParamAccess.item };
                run.SetPersistentData(false);
                result.Add(new GH_SAMParam(run, ParamVisibility.Binding));
                return result.ToArray();
            }
        }

        protected override GH_SAMParam[] Outputs
        {
            get
            {
                List<GH_SAMParam> result = new List<GH_SAMParam>();
                result.Add(new GH_SAMParam(new GooSAMObjectParam() { Name = "shadingSchemes", NickName = "shadingSchemes", Description = "The complete shading schemes, ready for SAMAnalytical.CompareShading", Access = GH_ParamAccess.list }, ParamVisibility.Binding));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_String() { Name = "schemeNames", NickName = "schemeNames", Description = "One name per scheme", Access = GH_ParamAccess.list }, ParamVisibility.Binding));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_String() { Name = "apertureGuids", NickName = "apertureGuids", Description = "The windows of each scheme, one branch per scheme", Access = GH_ParamAccess.tree }, ParamVisibility.Binding));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_Integer() { Name = "physicalDeviceCounts", NickName = "physicalDeviceCounts", Description = "Separate physical units per scheme - one grouped awning over three windows is 1, not 3", Access = GH_ParamAccess.list }, ParamVisibility.Binding));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_String() { Name = "statuses", NickName = "statuses", Description = "OK / NO SHADE / WARNING / NOT EVALUATED per scheme", Access = GH_ParamAccess.list }, ParamVisibility.Binding));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_String() { Name = "warnings", NickName = "warnings", Description = "The warnings per scheme, one branch per scheme", Access = GH_ParamAccess.tree }, ParamVisibility.Voluntary));
                result.Add(new GH_SAMParam(new global::Grasshopper.Kernel.Parameters.Param_Boolean() { Name = "successful", NickName = "successful", Description = "Successful?", Access = GH_ParamAccess.item }, ParamVisibility.Binding));
                return result.ToArray();
            }
        }

        protected override void SolveInstance(IGH_DataAccess dataAccess)
        {
            int index_Successful = Params.IndexOfOutputParam("successful");
            if (index_Successful != -1)
            {
                dataAccess.SetData(index_Successful, false);
            }

            int index = Params.IndexOfInputParam("_run");
            bool run = false;
            if (index == -1 || !dataAccess.GetData(index, ref run))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Please supply a value for _run.");
                return;
            }

            if (!run)
            {
                return;
            }

            index = Params.IndexOfInputParam("_apertureSolarTargets");
            List<ApertureSolarTarget> targets = new List<ApertureSolarTarget>();
            if (index == -1 || !dataAccess.GetDataList(index, targets) || targets.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Please supply at least one aperture solar target from SAMAnalytical.ApertureSolarTargets.");
                return;
            }

            List<OptimisedShadingResult> optimisedShadingResults = new List<OptimisedShadingResult>();
            index = Params.IndexOfInputParam("_optimisedShadingResults_");
            if (index != -1)
            {
                List<GH_ObjectWrapper> wrappers = new List<GH_ObjectWrapper>();
                dataAccess.GetDataList(index, wrappers);
                foreach (GH_ObjectWrapper wrapper in wrappers)
                {
                    OptimisedShadingResult result = Query.Value<OptimisedShadingResult>(wrapper);
                    if (result != null)
                    {
                        optimisedShadingResults.Add(result);
                    }
                }
            }

            List<GroupedAwningResult> groupedAwningResults = new List<GroupedAwningResult>();
            index = Params.IndexOfInputParam("_groupedAwningResults_");
            if (index != -1)
            {
                List<GH_ObjectWrapper> wrappers = new List<GH_ObjectWrapper>();
                dataAccess.GetDataList(index, wrappers);
                foreach (GH_ObjectWrapper wrapper in wrappers)
                {
                    GroupedAwningResult result = Query.Value<GroupedAwningResult>(wrapper);
                    if (result != null)
                    {
                        groupedAwningResults.Add(result);
                    }
                }
            }

            bool includeNoShade = true;
            index = Params.IndexOfInputParam("_includeNoShade_");
            if (index != -1)
            {
                dataAccess.GetData(index, ref includeNoShade);
            }

            List<ShadingScheme> schemes = SolarCreate.ShadingSchemes(targets, optimisedShadingResults, groupedAwningResults, includeNoShade, out string message);
            if (schemes == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, message ?? "The shading schemes could not be assembled.");
                return;
            }

            index = Params.IndexOfOutputParam("shadingSchemes");
            if (index != -1)
            {
                dataAccess.SetDataList(index, schemes.ConvertAll(x => new GooSAMObject(x)));
            }

            index = Params.IndexOfOutputParam("schemeNames");
            if (index != -1)
            {
                dataAccess.SetDataList(index, schemes.ConvertAll(x => x.Name));
            }

            index = Params.IndexOfOutputParam("apertureGuids");
            if (index != -1)
            {
                GH_Structure<GH_String> dataTree = new GH_Structure<GH_String>();
                int branch = 0;
                foreach (ShadingScheme scheme in schemes)
                {
                    GH_Path path = new GH_Path(branch++);
                    foreach (Guid guid in scheme.ApertureGuids)
                    {
                        dataTree.Append(new GH_String(guid.ToString()), path);
                    }
                }

                dataAccess.SetDataTree(index, dataTree);
            }

            index = Params.IndexOfOutputParam("physicalDeviceCounts");
            if (index != -1)
            {
                dataAccess.SetDataList(index, schemes.ConvertAll(x => x.PhysicalDeviceCount));
            }

            index = Params.IndexOfOutputParam("statuses");
            if (index != -1)
            {
                dataAccess.SetDataList(index, schemes.ConvertAll(x => SolarQuery.StatusText(x.Status)));
            }

            index = Params.IndexOfOutputParam("warnings");
            if (index != -1)
            {
                GH_Structure<GH_String> dataTree = new GH_Structure<GH_String>();
                int branch = 0;
                foreach (ShadingScheme scheme in schemes)
                {
                    GH_Path path = new GH_Path(branch++);
                    foreach (string warning in scheme.Warnings)
                    {
                        dataTree.Append(new GH_String(warning), path);
                    }
                }

                dataAccess.SetDataTree(index, dataTree);
            }

            if (index_Successful != -1)
            {
                dataAccess.SetData(index_Successful, true);
            }
        }
    }
}
