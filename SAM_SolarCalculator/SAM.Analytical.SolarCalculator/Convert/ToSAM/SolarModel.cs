// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using SAM.Geometry.Object.Spatial;
using SAM.Geometry.SolarCalculator;
using SAM.Geometry.Spatial;
using SAM.Weather;
using SAM.Weather.SolarCalculator;
using System.Collections.Generic;

namespace SAM.Analytical.SolarCalculator
{
    public static partial class Convert
    {
        public static SolarModel ToSAM_SolarModel(this AnalyticalModel analyticalModel)
        {
            if(analyticalModel == null)
            {
                return null;
            }

            AdjacencyCluster adjacencyCluster = analyticalModel.AdjacencyCluster;
            if (adjacencyCluster == null)
            {
                return null;
            }

            SolarModel result = new SolarModel(analyticalModel.Location);

            List<Panel> panels = adjacencyCluster.GetPanels();
            if(panels != null && panels.Count != 0)
            {
                foreach(Panel panel in panels)
                {
                    List<Space> spaces = adjacencyCluster.GetSpaces(panel);
                    if (spaces != null && spaces.Count >= 2)
                    {
                        continue;
                    }

                    if(!panel.IsExposedToSun())
                    {
                        continue;
                    }

                    LinkedFace3D linkedFace3D = new LinkedFace3D(panel.Guid, panel.Face3D);
                    result.Add(linkedFace3D);

                    // Include the panel's windows as their own surfaces so SAM shades glazing too —
                    // the dominant solar-gain surfaces. Mirror TAS, which reports shade proportion per
                    // window as TWO coplanar surfaces: the outer opening and the inset glazing pane.
                    // Emitting both keeps SAM's surface set 1:1 with the TAS import. (Aperture.Face3D
                    // itself is the frame ring — a polygon with the pane cut out — so it is not used.)
                    List<Aperture> apertures = panel.Apertures;
                    if (apertures != null)
                    {
                        foreach (Aperture aperture in apertures)
                        {
                            if (aperture == null)
                            {
                                continue;
                            }

                            // Window opening (outer boundary) — matches the TAS per-window opening surface.
                            IClosedPlanar3D externalEdge3D = aperture.GetExternalEdge3D();
                            if (externalEdge3D != null)
                            {
                                result.Add(new LinkedFace3D(aperture.Guid, new Face3D(externalEdge3D)));
                            }

                            // Glazing pane(s) — matches the TAS inset glazing surface. Fresh Guids so each
                            // pane is a distinct coverage surface (the aperture Guid is used by the opening).
                            List<Face3D> paneFace3Ds = aperture.GetPaneFace3Ds();
                            if (paneFace3Ds != null)
                            {
                                foreach (Face3D paneFace3D in paneFace3Ds)
                                {
                                    if (paneFace3D == null)
                                    {
                                        continue;
                                    }

                                    result.Add(new LinkedFace3D(System.Guid.NewGuid(), paneFace3D));
                                }
                            }
                        }
                    }
                }

            }

            WeatherData weatherData = analyticalModel.GetValue<WeatherData>(AnalyticalModelParameter.WeatherData);
            if(weatherData != null)
            {
                result.SetValue(SolarModelParameter.WeatherData, weatherData);
            }

            return result;
        }

        public static SolarModel ToSAM_SolarModel(this BuildingModel buildingModel)
        {
            if (buildingModel == null)
            {
                return null;
            }

            SolarModel result = new SolarModel(buildingModel.Location);

            List<IPartition> partitions = buildingModel.GetPartitions();
            if (partitions != null && partitions.Count != 0)
            {
                foreach (IPartition partition in partitions)
                {
                    if(partition == null)
                    {
                        continue;
                    }
                    
                    if(!buildingModel.Shade(partition))
                    {
                        List<Space> spaces = buildingModel.GetSpaces(partition);
                        if (spaces != null && spaces.Count >= 2)
                        {
                            continue;
                        }
                    }

                    LinkedFace3D linkedFace3D = Geometry.Object.Spatial.Create.LinkedFace3D(partition);
                    if(linkedFace3D == null)
                    {
                        continue;
                    }

                    result.Add(linkedFace3D);
                }
            }

            return result;
        }
    }
}
