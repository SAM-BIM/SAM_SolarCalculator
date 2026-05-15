using System.Text.Json.Nodes;
using SAM.Core;
using SAM.Core.SolarCalculator;

namespace SAM.Geometry.SolarCalculator
{
    public class SolarRelationCluster : SAMObjectRelationCluster<IJSAMObject>, ISolarObject
    {
        public SolarRelationCluster()
            : base()
        {

        }

        public SolarRelationCluster(JsonObject jObject)
            : base(jObject)
        {

        }

        public SolarRelationCluster(SolarRelationCluster solarRelationCluster)
            :base(solarRelationCluster)
        {

        }

        public SolarRelationCluster(SolarRelationCluster solarRelationCluster, bool deepClone)
            : base(solarRelationCluster, deepClone)
        {

        }

    }
}
