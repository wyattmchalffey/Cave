using System;
using Unity.Mathematics;
using UnityEngine;

[Serializable]
public struct CaveSettings
{
    [Header("Generation Constraints")]
    public float minCaveHeight;         // Minimum Y for caves
    public float maxCaveHeight;         // Maximum Y for caves
    public float surfaceTransitionHeight; // Where caves blend to surface
    
    [Header("System Settings")]
    public int seed;
    public float3 noiseOffset;
    
    public static CaveSettings Default()
    {
        return new CaveSettings
        {
            // Generation constraints
            minCaveHeight = -50f,
            maxCaveHeight = 50f,
            surfaceTransitionHeight = 40f,
            
            // System settings
            seed = 42,
            noiseOffset = new float3(0, 0, 0)
        };
    }
}