// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using SAM.Core;
using SAM.Core.SolarCalculator;

namespace SAM.Analytical.SolarCalculator
{
    /// <summary>
    /// ONE physical awning designed for a GROUP of apertures, with the identity of that group.
    ///
    /// The single-aperture ShadingDevice deliberately carries one aperture and is refused against
    /// another; that contract is unchanged. A grouped awning is a different engineering object — one
    /// unit spanning several windows — so it gets its own type instead of a synthetic group Guid
    /// written into a field documented as an aperture Guid.
    ///
    /// The typology is stored, not the geometry: the canopy is regenerated from the group's frame
    /// and combined bounds, so a grouped device is always rebuilt against the group it was designed
    /// for. The null device (NoShading) means "build nothing over this group" and is the honest
    /// NO SHADE answer, exactly as in the single-aperture path.
    /// </summary>
    public class GroupedShadingDevice : IJSAMObject, ISolarObject
    {
        private Guid groupGuid;
        private Guid panelGuid;
        private List<Guid> apertureGuids = new List<Guid>();
        private IShadingTypology typology;
        private AwningSpecification specification;

        public GroupedShadingDevice(Guid groupGuid, Guid panelGuid, IEnumerable<Guid> apertureGuids, IShadingTypology typology, AwningSpecification specification)
        {
            this.groupGuid = groupGuid;
            this.panelGuid = panelGuid;
            this.apertureGuids = new List<Guid>(apertureGuids ?? new List<Guid>());
            this.typology = typology;
            this.specification = specification;
        }

        public GroupedShadingDevice(GroupedShadingDevice groupedShadingDevice)
        {
            if (groupedShadingDevice != null)
            {
                groupGuid = groupedShadingDevice.groupGuid;
                panelGuid = groupedShadingDevice.panelGuid;
                apertureGuids = new List<Guid>(groupedShadingDevice.apertureGuids);
                typology = groupedShadingDevice.typology;
                specification = groupedShadingDevice.specification;
            }
        }

        public GroupedShadingDevice(JsonObject jObject)
        {
            FromJsonObject(jObject);
        }

        /// <summary>The deterministic identity of the aperture group this device belongs to.</summary>
        public Guid GroupGuid { get { return groupGuid; } }

        /// <summary>The host panel every member aperture belongs to.</summary>
        public Guid PanelGuid { get { return panelGuid; } }

        /// <summary>Member aperture GUIDs, ordered left-to-right across the facade.</summary>
        public List<Guid> ApertureGuids { get { return new List<Guid>(apertureGuids); } }

        /// <summary>The device itself. NoShading is the null device: "build nothing over this group".</summary>
        public IShadingTypology Typology { get { return typology; } }

        /// <summary>The product preset the device was constrained by. Null for the generic single-aperture family.</summary>
        public AwningSpecification Specification { get { return specification; } }

        public string SpecificationName { get { return specification?.Name; } }

        public string TypologyName { get { return typology?.Name; } }

        public bool IsNoShading { get { return typology is NoShading; } }

        /// <summary>The awning width this device spans [m]: group envelope width plus both side extensions. NaN for the null device.</summary>
        public double Width(ApertureShadingGroup group)
        {
            if (group == null || !(typology is RetractableAwning awning))
            {
                return double.NaN;
            }

            return group.Width + 2.0 * awning.GetParameter("ExtensionBeyondJambs");
        }

        /// <summary>The shared element set of this device over the group: built ONCE in world
        /// coordinates from the group frame, never one copy per member aperture.</summary>
        public List<ShadingElement> ShadingElements(ApertureShadingGroup group)
        {
            if (group == null || typology == null)
            {
                return null;
            }

            if (typology is NoShading)
            {
                return new List<ShadingElement>();
            }

            if (!(typology is RetractableAwning awning))
            {
                return null;
            }

            return awning.ShadingElements(group.Plane, group.MinX, group.MaxX, group.MaxY);
        }

        public override string ToString()
        {
            return typology == null ? "GroupedShadingDevice (none)" : typology.Name + " @ " + groupGuid;
        }

        public bool FromJsonObject(JsonObject jObject)
        {
            if (jObject == null)
            {
                return false;
            }

            if (jObject.ContainsKey("GroupGuid")) { Guid.TryParse(jObject["GroupGuid"]?.GetValue<string>(), out groupGuid); }
            if (jObject.ContainsKey("PanelGuid")) { Guid.TryParse(jObject["PanelGuid"]?.GetValue<string>(), out panelGuid); }

            apertureGuids = new List<Guid>();
            if (jObject.ContainsKey("ApertureGuids") && jObject["ApertureGuids"] is JsonArray guidsArray)
            {
                foreach (JsonNode node in guidsArray)
                {
                    if (Guid.TryParse(node?.GetValue<string>(), out Guid guid))
                    {
                        apertureGuids.Add(guid);
                    }
                }
            }

            typology = jObject.ContainsKey("Typology")
                ? Core.Create.IJSAMObject<IShadingTypology>(jObject["Typology"] as JsonObject)
                : null;

            specification = jObject.ContainsKey("Specification")
                ? new AwningSpecification(jObject["Specification"] as JsonObject)
                : null;

            return true;
        }

        public JsonObject ToJsonObject()
        {
            JsonObject jObject = new JsonObject();
            jObject.Add("_type", Core.Query.FullTypeName(this));
            jObject.Add("GroupGuid", groupGuid.ToString());
            jObject.Add("PanelGuid", panelGuid.ToString());

            JsonArray guidsArray = new JsonArray();
            foreach (Guid guid in apertureGuids)
            {
                guidsArray.Add(guid.ToString());
            }
            jObject.Add("ApertureGuids", guidsArray);

            if (typology != null)
            {
                jObject.Add("Typology", typology.ToJsonObject());
            }

            if (specification != null)
            {
                jObject.Add("Specification", specification.ToJsonObject());
            }

            return jObject;
        }
    }
}
