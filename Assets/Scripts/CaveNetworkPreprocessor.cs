using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public class CaveNetworkPreprocessor : MonoBehaviour
{
    [Header("Network Generation")]
    public int worldSeed = 42;
    public float3 worldSize = new float3(1000, 100, 1000);
    public int chamberCount = 50;
    public int maxTunnelsPerChamber = 4;
    
    // Output data for job system
    public NativeArray<float3> chamberCenters;
    public List<TunnelData> tunnelNetwork;
    
    private Unity.Mathematics.Random random;
    
    public struct Chamber
    {
        public float3 position;
        public float radius;
        public List<int> connections;
    }
    
    public struct TunnelData
    {
        public int startChamber;
        public int endChamber;
        public List<float3> pathPoints;
        public float radius;
    }
    
    public void GenerateCaveNetwork(CaveSettings settings)
    {
        random = new Unity.Mathematics.Random((uint)worldSeed);
        
        // Step 1: Generate chamber positions
        List<Chamber> chambers = GenerateChambers(settings);
        
        // Step 2: Create tunnel network using Delaunay triangulation
        tunnelNetwork = GenerateTunnelNetwork(chambers, settings);
        
        // Step 3: Convert to native arrays for job system
        ConvertToNativeArrays(chambers, tunnelNetwork);
    }
    
    List<Chamber> GenerateChambers(CaveSettings settings)
    {
        List<Chamber> chambers = new List<Chamber>();
        
        // Use Poisson disk sampling for better distribution
        List<float3> points = PoissonDiskSampling3D(
            worldSize,
            settings.chamberMaxRadius * 2f,
            30
        );
        
        // Convert points to chambers
        foreach (var point in points)
        {
            if (chambers.Count >= chamberCount) break;
            
            Chamber chamber = new Chamber
            {
                position = point,
                radius = random.NextFloat(settings.chamberMinRadius, settings.chamberMaxRadius),
                connections = new List<int>()
            };
            
            chambers.Add(chamber);
        }
        
        return chambers;
    }
    
    List<float3> PoissonDiskSampling3D(float3 region, float minDistance, int maxAttempts)
    {
        float cellSize = minDistance / math.sqrt(3f);
        int3 gridSize = (int3)(region / cellSize);
        
        Dictionary<int3, int> grid = new Dictionary<int3, int>();
        List<float3> points = new List<float3>();
        List<float3> activeList = new List<float3>();
        
        // Start with random point
        float3 firstPoint = new float3(
            random.NextFloat(0, region.x),
            random.NextFloat(region.y * 0.2f, region.y * 0.8f), // Bias away from surface
            random.NextFloat(0, region.z)
        );
        
        points.Add(firstPoint);
        activeList.Add(firstPoint);
        grid[GetGridCell(firstPoint, cellSize)] = 0;
        
        while (activeList.Count > 0)
        {
            int randomIndex = random.NextInt(0, activeList.Count);
            float3 point = activeList[randomIndex];
            bool foundCandidate = false;
            
            for (int i = 0; i < maxAttempts; i++)
            {
                // Generate random point in annulus
                float angle1 = random.NextFloat(0, 2 * math.PI);
                float angle2 = random.NextFloat(0, math.PI);
                float radius = random.NextFloat(minDistance, minDistance * 2);
                
                float3 candidate = point + new float3(
                    radius * math.sin(angle2) * math.cos(angle1),
                    radius * math.cos(angle2),
                    radius * math.sin(angle2) * math.sin(angle1)
                );
                
                // Check bounds
                if (candidate.x < 0 || candidate.x >= region.x ||
                    candidate.y < 0 || candidate.y >= region.y ||
                    candidate.z < 0 || candidate.z >= region.z)
                    continue;
                
                // Check distance to nearby points
                if (IsValidPoint(candidate, cellSize, minDistance, points, grid, gridSize))
                {
                    points.Add(candidate);
                    activeList.Add(candidate);
                    grid[GetGridCell(candidate, cellSize)] = points.Count - 1;
                    foundCandidate = true;
                    break;
                }
            }
            
            if (!foundCandidate)
            {
                activeList.RemoveAt(randomIndex);
            }
        }
        
        return points;
    }
    
    bool IsValidPoint(float3 candidate, float cellSize, float minDistance, 
        List<float3> points, Dictionary<int3, int> grid, int3 gridSize)
    {
        int3 cell = GetGridCell(candidate, cellSize);
        
        // Check neighboring cells
        for (int x = -2; x <= 2; x++)
        {
            for (int y = -2; y <= 2; y++)
            {
                for (int z = -2; z <= 2; z++)
                {
                    int3 neighbor = cell + new int3(x, y, z);
                    
                    if (neighbor.x < 0 || neighbor.x >= gridSize.x ||
                        neighbor.y < 0 || neighbor.y >= gridSize.y ||
                        neighbor.z < 0 || neighbor.z >= gridSize.z)
                        continue;
                    
                    if (grid.ContainsKey(neighbor))
                    {
                        int pointIndex = grid[neighbor];
                        float distance = math.distance(candidate, points[pointIndex]);
                        if (distance < minDistance)
                            return false;
                    }
                }
            }
        }
        
        return true;
    }
    
    int3 GetGridCell(float3 point, float cellSize)
    {
        return (int3)(point / cellSize);
    }
    
    List<TunnelData> GenerateTunnelNetwork(List<Chamber> chambers, CaveSettings settings)
    {
        List<TunnelData> tunnels = new List<TunnelData>();
        
        // Create a graph of chamber connections
        // Using a simple approach: connect each chamber to its nearest neighbors
        for (int i = 0; i < chambers.Count; i++)
        {
            List<KeyValuePair<int, float>> distances = new List<KeyValuePair<int, float>>();
            
            // Calculate distances to all other chambers
            for (int j = 0; j < chambers.Count; j++)
            {
                if (i == j) continue;
                
                float distance = math.distance(chambers[i].position, chambers[j].position);
                distances.Add(new KeyValuePair<int, float>(j, distance));
            }
            
            // Sort by distance
            distances.Sort((a, b) => a.Value.CompareTo(b.Value));
            
            // Connect to nearest chambers (up to max connections)
            int connections = math.min(settings.tunnelConnectionsPerChamber, distances.Count);
            for (int c = 0; c < connections; c++)
            {
                int targetChamber = distances[c].Key;
                
                // Avoid duplicate tunnels
                bool alreadyConnected = false;
                foreach (var tunnel in tunnels)
                {
                    if ((tunnel.startChamber == i && tunnel.endChamber == targetChamber) ||
                        (tunnel.startChamber == targetChamber && tunnel.endChamber == i))
                    {
                        alreadyConnected = true;
                        break;
                    }
                }
                
                if (!alreadyConnected)
                {
                    TunnelData tunnel = GenerateTunnel(i, targetChamber, chambers, settings);
                    tunnels.Add(tunnel);
                }
            }
        }
        
        return tunnels;
    }
    
    TunnelData GenerateTunnel(int startIdx, int endIdx, List<Chamber> chambers, CaveSettings settings)
    {
        float3 start = chambers[startIdx].position;
        float3 end = chambers[endIdx].position;
        
        TunnelData tunnel = new TunnelData
        {
            startChamber = startIdx,
            endChamber = endIdx,
            pathPoints = new List<float3>(),
            radius = random.NextFloat(settings.tunnelMinRadius, settings.tunnelMaxRadius)
        };
        
        // Generate curved path using Catmull-Rom spline control points
        int segments = 5;
        float3 direction = math.normalize(end - start);
        float distance = math.distance(start, end);
        
        tunnel.pathPoints.Add(start);
        
        // Add intermediate points with some randomness
        for (int i = 1; i < segments - 1; i++)
        {
            float t = (float)i / (segments - 1);
            float3 basePoint = math.lerp(start, end, t);
            
            // Add perpendicular offset for curvature
            float3 perpendicular = math.cross(direction, new float3(0, 1, 0));
            if (math.lengthsq(perpendicular) < 0.001f)
                perpendicular = math.cross(direction, new float3(1, 0, 0));
            perpendicular = math.normalize(perpendicular);
            
            float offset = math.sin(t * math.PI) * distance * settings.tunnelCurvature;
            float3 randomOffset = perpendicular * offset * random.NextFloat(-1f, 1f);
            randomOffset.y *= 0.3f; // Less vertical variation
            
            tunnel.pathPoints.Add(basePoint + randomOffset);
        }
        
        tunnel.pathPoints.Add(end);
        
        return tunnel;
    }
    
    void ConvertToNativeArrays(List<Chamber> chambers, List<TunnelData> tunnels)
    {
        // Dispose old arrays if they exist
        if (chamberCenters.IsCreated)
            chamberCenters.Dispose();
        
        // Create chamber centers array
        chamberCenters = new NativeArray<float3>(chambers.Count, Allocator.Persistent);
        for (int i = 0; i < chambers.Count; i++)
        {
            chamberCenters[i] = chambers[i].position;
        }
        
        // Note: Tunnel splines would need additional conversion for job system
        // This is simplified for the example
    }
    
    void OnDestroy()
    {
        if (chamberCenters.IsCreated)
            chamberCenters.Dispose();
    }
}