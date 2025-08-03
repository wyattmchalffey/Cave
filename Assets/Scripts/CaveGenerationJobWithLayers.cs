using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

// Job-friendly version of noise layer stack
public struct JobNoiseLayerStack : System.IDisposable
{
    public int layerCount;
    
    // Layer properties as separate arrays
    public NativeArray<bool> enabled;
    public NativeArray<int> noiseTypes;
    public NativeArray<int> blendModes;
    public NativeArray<float> frequencies;
    public NativeArray<float> amplitudes;
    public NativeArray<int> octaves;
    public NativeArray<float> persistences;
    public NativeArray<float> lacunarities;
    public NativeArray<float3> offsets;
    public NativeArray<float> verticalSquashes;
    public NativeArray<float> densityBiases;
    public NativeArray<float> powers;
    
    public void Dispose()
    {
        if (enabled.IsCreated) enabled.Dispose();
        if (noiseTypes.IsCreated) noiseTypes.Dispose();
        if (blendModes.IsCreated) blendModes.Dispose();
        if (frequencies.IsCreated) frequencies.Dispose();
        if (amplitudes.IsCreated) amplitudes.Dispose();
        if (octaves.IsCreated) octaves.Dispose();
        if (persistences.IsCreated) persistences.Dispose();
        if (lacunarities.IsCreated) lacunarities.Dispose();
        if (offsets.IsCreated) offsets.Dispose();
        if (verticalSquashes.IsCreated) verticalSquashes.Dispose();
        if (densityBiases.IsCreated) densityBiases.Dispose();
        if (powers.IsCreated) powers.Dispose();
    }
}

[BurstCompile]
public struct CaveGenerationJobWithLayers : IJob
{
    // Input
    [ReadOnly] public int3 chunkCoord;
    [ReadOnly] public int chunkSize;
    [ReadOnly] public float voxelSize;
    [ReadOnly] public CaveSettings settings;
    [ReadOnly] public JobNoiseLayerStack noiseLayerStack;
    
    // Output
    public NativeArray<float> densityField;
    
    public void Execute()
    {
        int size = chunkSize + 1;
        float3 chunkOrigin = new float3(chunkCoord) * chunkSize * voxelSize;
        
        for (int x = 0; x < size; x++)
        {
            for (int y = 0; y < size; y++)
            {
                for (int z = 0; z < size; z++)
                {
                    float3 localPos = new float3(x, y, z) * voxelSize;
                    float3 worldPos = chunkOrigin + localPos;
                    
                    // Evaluate noise layers
                    float density = EvaluateNoiseStack(worldPos);
                    
                    // Store in flat array
                    int index = x + y * size + z * size * size;
                    densityField[index] = density;
                }
            }
        }
    }
    
    float EvaluateNoiseStack(float3 worldPos)
    {
        float result = 0f;
        
        for (int i = 0; i < noiseLayerStack.layerCount; i++)
        {
            if (!noiseLayerStack.enabled[i]) continue;
            
            float layerValue = EvaluateLayer(i, worldPos);
            
            // Apply blend mode
            int blendMode = noiseLayerStack.blendModes[i];
            switch (blendMode)
            {
                case 0: // Add
                    result += layerValue;
                    break;
                case 1: // Subtract
                    result -= layerValue;
                    break;
                case 2: // Multiply
                    result *= layerValue;
                    break;
                case 3: // Min
                    result = math.min(result, layerValue);
                    break;
                case 4: // Max
                    result = math.max(result, layerValue);
                    break;
                case 5: // Override
                    result = layerValue;
                    break;
            }
        }
        
        return result;
    }
    
    float EvaluateLayer(int layerIndex, float3 worldPos)
    {
        // Apply vertical squash for horizontal caverns
        float3 samplePos = worldPos;
        samplePos.y *= 1f / math.max(0.01f, noiseLayerStack.verticalSquashes[layerIndex]);
        
        // Apply offset
        samplePos += noiseLayerStack.offsets[layerIndex];
        
        // Generate base noise
        float value = 0f;
        int noiseType = noiseLayerStack.noiseTypes[layerIndex];
        
        switch (noiseType)
        {
            case 0: // Perlin
                value = SamplePerlin(layerIndex, samplePos);
                break;
            case 1: // Simplex
                value = SampleSimplex(layerIndex, samplePos);
                break;
            case 2: // Worley
                value = SampleWorley(layerIndex, samplePos);
                break;
            case 3: // Ridged
                value = SampleRidged(layerIndex, samplePos);
                break;
            case 4: // Cavern
                value = SampleCavern(layerIndex, samplePos);
                break;
        }
        
        // Apply power curve
        float power = noiseLayerStack.powers[layerIndex];
        if (power != 1f)
        {
            value = math.sign(value) * math.pow(math.abs(value), power);
        }
        
        // Apply density bias
        value += noiseLayerStack.densityBiases[layerIndex];
        
        return value * noiseLayerStack.amplitudes[layerIndex];
    }
    
