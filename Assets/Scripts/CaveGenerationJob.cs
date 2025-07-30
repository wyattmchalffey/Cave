using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

[BurstCompile(CompileSynchronously = true)]
public struct CaveGenerationJob : IJob
{
    // Input
    [ReadOnly] public float3 chunkWorldPosition;
    [ReadOnly] public int chunkSize;
    [ReadOnly] public CaveSettings settings;
    
    // Output
    public NativeArray<float> voxelData;
    
    public void Execute()
    {
        int index = 0;
        
        for (int x = 0; x < chunkSize; x++)
        {
            for (int y = 0; y < chunkSize; y++)
            {
                for (int z = 0; z < chunkSize; z++)
                {
                    float3 worldPos = chunkWorldPosition + new float3(x, y, z);
                    float density = GenerateCaveDensity(worldPos);
                    voxelData[index++] = density;
                }
            }
        }
    }
    
    float GenerateCaveDensity(float3 worldPos)
    {
        // Base cave noise - larger scale for bigger caves
        float caveNoise = SampleNoise3D(
            worldPos * settings.baseFrequency,
            settings.octaves,
            settings.persistence,
            settings.lacunarity,
            settings.noiseOffset
        );
        
        // Start with solid
        float density = 1f;
        
        // Carve out base caves
        if (caveNoise > settings.caveThreshold)
        {
            density = 0f;
        }
        
        // Add large caverns using multiple noise layers
        if (settings.enableCaverns)
        {
            // Large scale caverns
            float cavernNoise1 = SampleNoise3D(
                worldPos * settings.cavernScale,
                2,
                0.5f,
                2f,
                settings.noiseOffset + new float3(100, 100, 100)
            );
            
            // Medium scale variation
            float cavernNoise2 = SampleNoise3D(
                worldPos * (settings.cavernScale * 2f),
                2,
                0.5f,
                2f,
                settings.noiseOffset + new float3(200, 200, 200)
            );
            
            // Combine for more interesting shapes
            float cavernValue = cavernNoise1 * cavernNoise2;
            
            // Create large caverns where combined noise is high
            if (cavernValue > settings.cavernThreshold * settings.cavernThreshold)
            {
                density = 0f;
            }
        }
        
        // Add tunnels that connect caves
        if (settings.enableTunnels && density > 0.5f)
        {
            // Create 3D worm tunnels
            float tunnel1 = SampleNoise3D(
                worldPos * settings.tunnelFrequency,
                2,
                0.4f,
                2f,
                settings.tunnelOffset
            );
            
            float tunnel2 = SampleNoise3D(
                worldPos * settings.tunnelFrequency * 1.5f,
                2,
                0.4f,
                2f,
                settings.tunnelOffset + new float3(500, 500, 500)
            );
            
            // Worm-like tunnels using ridge noise
            float ridgeNoise1 = math.abs(tunnel1 - 0.5f);
            float ridgeNoise2 = math.abs(tunnel2 - 0.5f);
            
            // Wider tunnels for better connectivity
            if (ridgeNoise1 < settings.tunnelWidth || ridgeNoise2 < settings.tunnelWidth)
            {
                density = 0f;
            }
        }
        
        // Height-based modifications
        if (worldPos.y > 40f)
        {
            density = 1f; // Force solid high up
        }
        
        return density;
    }
    
    // Optimized 3D noise function
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
        
        return noiseValue / maxValue;
    }
    
    // Fast 3D Perlin noise approximation
    float Perlin3D(float3 pos)
    {
        // Using Unity.Mathematics noise functions
        float xy = noise.cnoise(new float2(pos.x, pos.y));
        float xz = noise.cnoise(new float2(pos.x, pos.z));
        float yz = noise.cnoise(new float2(pos.y, pos.z));
        
        // Combine for 3D effect
        return (xy + xz + yz + 1.5f) / 3f; // Normalized to 0-1
    }
}

[System.Serializable]
public struct CaveSettings
{
    [Header("Base Cave Settings")]
    public float baseFrequency;
    public int octaves;
    public float persistence;
    public float lacunarity;
    public float caveThreshold;
    public float3 noiseOffset;
    
    [Header("Tunnel Settings")]
    public bool enableTunnels;
    public float tunnelFrequency;
    public float tunnelWidth;
    public float3 tunnelOffset;
    
    [Header("Cavern Settings")]
    public bool enableCaverns;
    public float cavernThreshold;
    public float cavernScale;
    
    public static CaveSettings Default()
    {
        return new CaveSettings
        {
            baseFrequency = 0.05f,
            octaves = 3,
            persistence = 0.5f,
            lacunarity = 2f,
            caveThreshold = 0.5f,
            noiseOffset = new float3(0, 0, 0),
            
            enableTunnels = true,
            tunnelFrequency = 0.03f,
            tunnelWidth = 0.15f,
            tunnelOffset = new float3(1000, 1000, 1000),
            
            enableCaverns = true,
            cavernThreshold = 0.7f,
            cavernScale = 0.01f
        };
    }
}