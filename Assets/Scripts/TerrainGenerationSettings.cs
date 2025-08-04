// TerrainGenerationSettings.cs - Scriptable object for terrain generation parameters
using UnityEngine;

namespace GPUTerrain
{
    [CreateAssetMenu(fileName = "TerrainSettings", menuName = "GPU Terrain/Generation Settings")]
    public class TerrainGenerationSettings : ScriptableObject
    {
        [Header("Cave Generation")]
        public float caveFrequency = 0.02f;
        public float caveAmplitude = 1.0f;
        public int octaves = 4;
        public float lacunarity = 2.0f;
        public float persistence = 0.5f;
        
        [Header("Height Limits")]
        public float minCaveHeight = -50f;
        public float maxCaveHeight = 100f;
        
        [Header("Chamber Settings")]
        public float chamberProbability = 0.1f;
        public float minChamberRadius = 10f;
        public float maxChamberRadius = 30f;
        
        public void ApplyToComputeShader(ComputeShader shader, int kernel)
        {
            shader.SetFloat("CaveFrequency", caveFrequency);
            shader.SetFloat("CaveAmplitude", caveAmplitude);
            shader.SetInt("Octaves", octaves);
            shader.SetFloat("Lacunarity", lacunarity);
            shader.SetFloat("Persistence", persistence);
            shader.SetFloat("MinCaveHeight", minCaveHeight);
            shader.SetFloat("MaxCaveHeight", maxCaveHeight);
        }
        
        // Create default settings
        public static TerrainGenerationSettings CreateDefault()
        {
            var settings = CreateInstance<TerrainGenerationSettings>();
            settings.caveFrequency = 0.02f;
            settings.caveAmplitude = 1.0f;
            settings.octaves = 4;
            settings.lacunarity = 2.0f;
            settings.persistence = 0.5f;
            settings.minCaveHeight = -50f;
            settings.maxCaveHeight = 100f;
            settings.chamberProbability = 0.1f;
            settings.minChamberRadius = 10f;
            settings.maxChamberRadius = 30f;
            return settings;
        }
    }
}