using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

[Serializable]
public class NoiseLayer
{
    [Header("Layer Settings")]
    public string layerName = "New Layer";
    public bool enabled = true;
    public NoiseType noiseType = NoiseType.Perlin;
    public BlendMode blendMode = BlendMode.Add;
    
    [Header("Noise Parameters")]
    [Range(0.001f, 0.5f)] public float frequency = 0.02f;
    [Range(0f, 2f)] public float amplitude = 1f;
    [Range(1, 8)] public int octaves = 4;
    [Range(0f, 1f)] public float persistence = 0.5f;
    [Range(1f, 4f)] public float lacunarity = 2f;
    public float3 offset = float3.zero;
    
    [Header("Shape Modifiers")]
    [Range(0f, 1f)] public float verticalSquash = 0.5f;  // For creating horizontal caverns
    [Range(-1f, 1f)] public float densityBias = 0f;     // Overall density adjustment
    [Range(0f, 10f)] public float power = 1f;           // Power curve for noise
    
    [Header("Constraints")]
    public bool useHeightConstraints = false;
    public float minHeight = -50f;
    public float maxHeight = 50f;
    public AnimationCurve heightFalloff = AnimationCurve.Linear(0f, 1f, 1f, 1f);
    
    [Header("Visualization")]
    public bool showPreview = true;
    public Gradient previewGradient = new Gradient();
    
    // Cache for preview texture
    [NonSerialized] public Texture2D previewTexture;
    [NonSerialized] public bool isDirty = true;
    
    public enum NoiseType
    {
        Perlin,
        Simplex,
        Worley,
        Ridged,
        Cavern  // Special type for large cavern generation
    }
    
    public enum BlendMode
    {
        Add,
        Subtract,
        Multiply,
        Min,
        Max,
        Override
    }
    
    public NoiseLayer()
    {
        // Set default gradient
        var colorKeys = new GradientColorKey[2];
        colorKeys[0] = new GradientColorKey(Color.black, 0f);
        colorKeys[1] = new GradientColorKey(Color.white, 1f);
        
        var alphaKeys = new GradientAlphaKey[2];
        alphaKeys[0] = new GradientAlphaKey(1f, 0f);
        alphaKeys[1] = new GradientAlphaKey(1f, 1f);
        
        previewGradient.SetKeys(colorKeys, alphaKeys);
    }
    
    public float Evaluate(float3 worldPos)
    {
        if (!enabled) return 0f;
        
        // Apply vertical squash for horizontal caverns
        float3 samplePos = worldPos;
        samplePos.y *= 1f / math.max(0.01f, verticalSquash);
        
        // Apply offset
        samplePos += offset;
        
        // Generate base noise
        float value = 0f;
        switch (noiseType)
        {
            case NoiseType.Perlin:
                value = SamplePerlin(samplePos);
                break;
            case NoiseType.Simplex:
                value = SampleSimplex(samplePos);
                break;
            case NoiseType.Worley:
                value = SampleWorley(samplePos);
                break;
            case NoiseType.Ridged:
                value = SampleRidged(samplePos);
                break;
            case NoiseType.Cavern:
                value = SampleCavern(samplePos);
                break;
        }
        
        // Apply power curve
        if (power != 1f)
        {
            value = math.sign(value) * math.pow(math.abs(value), power);
        }
        
        // Apply density bias
        value += densityBias;
        
        // Apply height constraints
        if (useHeightConstraints)
        {
            float heightNorm = math.saturate((worldPos.y - minHeight) / (maxHeight - minHeight));
            float heightMultiplier = heightFalloff.Evaluate(heightNorm);
            value *= heightMultiplier;
        }
        
        return value * amplitude;
    }
    
    float SamplePerlin(float3 pos)
    {
        float value = 0f;
        float amp = 1f;
        float freq = frequency;
        float maxValue = 0f;
        
        for (int i = 0; i < octaves; i++)
        {
            value += noise.cnoise(pos * freq) * amp;
            maxValue += amp;
            amp *= persistence;
            freq *= lacunarity;
        }
        
        return value / maxValue;
    }
    
