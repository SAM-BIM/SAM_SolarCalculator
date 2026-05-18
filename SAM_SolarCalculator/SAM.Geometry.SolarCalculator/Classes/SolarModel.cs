// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors
using System.Text.Json.Nodes;
using SAM.Core;
using SAM.Core.SolarCalculator;
using SAM.Geometry.Object.Spatial;
using SAM.Geometry.Spatial;
using System.Collections.Generic;

namespace SAM.Geometry.SolarCalculator
{
    public class SolarModel : SAMModel, ISolarObject
    {
        private Location location;
        private SolarRelationCluster solarRelationCluster;

        public SolarModel(JsonObject jObject)
            : base(jObject)
        {

        }

        public SolarModel(Location location)
            : base()
        {
            if(location != null)
            {
                this.location = new Location(location);
            }
        }

        public Location Location
        {
            get
            {
                return location == null ? null : new Location(location);
            }
        }

        public bool Add(LinkedFace3D linkedFace3D)
        {
            if(linkedFace3D == null)
            {
                return false;
            }

            if (solarRelationCluster == null)
                solarRelationCluster = new SolarRelationCluster();

            LinkedFace3D solarFace_Temp = new LinkedFace3D(linkedFace3D);

            if (!solarRelationCluster.AddObject(solarFace_Temp))
            {
                return false;
            }

            return true;
        }

        public bool Add(SolarFaceSimulationResult solarFaceSimulationResult, System.Guid linkedFace3DGuid)
        {
            if (solarFaceSimulationResult == null)
            {
                return false;
            }

            if (solarRelationCluster == null)
            {
                solarRelationCluster = new SolarRelationCluster();
            }

            bool result = solarRelationCluster.AddObject(solarFaceSimulationResult);
            if (!result)
            {
                return result;
            }

            if (linkedFace3DGuid != System.Guid.Empty)
            {
                LinkedFace3D linkedFace3D = solarRelationCluster.GetObject<LinkedFace3D>(linkedFace3DGuid);
                if (linkedFace3D != null)
                {
                    solarRelationCluster.AddRelation(solarFaceSimulationResult, linkedFace3D);
                }
            }

            return result;
        }
        
        public List<LinkedFace3D> GetLinkedFace3Ds()
        {
            return solarRelationCluster?.GetObjects<LinkedFace3D>()?.ConvertAll(x => x == null ? null : new LinkedFace3D(x));
        }

        public List<SolarFaceSimulationResult> GetSolarFaceSimulationResults()
        {
            return solarRelationCluster?.GetObjects<SolarFaceSimulationResult>()?.ConvertAll(x => x == null ? null : new SolarFaceSimulationResult(x));
        }

        public override bool FromJsonObject(JsonObject jObject)
        {
            if (!base.FromJsonObject(jObject))
                return false;

            if (jObject.ContainsKey("Location"))
                location = new Location(jObject["Location"] as JsonObject);

            if (jObject.ContainsKey("SolarRelationCluster"))
                solarRelationCluster = new SolarRelationCluster(jObject["SolarRelationCluster"] as JsonObject);

            return true;
        }

        public override JsonObject ToJsonObject()
        {
            JsonObject jObject = base.ToJsonObject();
            if (jObject == null)
                return jObject;

            if (location != null)
                jObject.Add("Location", location.ToJsonObject());

            if (solarRelationCluster != null)
                jObject.Add("SolarRelationCluster", solarRelationCluster.ToJsonObject());

            return jObject;
        }
    }
}
