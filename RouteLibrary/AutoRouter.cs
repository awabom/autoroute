using Geolocation;
using QuickGraph.Algorithms;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace RouteLibrary
{
	internal class AutoRouteEntry
	{
		public List<string> WaypointNames { get; set; }
		public XElement RouteElement { get; set; }

		public override string ToString()
		{
			return "Auto Route: " + string.Join(", ", WaypointNames.ToArray());
		}
	}

	public class AutoRouter
	{
		public static XNamespace nsGpx = "http://www.topografix.com/GPX/1/1";
		public static XNamespace nsOpenCpn = "http://www.opencpn.org";

		/// <summary>
		/// Finds all routes containing the text "(auto)" and tries to auto-route them by using all non-auto routes
		/// </summary>
		/// <param name="updateNavObjXmlFileName"></param>
		public void MakeRoutes(string updateNavObjXmlFileName)
		{
			XDocument docNavObj = XDocument.Load(updateNavObjXmlFileName);

			var autoRoutes = new List<AutoRouteEntry>();

			var idToVertexMap = new Dictionary<string, RouteVertex>();
			var nameToVertexMap = new Dictionary<string, RouteVertex>();
			var edgeWeightMap = new Dictionary<QuickGraph.Edge<RouteVertex>, double>();
			var nonUniqueNames = new List<string>();

			// Process each (non-auto) route into a "all routes" graph, 
			// and add auto-routes to a separate list for later processing
			var graph = new QuickGraph.UndirectedGraph<RouteVertex, QuickGraph.Edge<RouteVertex>>();
			foreach (var elemRoute in docNavObj.Element(nsGpx + "gpx").Elements(nsGpx + "rte"))
			{
				RouteVertex previousVertex = null;

				// Is this an auto-route?
				AutoRouteEntry autoRouteEntry = GetAutoRouteEntry(elemRoute);
				if (autoRouteEntry != null)
				{
					autoRoutes.Add(autoRouteEntry);
					continue;
				}

				// Process each route point in route, adding vertices and edges as we follow the route
				foreach (var elemRoutePoint in elemRoute.Elements(nsGpx + "rtept"))
				{
					string routePointId = RouteVertex.GetElementId(elemRoutePoint);

					// Get this route point's vertex (existing, or create new and add to graph)
					RouteVertex elementVertex;

					if (!idToVertexMap.TryGetValue(routePointId, out elementVertex))
					{
						elementVertex = new RouteVertex { RoutePointElement = elemRoutePoint };
						graph.AddVertex(elementVertex);

						// Add new vertex to dictionaries
						idToVertexMap.Add(routePointId, elementVertex);
						string elementVertexName = elementVertex.Name;
						if (nameToVertexMap.ContainsKey(elementVertexName))
						{
							nonUniqueNames.Add(elementVertexName);
						}
						else
						{
							nameToVertexMap.Add(elementVertexName, elementVertex);
						}
					}

					// Did we get to this vertex from another vertex? If so, add the edge and calculate its weight
					if (previousVertex != null)
					{
						var edge = new QuickGraph.Edge<RouteVertex>(previousVertex, elementVertex);
						graph.AddEdge(edge);

						// Pre-calculate edge weight = distance in meters
						Coordinate coordOrigin = previousVertex.Coordinate;
						Coordinate coordDestination = elementVertex.Coordinate;
						double weight = GeoCalculator.GetDistance(coordOrigin, coordDestination, 1, DistanceUnit.Meters);
						edgeWeightMap[edge] = weight;
					}

					// Save current vertex as previous for next vertex
					previousVertex = elementVertex;
				}
			}

			// Find best routes for each AutoRouteEntry
			foreach (AutoRouteEntry arEntry in autoRoutes)
			{
				// Check that auto-route uses unique names
				var nonUniqueUsed = arEntry.WaypointNames.Where(x => nonUniqueNames.Contains(x)).ToList();
				if (nonUniqueUsed.Count > 0)
				{
					SetRouteError(arEntry.RouteElement, "Non-unique waypoint name(s): " + string.Join(", ", nonUniqueUsed));
					continue;
				}
				// Check for unknown names
				var unknownWaypoints = arEntry.WaypointNames.Where(x => !nameToVertexMap.ContainsKey(x)).ToList();
				if (unknownWaypoints.Count > 0)
				{
					SetRouteError(arEntry.RouteElement, "Unknown waypoint name(s): " + string.Join(", ", unknownWaypoints));
					continue;
				}

				var routeElement = arEntry.RouteElement;
				bool firstPart = true;
				bool firstWaypoint = true;

				// Process the auto route a 'pair of waypoints' at a time
				for (int i = 0; i < arEntry.WaypointNames.Count - 1; i++)
				{
					RouteVertex start = nameToVertexMap[arEntry.WaypointNames[i]];
					RouteVertex stop = nameToVertexMap[arEntry.WaypointNames[i + 1]];

					var shortestPaths = graph.ShortestPathsDijkstra((x) => edgeWeightMap[x], start);
					if (shortestPaths(stop, out var result))
					{
						// If first part of auto route, clear the existing route data
						if (firstPart)
						{
							foreach (var deleteMe in routeElement.Elements(nsGpx + "rtept").ToList())
							{
								deleteMe.Remove();
							}
							firstPart = false;
						}

						RouteVertex currentPos = start;
						foreach (var edge in result)
						{
							// Graph is not directional, so edges can be in 'reverse' direction
							// We should not use current position vertex as next vertex
							RouteVertex nextVertex = edge.Source != currentPos ? edge.Source : edge.Target;

							// If this is the very first waypoint of the complete auto route - add it to output (if not first edge, the waypoint has already been put into the route)
							if (firstWaypoint)
							{
								var pointElement = new XElement(currentPos.RoutePointElement);
								routeElement.Add(pointElement);
								firstWaypoint = false;
							}

							routeElement.Add(new XElement(nextVertex.RoutePointElement));
							currentPos = nextVertex;
						}
					}
					else
					{
						SetRouteError(arEntry.RouteElement, "No route found from: \"" + start.Name + "\" to \"" + stop.Name + "\"");
						break;
					}
				}
			}

			// Overwrite original navobj file
			docNavObj.Save(updateNavObjXmlFileName);
		}

		private void SetRouteError(XElement routeElement, string errorMsg)
		{
			string autoString = (string)routeElement.Element(nsGpx + "name");
			autoString = autoString.Substring(0, autoString.IndexOf("(auto)")) + "(auto)";
			routeElement.SetElementValue(nsGpx + "name", autoString + " Error: " + errorMsg);
		}

		/// <summary>
		/// Parses the name of a route into an auto-route entry 
		/// Format is: "waypointname1, waypointname2, waypointname3 (auto)"
		/// </summary>
		/// <param name="elemRoute"></param>
		/// <returns>An entry if an auto-route, or null if a normal route</returns>
		private AutoRouteEntry GetAutoRouteEntry(XElement elemRoute)
		{
			// Example name for auto: "Svinninge, Grinda Hamn (auto)"
			string name = (string)elemRoute.Element(nsGpx + "name") ?? "";
			if (name.Contains("(auto)"))
			{
				var onlyNames = name.Substring(0, name.IndexOf("(auto)"));
				return new AutoRouteEntry { RouteElement = elemRoute, WaypointNames = onlyNames.Split(',').Select(x => x.Trim()).ToList() };
			}
			else
			{
				return null;
			}
		}
	}

	internal class RouteVertex
	{
		public XElement RoutePointElement { get; set; }

		public string Name
		{
			get { return GetElementName(RoutePointElement); }
		}

		public Coordinate Coordinate
		{
			get
			{
				return new Coordinate
				{
					Latitude = double.Parse((string)RoutePointElement.Attribute("lat"), System.Globalization.CultureInfo.InvariantCulture),
					Longitude = double.Parse((string)RoutePointElement.Attribute("lon"), System.Globalization.CultureInfo.InvariantCulture)
				};
			}
		}

		public static string GetElementId(XElement routePointElement)
		{
			return (string)routePointElement.Element(AutoRouter.nsGpx + "extensions").Element(AutoRouter.nsOpenCpn + "guid") ?? GetElementName(routePointElement);
		}

		public static string GetElementName(XElement routePointElement)
		{
			return (string)routePointElement.Element(AutoRouter.nsGpx + "name");
		}
	}
}