    float SamplePerlin(int layerIndex, float3 pos)
    {
        float value = 0f;
        float amp = 1f;
        float freq = noiseLayerStack.frequencies[layerIndex];
        float maxValue = 0f;
        int octaves = noiseLayerStack.octaves[layerIndex];
        float persistence = noiseLayerStack.persistences[layerIndex];
        float lacunarity = noiseLayerStack.lacunarities[layerIndex];
        
        for (int i = 0; i < octaves; i++)
        {
            value += noise.cnoise(pos * freq) * amp;
            maxValue += amp;
            amp *= persistence;
            freq *= lacunarity;
        }
        
        return value / maxValue;
    }
    
    float SampleSimplex(int layerIndex, float3 pos)
    {
        float value = 0f;
        float amp = 1f;
        float freq = noiseLayerStack.frequencies[layerIndex];
        float maxValue = 0f;
        int octaves = noiseLayerStack.octaves[layerIndex];
        float persistence = noiseLayerStack.persistences[layerIndex];
        float lacunarity = noiseLayerStack.lacunarities[layerIndex];
        
        for (int i = 0; i < octaves; i++)
        {
            value += noise.snoise(pos * freq) * amp;
            maxValue += amp;
            amp *= persistence;
            freq *= lacunarity;
        }
        
        return value / maxValue;
    }
    
    float SampleWorley(int layerIndex, float3 pos)
    {
        float freq = noiseLayerStack.frequencies[layerIndex];
        float3 cell = math.floor(pos * freq);
        float3 localPos = math.frac(pos * freq);
        
        float minDist = 1f;
        
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    float3 neighbor = cell + new float3(x, y, z);
                    float3 randomPoint = neighbor + HashToFloat3(neighbor) * 0.5f + 0.25f;
                    float dist = math.distance(pos * freq, randomPoint);
                    minDist = math.min(minDist, dist);
                }
            }
        }
        
        return 1f - minDist;
    }
    
    float SampleRidged(int layerIndex, float3 pos)
    {
        float value = 0f;
        float amp = 1f;
        float freq = noiseLayerStack.frequencies[layerIndex];
        float maxValue = 0f;
        int octaves = noiseLayerStack.octaves[layerIndex];
        float persistence = noiseLayerStack.persistences[layerIndex];
        float lacunarity = noiseLayerStack.lacunarities[layerIndex];
        
        for (int i = 0; i < octaves; i++)
        {
            float sample = 1f - math.abs(noise.cnoise(pos * freq));
            sample = sample * sample;
            value += sample * amp;
            maxValue += amp;
            amp *= persistence;
            freq *= lacunarity;
        }
        
        return value / maxValue;
    }
    
    float SampleCavern(int layerIndex, float3 pos)
    {
        // Special cavern generation using multiple noise types
        float freq = noiseLayerStack.frequencies[layerIndex];
        
        // Use worley noise for base cavern shape
        float worley1 = SampleWorley(layerIndex, pos * 0.5f);
        float worley2 = SampleWorley(layerIndex, pos * 1.5f);
        
        // Add some perlin for variation
        float perlin = noise.cnoise(pos * freq * 2f) * 0.5f + 0.5f;
        
        // Combine for cave-like structures
        float cavern = worley1 * worley2;
        cavern = math.pow(cavern, 2f);
        cavern += perlin * 0.1f;
        
        // Remap to -1 to 1
        return cavern * 2f - 1f;
    }
    
    float3 HashToFloat3(float3 p)
    {
        p = math.frac(p * new float3(0.1031f, 0.1030f, 0.0973f));
        p += math.dot(p, p.yxz + 33.33f);
        return math.frac((p.xxy + p.yxx) * p.zyx);
    }
    
    float Hash(float3 p)
    {
        p = math.frac(p * 0.3183099f + 0.1f);
        p *= 17.0f;
        return math.frac(p.x * p.y * p.z * (p.x + p.y + p.z));
    }
}