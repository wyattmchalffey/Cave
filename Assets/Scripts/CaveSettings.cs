using System;
using Unity.Mathematics;
using UnityEngine;

[Serializable]
public struct CaveSettings
{
    [Header("Chamber Settings")]
    public float chamberFrequency;      // 0.01-0.03 for large chambers
    public float chamberMinRadius;      // Minimum chamber size
    public float chamberMaxRadius;      // Maximum chamber size
    public float chamberFloorFlatness;  // 0-1, how flat chamber floors are
    public float chamberVerticalScale;  // Vertical stretch of chambers
    
    [Header("Tunnel Settings")]
    public float tunnelMinRadius;       // Minimum tunnel radius
    public float tunnelMaxRadius;       // Maximum tunnel radius
    public float tunnelCurvature;       // How much tunnels curve
    public float tunnelFrequency;       // Frequency of tunnel network
    public int tunnelConnectionsPerChamber; // How many tunnels per chamber
    
    [Header("Geological Settings")]
    public float stratificationStrength; // Horizontal layer influence
    public float stratificationFrequency; // How often layers occur
    public float erosionStrength;       // Water erosion simulation
    public float rockHardness;          // Affects erosion patterns
    
    [Header("Noise Settings")]
    public int seed;
    public float3 noiseOffset;
    public int worleyOctaves;
    public float worleyPersistence;
    
    [Header("Generation Constraints")]
    public float minCaveHeight;         // Minimum Y for caves
    public float maxCaveHeight;         // Maximum Y for caves
    public float surfaceTransitionHeight; // Where caves blend to surface
    
    public static CaveSettings Default()
    {
        return new CaveSettings
        {
            // Chamber settings
            chamberFrequency = 0.02f,
            chamberMinRadius = 8f,
            chamberMaxRadius = 20f,
            chamberFloorFlatness = 0.7f,
            chamberVerticalScale = 0.6f,
            
            // Tunnel settings
            tunnelMinRadius = 2f,
            tunnelMaxRadius = 4f,
            tunnelCurvature = 0.3f,
            tunnelFrequency = 0.05f,
            tunnelConnectionsPerChamber = 3,
            
            // Geological settings
            stratificationStrength = 0.15f,
            stratificationFrequency = 0.1f,
            erosionStrength = 0.3f,
            rockHardness = 0.5f,
            
            // Noise settings
            seed = 42,
            noiseOffset = new float3(0, 0, 0),
            worleyOctaves = 2,
            worleyPersistence = 0.5f,
            
            // Generation constraints
            minCaveHeight = -50f,
            maxCaveHeight = 50f,
            surfaceTransitionHeight = 40f
        };
    }
}