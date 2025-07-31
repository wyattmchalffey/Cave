using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

[BurstCompile(CompileSynchronously = true)]
public struct CaveGenerationJob : IJobParallelFor
{
    // Input
    [ReadOnly] public float3 chunkWorldPosition;
    [ReadOnly] public int chunkSize;
    [ReadOnly] public float voxelSize;
    [ReadOnly] public CaveSettings settings;
    [ReadOnly] public NativeArray<float3> chamberCenters;
    
    // Flattened tunnel data to avoid nested containers
    [ReadOnly] public NativeArray<float3> allTunnelPoints; // All tunnel control points flattened
    [ReadOnly] public NativeArray<int> tunnelPointCounts; // Number of points per tunnel
    [ReadOnly] public NativeArray<int> tunnelStartIndices; // Start index in allTunnelPoints for each tunnel
    [ReadOnly] public NativeArray<float> tunnelRadii; // Radius for each tunnel
    
    // Output
    [NativeDisableParallelForRestriction]
    public NativeArray<float> voxelData;
    
    public void Execute(int index)
    {
        // Convert linear index to 3D coordinates
        int sizeWithBoundary = chunkSize + 1;
        int x = index % sizeWithBoundary;
        int y = (index / sizeWithBoundary) % sizeWithBoundary;
        int z = index / (sizeWithBoundary * sizeWithBoundary);
        
        float3 localPos = new float3(x, y, z) * voxelSize;
        float3 worldPos = chunkWorldPosition + localPos;
        
        float density = GenerateImprovedCaveDensity(worldPos);
        voxelData[index] = density;
    }
    
    float GenerateImprovedCaveDensity(float3 worldPos)
    {
        // Start with solid rock
        float density = 1.0f;
        
        // Skip if outside cave height range
        if (worldPos.y < settings.minCaveHeight || worldPos.y > settings.maxCaveHeight)
            return density;
        
        // Surface transition
        if (worldPos.y > settings.surfaceTransitionHeight)
        {
            float transition = (worldPos.y - settings.surfaceTransitionHeight) / 10f;
            density = math.lerp(density, 0f, math.saturate(transition));
        }
        
        // 1. Chamber generation
        float chamberInfluence = EvaluateChambers(worldPos);
        
        // 2. Tunnel network
        float tunnelInfluence = EvaluateTunnels(worldPos);
        
        // 3. Geological stratification
        float stratification = EvaluateStratification(worldPos);
        
        // 4. Detail noise for natural variation
        float detailNoise = SampleDetailNoise(worldPos);
        
        // Combine influences
        float caveDensity = math.min(chamberInfluence, tunnelInfluence);
        caveDensity += stratification + detailNoise;
        
        // Apply erosion
        caveDensity = ApplyErosion(worldPos, caveDensity);
        
        return caveDensity;
    }
    
    float EvaluateChambers(float3 worldPos)
    {
        if (chamberCenters.Length == 0) return 1f;
        
        float minDistance = float.MaxValue;
        float3 nearestChamber = float3.zero;
        
        // Find nearest chamber
        for (int i = 0; i < chamberCenters.Length; i++)
        {
            float3 chamberPos = chamberCenters[i];
            
            // Apply vertical scaling to make chambers more horizontal
            float3 scaledDiff = worldPos - chamberPos;
            scaledDiff.y *= 1f / settings.chamberVerticalScale;
            
            float distance = math.length(scaledDiff);
            if (distance < minDistance)
            {
                minDistance = distance;
                nearestChamber = chamberPos;
            }
        }
        
        // Calculate chamber influence with flat floors
        float chamberRadius = math.lerp(settings.chamberMinRadius, settings.chamberMaxRadius, 
            Hash(nearestChamber) * 0.5f + 0.5f);
        
        // Create flat floor effect
        float heightInChamber = worldPos.y - nearestChamber.y;
        float floorDistance = 0f;
        
        if (heightInChamber < 0)
        {
            // Below chamber center - create flat floor
            float floorY = nearestChamber.y - chamberRadius * 0.3f;
            floorDistance = math.abs(worldPos.y - floorY) * settings.chamberFloorFlatness;
        }
        
        // Chamber shape function
        float chamberDensity = (minDistance + floorDistance) / chamberRadius - 1f;
        
        // Smooth edges
        return math.smoothstep(-0.1f, 0.1f, chamberDensity);
    }
    