    float SampleSimplex(float3 pos)
    {
        // Using Unity's noise library which provides simplex noise
        float value = 0f;
        float amp = 1f;
        float freq = frequency;
        float maxValue = 0f;
        
        for (int i = 0; i < octaves; i++)
        {
            value += noise.snoise(pos * freq) * amp;
            maxValue += amp;
            amp *= persistence;
            freq *= lacunarity;
        }
        
        return value / maxValue;
    }
    
    float SampleWorley(float3 pos)
    {
        float3 cell = math.floor(pos * frequency);
        float3 localPos = math.frac(pos * frequency);
        
        float minDist = 1f;
        
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    float3 neighbor = cell + new float3(x, y, z);
                    float3 randomPoint = neighbor + HashToFloat3(neighbor) * 0.5f + 0.25f;
                    float dist = math.distance(pos * frequency, randomPoint);
                    minDist = math.min(minDist, dist);
                }
            }
        }
        
        return 1f - minDist;
    }
    
    float SampleRidged(float3 pos)
    {
        float value = 0f;
        float amp = 1f;
        float freq = frequency;
        float maxValue = 0f;
        
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
    
    float SampleCavern(float3 pos)
    {
        // Special cavern generation using multiple noise types
        float worley1 = SampleWorley(pos * 0.5f);
        float worley2 = SampleWorley(pos * 1.5f);
        float perlin = SamplePerlin(pos * 2f);
        
        // Combine for cave-like structures
        float cavern = worley1 * worley2;
        cavern = math.pow(cavern, 2f);
        cavern += perlin * 0.1f;
        
        return cavern;
    }
    
    float3 HashToFloat3(float3 p)
    {
        p = math.frac(p * new float3(0.1031f, 0.1030f, 0.0973f));
        p += math.dot(p, p.yxz + 33.33f);
        return math.frac((p.xxy + p.yxx) * p.zyx);
    }
    
    public void MarkDirty()
    {
        isDirty = true;
    }
}

[Serializable]
public class NoiseLayerStack
{
    public List<NoiseLayer> layers = new List<NoiseLayer>();
    
    public NoiseLayerStack()
    {
        // Add default cavern layer
        var cavernLayer = new NoiseLayer
        {
            layerName = "Main Caverns",
            noiseType = NoiseLayer.NoiseType.Cavern,
            frequency = 0.02f,
            amplitude = 1f,
            verticalSquash = 0.3f
        };
        layers.Add(cavernLayer);
    }
    
    public float EvaluateStack(float3 worldPos)
    {
        float result = 0f;
        
        foreach (var layer in layers)
        {
            if (!layer.enabled) continue;
            
            float layerValue = layer.Evaluate(worldPos);
            
            switch (layer.blendMode)
            {
                case NoiseLayer.BlendMode.Add:
                    result += layerValue;
                    break;
                case NoiseLayer.BlendMode.Subtract:
                    result -= layerValue;
                    break;
                case NoiseLayer.BlendMode.Multiply:
                    result *= layerValue;
                    break;
                case NoiseLayer.BlendMode.Min:
                    result = math.min(result, layerValue);
                    break;
                case NoiseLayer.BlendMode.Max:
                    result = math.max(result, layerValue);
                    break;
                case NoiseLayer.BlendMode.Override:
                    result = layerValue;
                    break;
            }
        }
        
        return result;
    }
    
    public void AddLayer(NoiseLayer layer)
    {
        layers.Add(layer);
    }
    
    public void RemoveLayer(int index)
    {
        if (index >= 0 && index < layers.Count)
            layers.RemoveAt(index);
    }
    
    public void MoveLayer(int fromIndex, int toIndex)
    {
        if (fromIndex >= 0 && fromIndex < layers.Count && 
            toIndex >= 0 && toIndex < layers.Count)
        {
            var layer = layers[fromIndex];
            layers.RemoveAt(fromIndex);
            layers.Insert(toIndex, layer);
        }
    }
}