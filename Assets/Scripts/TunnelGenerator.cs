using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public class TunnelGenerator : MonoBehaviour
{
    [Header("Tunnel Settings")]
    public float minTunnelLength = 20f;
    public float maxTunnelLength = 100f;
    public int splineResolution = 20;
    public AnimationCurve radiusVariation = AnimationCurve.Linear(0, 1, 1, 1);
    
    [Header("Pathfinding")]
    public int maxPathfindingSteps = 1000;
    public float nodeSpacing = 2f;
    public float geologicalResistance = 0.5f;
    
    public struct TunnelPath
    {
        public List<float3> controlPoints;
        public List<float> radii;
        public int startChamberIndex;
        public int endChamberIndex;
    }
    
    // A* node for pathfinding
    private class PathNode
    {
        public float3 position;
        public float gCost; // Distance from start
        public float hCost; // Heuristic distance to goal
        public float fCost => gCost + hCost;
        public PathNode parent;
        public float resistance; // Geological resistance at this point
    }
    
    public List<TunnelPath> GenerateTunnelPaths(NativeArray<float3> chamberCenters, CaveSettings settings)
    {
        List<TunnelPath> tunnels = new List<TunnelPath>();
        
        // Build connectivity graph
        var connections = BuildConnectivityGraph(chamberCenters, settings);
        
        // Generate paths between connected chambers
        foreach (var connection in connections)
        {
            var path = GenerateTunnelPath(
                chamberCenters[connection.Item1],
                chamberCenters[connection.Item2],
                connection.Item1,
                connection.Item2,
                settings
            );
            
            if (path.controlPoints.Count > 0)
            {
                tunnels.Add(path);
            }
        }
        
        return tunnels;
    }
    
    List<(int, int)> BuildConnectivityGraph(NativeArray<float3> chamberCenters, CaveSettings settings)
    {
        List<(int, int)> connections = new List<(int, int)>();
        
        // Use Delaunay triangulation or simple nearest neighbor
        for (int i = 0; i < chamberCenters.Length; i++)
        {
            List<(int index, float distance)> neighbors = new List<(int, float)>();
            
            // Find nearest chambers
            for (int j = 0; j < chamberCenters.Length; j++)
            {
                if (i == j) continue;
                
                float distance = math.distance(chamberCenters[i], chamberCenters[j]);
                if (distance >= minTunnelLength && distance <= maxTunnelLength)
                {
                    neighbors.Add((j, distance));
                }
            }
            
            // Sort by distance
            neighbors.Sort((a, b) => a.distance.CompareTo(b.distance));
            
            // Connect to nearest N chambers
            int connectCount = math.min(settings.tunnelConnectionsPerChamber, neighbors.Count);
            for (int c = 0; c < connectCount; c++)
            {
                int j = neighbors[c].index;
                
                // Avoid duplicate connections
                bool exists = false;
                foreach (var conn in connections)
                {
                    if ((conn.Item1 == i && conn.Item2 == j) || 
                        (conn.Item1 == j && conn.Item2 == i))
                    {
                        exists = true;
                        break;
                    }
                }
                
                if (!exists)
                {
                    connections.Add((i, j));
                }
            }
        }
        
        return connections;
    }
    
    TunnelPath GenerateTunnelPath(float3 start, float3 end, int startIdx, int endIdx, CaveSettings settings)
    {
        TunnelPath path = new TunnelPath
        {
            controlPoints = new List<float3>(),
            radii = new List<float>(),
            startChamberIndex = startIdx,
            endChamberIndex = endIdx
        };
        
        // Use A* pathfinding with geological constraints
        var rawPath = FindPath(start, end, settings);
        
        if (rawPath.Count < 2)
        {
            // Fallback to direct path
            rawPath = new List<float3> { start, end };
        }
        
        // Smooth the path using Catmull-Rom splines
        path.controlPoints = SmoothPath(rawPath, settings.tunnelCurvature);
        
        // Calculate radius variations along the path
        for (int i = 0; i < path.controlPoints.Count; i++)
        {
            float t = (float)i / (path.controlPoints.Count - 1);
            float radiusMultiplier = radiusVariation.Evaluate(t);
            float baseRadius = math.lerp(settings.tunnelMinRadius, settings.tunnelMaxRadius, 
                Mathf.PerlinNoise(t * 10f, 0.5f));
            path.radii.Add(baseRadius * radiusMultiplier);
        }
        
        return path;
    }
    
    List<float3> FindPath(float3 start, float3 goal, CaveSettings settings)
    {
        List<float3> path = new List<float3>();
        
        // A* pathfinding implementation
        List<PathNode> openSet = new List<PathNode>();
        HashSet<int3> closedSet = new HashSet<int3>();
        
        PathNode startNode = new PathNode
        {
            position = start,
            gCost = 0,
            hCost = math.distance(start, goal),
            parent = null,
            resistance = 0
        };
        
        openSet.Add(startNode);
        
        int steps = 0;
        while (openSet.Count > 0 && steps < maxPathfindingSteps)
        {
            steps++;
            
            // Find node with lowest fCost
            PathNode current = openSet[0];
            for (int i = 1; i < openSet.Count; i++)
            {
                if (openSet[i].fCost < current.fCost || 
                    (openSet[i].fCost == current.fCost && openSet[i].hCost < current.hCost))
                {
                    current = openSet[i];
                }
            }
            
            openSet.Remove(current);
            closedSet.Add(WorldToGrid(current.position));
            
            // Check if we've reached the goal
            if (math.distance(current.position, goal) < nodeSpacing)
            {
                // Reconstruct path
                while (current != null)
                {
                    path.Add(current.position);
                    current = current.parent;
                }
                path.Reverse();
                break;
            }
            
            // Check neighbors
            foreach (var neighbor in GetNeighbors(current.position, nodeSpacing))
            {
                if (closedSet.Contains(WorldToGrid(neighbor)))
                    continue;
                
                // Calculate geological resistance
                float resistance = CalculateGeologicalResistance(neighbor, settings);
                float tentativeGCost = current.gCost + math.distance(current.position, neighbor) * (1f + resistance);
                
                PathNode neighborNode = null;
                foreach (var node in openSet)
                {
                    if (math.distance(node.position, neighbor) < 0.01f)
                    {
                        neighborNode = node;
                        break;
                    }
                }
                
                if (neighborNode == null)
                {
                    neighborNode = new PathNode
                    {
                        position = neighbor,
                        gCost = float.MaxValue,
                        hCost = math.distance(neighbor, goal),
                        parent = null,
                        resistance = resistance
                    };
                    openSet.Add(neighborNode);
                }
                
                if (tentativeGCost < neighborNode.gCost)
                {
                    neighborNode.parent = current;
                    neighborNode.gCost = tentativeGCost;
                }
            }
        }
        
        return path;
    }
    
    List<float3> GetNeighbors(float3 position, float spacing)
    {
        List<float3> neighbors = new List<float3>();
        
        // 3D neighbors (26 directions)
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    if (x == 0 && y == 0 && z == 0) continue;
                    
                    float3 offset = new float3(x, y, z) * spacing;
                    neighbors.Add(position + offset);
                }
            }
        }
        
        return neighbors;
    }
    
    float CalculateGeologicalResistance(float3 position, CaveSettings settings)
    {
        // Simulate geological layers and rock hardness
        float layerResistance = math.sin(position.y * settings.stratificationFrequency) * 0.5f + 0.5f;
        float rockResistance = settings.rockHardness;
        
        // Add noise for variation
        float3 noisePos = position * 0.1f + settings.noiseOffset;
        float noiseValue = noise.snoise(noisePos) * 0.5f + 0.5f;
        
        return (layerResistance * rockResistance + noiseValue) * geologicalResistance;
    }
    
    List<float3> SmoothPath(List<float3> rawPath, float smoothness)
    {
        if (rawPath.Count < 3)
            return rawPath;
        
        List<float3> smoothed = new List<float3>();
        
        // Add start point
        smoothed.Add(rawPath[0]);
        
        // Generate intermediate points using Catmull-Rom spline
        for (int i = 0; i < rawPath.Count - 1; i++)
        {
            float3 p0 = i > 0 ? rawPath[i - 1] : rawPath[i];
            float3 p1 = rawPath[i];
            float3 p2 = rawPath[i + 1];
            float3 p3 = i < rawPath.Count - 2 ? rawPath[i + 2] : rawPath[i + 1];
            
            // Generate points along the spline segment
            int subdivisions = Mathf.CeilToInt(math.distance(p1, p2) / nodeSpacing);
            for (int j = 1; j <= subdivisions; j++)
            {
                float t = (float)j / subdivisions;
                float3 point = CatmullRomSpline(p0, p1, p2, p3, t);
                
                // Apply smoothness factor
                point = math.lerp(math.lerp(p1, p2, t), point, smoothness);
                
                smoothed.Add(point);
            }
        }
        
        return smoothed;
    }
    
    float3 CatmullRomSpline(float3 p0, float3 p1, float3 p2, float3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        
        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }
    
    int3 WorldToGrid(float3 worldPos)
    {
        return new int3(
            (int)math.floor(worldPos.x / nodeSpacing),
            (int)math.floor(worldPos.y / nodeSpacing),
            (int)math.floor(worldPos.z / nodeSpacing)
        );
    }
}