    float EvaluateTunnels(float3 worldPos)
    {
        if (tunnelPointCounts.Length == 0) return 1f;
        
        float minDistance = float.MaxValue;
        
        // Find distance to nearest tunnel
        for (int tunnelIdx = 0; tunnelIdx < tunnelPointCounts.Length; tunnelIdx++)
        {
            float distance = DistanceToTunnel(worldPos, tunnelIdx);
            minDistance = math.min(minDistance, distance);
        }
        
        // Tunnel radius with variation
        float tunnelRadius = math.lerp(settings.tunnelMinRadius, settings.tunnelMaxRadius,
            SampleNoise3D(worldPos * 0.1f, 1, 0.5f, 2f, settings.noiseOffset));
        
        // Tunnel shape function
        float tunnelDensity = minDistance / tunnelRadius - 1f;
        
        return math.smoothstep(-0.1f, 0.1f, tunnelDensity);
    }
    
    float DistanceToTunnel(float3 point, int tunnelIndex)
    {
        int startIdx = tunnelStartIndices[tunnelIndex];
        int pointCount = tunnelPointCounts[tunnelIndex];
        float minDist = float.MaxValue;
        
        // Check each segment of the tunnel
        for (int i = 0; i < pointCount - 1; i++)
        {
            float3 p0 = allTunnelPoints[startIdx + i];
            float3 p1 = allTunnelPoints[startIdx + i + 1];
            
            // Distance to line segment
            float3 line = p1 - p0;
            float t = math.clamp(math.dot(point - p0, line) / math.dot(line, line), 0f, 1f);
            float3 closest = p0 + t * line;
            
            float dist = math.distance(point, closest);
            minDist = math.min(minDist, dist);
        }
        
        return minDist;
    }
    
    float EvaluateStratification(float3 worldPos)
    {
        // Geological layer influence
        float layerPattern = math.sin(worldPos.y * settings.stratificationFrequency * math.PI);
        float stratification = layerPattern * settings.stratificationStrength;
        
        // Add some horizontal variation
        float horizontalVariation = SampleNoise3D(
            new float3(worldPos.x, 0, worldPos.z) * 0.02f,
            2, 0.5f, 2f, settings.noiseOffset + new float3(100, 0, 100)
        ) * 0.1f;
        
        return stratification + horizontalVariation;
    }
    
    float ApplyErosion(float3 worldPos, float baseDensity)
    {
        // Simulate water erosion effects
        float erosionNoise = SampleNoise3D(
            worldPos * 0.05f,
            3, 0.6f, 2f,
            settings.noiseOffset + new float3(200, 200, 200)
        );
        
        // Erosion is stronger in softer rock and lower areas
        float heightFactor = 1f - math.saturate((worldPos.y - settings.minCaveHeight) / 50f);
        float erosion = erosionNoise * settings.erosionStrength * heightFactor * (1f - settings.rockHardness);
        
        return baseDensity - erosion;
    }
    
    float SampleDetailNoise(float3 worldPos)
    {
        // High-frequency detail noise
        return SampleNoise3D(worldPos * 0.1f, 2, 0.5f, 2f, settings.noiseOffset + new float3(300, 300, 300)) * 0.05f;
    }
    
    float SampleNoise3D(float3 pos, int octaves, float persistence, float lacunarity, float3 offset)
    {
        pos += offset;
        float amplitude = 1f;
        float frequency = 1f;
        float noiseValue = 0f;
        float maxValue = 0f;
        
        for (int i = 0; i < octaves; i++)
        {
            float3 samplePos = pos * frequency;
            noiseValue += Perlin3D(samplePos) * amplitude;
            maxValue += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }
        
        return (noiseValue / maxValue) * 2f - 1f; // Return -1 to 1
    }
    
    float Perlin3D(float3 pos)
    {
        float xy = noise.cnoise(new float2(pos.x, pos.y));
        float xz = noise.cnoise(new float2(pos.x, pos.z));
        float yz = noise.cnoise(new float2(pos.y, pos.z));
        float yx = noise.cnoise(new float2(pos.y, pos.x));
        float zx = noise.cnoise(new float2(pos.z, pos.x));
        float zy = noise.cnoise(new float2(pos.z, pos.y));
        
        return (xy + xz + yz + yx + zx + zy) / 6f;
    }
    
    float Hash(float3 p)
    {
        p = math.frac(p * 0.3183099f + 0.1f);
        p *= 17.0f;
        return math.frac(p.x * p.y * p.z * (p.x + p.y + p.z));
    }
}