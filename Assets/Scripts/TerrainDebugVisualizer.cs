// TerrainDebugVisualizer.cs - Debug visualization for terrain generation
using UnityEngine;
using Unity.Mathematics;
using System.Collections.Generic;

namespace GPUTerrain
{
    [RequireComponent(typeof(TerrainWorldManager))]
    public class TerrainDebugVisualizer : MonoBehaviour
    {
        [Header("Debug Settings")]
        public bool showChunkBounds = true;
        public bool showActiveChunks = true;
        public bool showGeneratingChunks = true;
        public bool showDebugInfo = true;
        
        [Header("Colors")]
        public Color chunkBoundsColor = Color.green;
        public Color activeChunkColor = Color.blue;
        public Color generatingChunkColor = Color.yellow;
        
        private TerrainWorldManager terrainManager;
        private Dictionary<int3, float> chunkGenerationTimes = new Dictionary<int3, float>();
        
        void Start()
        {
            terrainManager = GetComponent<TerrainWorldManager>();
        }
        
        void OnDrawGizmos()
        {
            if (!Application.isPlaying || terrainManager == null) return;
            
            DrawChunkBounds();
            DrawActiveChunks();
        }
        
        void DrawChunkBounds()
        {
            if (!showChunkBounds) return;
            
            Gizmos.color = chunkBoundsColor;
            
            // Draw world bounds
            float chunkWorldSize = terrainManager.VoxelSize * TerrainWorldManager.CHUNK_SIZE;
            Vector3 worldSize = new Vector3(
                terrainManager.WorldSizeChunks * chunkWorldSize,
                terrainManager.WorldHeightChunks * chunkWorldSize,
                terrainManager.WorldSizeChunks * chunkWorldSize
            );
            
            Gizmos.DrawWireCube(worldSize * 0.5f, worldSize);
        }
        
        void DrawActiveChunks()
        {
            if (!showActiveChunks) return;
            
            float chunkWorldSize = terrainManager.VoxelSize * TerrainWorldManager.CHUNK_SIZE;
            
            foreach (var kvp in terrainManager.GetChunkStates())
            {
                var state = kvp.Value;
                Vector3 chunkCenter = new Vector3(
                    state.coordinate.x * chunkWorldSize + chunkWorldSize * 0.5f,
                    state.coordinate.y * chunkWorldSize + chunkWorldSize * 0.5f,
                    state.coordinate.z * chunkWorldSize + chunkWorldSize * 0.5f
                );
                
                if (state.isGenerated)
                {
                    Gizmos.color = activeChunkColor;
                    Gizmos.DrawWireCube(chunkCenter, Vector3.one * chunkWorldSize * 0.95f);
                }
                else if (showGeneratingChunks)
                {
                    Gizmos.color = generatingChunkColor;
                    Gizmos.DrawWireCube(chunkCenter, Vector3.one * chunkWorldSize * 0.9f);
                }
            }
        }
        
        void OnGUI()
        {
            if (!showDebugInfo || terrainManager == null) return;
            
            int yOffset = 150; // Start below player controls
            int lineHeight = 20;
            
            GUI.Label(new Rect(10, yOffset, 400, lineHeight), "=== Terrain Debug Info ===");
            yOffset += lineHeight;
            
            // World info
            GUI.Label(new Rect(10, yOffset, 400, lineHeight), 
                $"World Size: {terrainManager.WorldSizeChunks}x{terrainManager.WorldHeightChunks}x{terrainManager.WorldSizeChunks} chunks");
            yOffset += lineHeight;
            
            GUI.Label(new Rect(10, yOffset, 400, lineHeight), 
                $"Voxel Size: {terrainManager.VoxelSize}m");
            yOffset += lineHeight;
            
            // Chunk statistics
            var chunkStates = terrainManager.GetChunkStates();
            int totalChunks = chunkStates.Count;
            int generatedChunks = 0;
            int chunksWithMesh = 0;
            
            foreach (var state in chunkStates.Values)
            {
                if (state.isGenerated) generatedChunks++;
                if (state.hasMesh) chunksWithMesh++;
            }
            
            GUI.Label(new Rect(10, yOffset, 400, lineHeight), 
                $"Chunks: {generatedChunks}/{totalChunks} generated, {chunksWithMesh} with mesh");
            yOffset += lineHeight;
            
            // Generation queue
            int queueSize = terrainManager.GetUpdateQueueSize();
            GUI.Label(new Rect(10, yOffset, 400, lineHeight), 
                $"Generation Queue: {queueSize} chunks pending");
            yOffset += lineHeight;
            
            // Memory usage estimate
            float memoryMB = EstimateMemoryUsage();
            GUI.Label(new Rect(10, yOffset, 400, lineHeight), 
                $"Estimated GPU Memory: {memoryMB:F1} MB");
            yOffset += lineHeight;
            
            // Instructions
            yOffset += lineHeight;
            GUI.Label(new Rect(10, yOffset, 400, lineHeight), "=== Debug Controls ===");
            yOffset += lineHeight;
            GUI.Label(new Rect(10, yOffset, 400, lineHeight), "G - Toggle chunk bounds");
            yOffset += lineHeight;
            GUI.Label(new Rect(10, yOffset, 400, lineHeight), "H - Toggle active chunks");
            yOffset += lineHeight;
            GUI.Label(new Rect(10, yOffset, 400, lineHeight), "J - Force regenerate world");
        }
        
        void Update()
        {
            HandleDebugInput();
        }
        
        void HandleDebugInput()
        {
            if (Input.GetKeyDown(KeyCode.G))
                showChunkBounds = !showChunkBounds;
                
            if (Input.GetKeyDown(KeyCode.H))
                showActiveChunks = !showActiveChunks;
                
            if (Input.GetKeyDown(KeyCode.J))
            {
                Debug.Log("Forcing world regeneration...");
                terrainManager.RegenerateWorld();
            }
        }
        
        float EstimateMemoryUsage()
        {
            if (terrainManager == null) return 0;
            
            // World texture size
            int texSize = terrainManager.WorldSizeChunks * TerrainWorldManager.CHUNK_SIZE;
            int texHeight = terrainManager.WorldHeightChunks * TerrainWorldManager.CHUNK_SIZE;
            float worldTextureMB = (texSize * texHeight * texSize * 16) / (1024f * 1024f); // 16 bytes per voxel
            
            // Vertex pool estimate
            float vertexPoolMB = (TerrainWorldManager.MAX_VERTICES_PER_CHUNK * TerrainWorldManager.MAX_CHUNKS * 32) / (1024f * 1024f);
            
            return worldTextureMB + vertexPoolMB;
        }
    }
}