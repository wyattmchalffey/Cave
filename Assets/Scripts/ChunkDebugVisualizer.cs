using UnityEngine;
using Unity.Mathematics;
using GPUTerrain;

public class ChunkDebugVisualizer : MonoBehaviour
{
    private TerrainWorldManager worldManager;
    private ChunkMeshBuilder meshBuilder;
    
    [Header("Debug Settings")]
    [SerializeField] private bool showExpectedChunkPositions = true;
    [SerializeField] private bool showActualMeshPositions = true;
    [SerializeField] private bool logChunkInfo = true;
    [SerializeField] private Color expectedColor = Color.green;
    [SerializeField] private Color actualColor = Color.red;
    
    void Start()
    {
        worldManager = GetComponent<TerrainWorldManager>();
        meshBuilder = GetComponent<ChunkMeshBuilder>();
        
        if (logChunkInfo)
        {
            InvokeRepeating(nameof(LogChunkPositions), 2f, 5f);
        }
    }
    
    void LogChunkPositions()
    {
        if (meshBuilder == null || worldManager == null) return;
        
        Debug.Log("=== Chunk Position Debug ===");
        Debug.Log($"Voxel Size: {worldManager.VoxelSize}");
        Debug.Log($"Chunk Size: {TerrainWorldManager.CHUNK_SIZE}");
        Debug.Log($"Chunk World Size: {worldManager.VoxelSize * TerrainWorldManager.CHUNK_SIZE}");
        Debug.Log($"World Size in Chunks: {worldManager.WorldSizeChunks} x {worldManager.WorldHeightChunks} x {worldManager.WorldSizeChunks}");
        
        // Check what chunks are generated vs what chunks have meshes
        var chunkStates = worldManager.GetChunkStates();
        int generatedCount = 0;
        int meshCount = 0;
        
        foreach (var kvp in chunkStates)
        {
            if (kvp.Value.isGenerated) generatedCount++;
            if (kvp.Value.hasMesh) meshCount++;
        }
        
        Debug.Log($"Generated chunks: {generatedCount}, Chunks with mesh: {meshCount}, Active meshes: {meshBuilder.activeChunks.Count}");
        
        // List all chunks that should be visible but aren't
        Vector3 playerPos = Camera.main.transform.position;
        int3 playerChunk = worldManager.WorldToChunkCoord(playerPos);
        Debug.Log($"Player at world {playerPos}, chunk {playerChunk}");
        
        // Check chunks around player
        for (int x = playerChunk.x - 2; x <= playerChunk.x + 2; x++)
        {
            for (int z = playerChunk.z - 2; z <= playerChunk.z + 2; z++)
            {
                int3 coord = new int3(x, playerChunk.y, z);
                
                if (worldManager.IsChunkInBounds(coord))
                {
                    if (chunkStates.TryGetValue(coord, out var state))
                    {
                        if (!state.isGenerated)
                        {
                            Debug.LogWarning($"Chunk {coord} in bounds but NOT generated!");
                        }
                        else if (!state.hasMesh)
                        {
                            Debug.LogWarning($"Chunk {coord} generated but NO mesh!");
                        }
                        else if (!meshBuilder.activeChunks.ContainsKey(coord))
                        {
                            Debug.LogWarning($"Chunk {coord} has mesh flag but NOT in active chunks!");
                        }
                    }
                    else
                    {
                        Debug.LogError($"Chunk {coord} in bounds but NOT in chunkStates!");
                    }
                }
                else
                {
                    Debug.Log($"Chunk {coord} is out of bounds (world size: {worldManager.WorldSizeChunks})");
                }
            }
        }
        
        // Log actual mesh positions
        foreach (var kvp in meshBuilder.activeChunks)
        {
            int3 coord = kvp.Key;
            GameObject chunk = kvp.Value;
            
            // Expected position
            float3 expectedPos = worldManager.ChunkToWorldPos(coord);
            Vector3 expectedPosV3 = new Vector3(expectedPos.x, expectedPos.y, expectedPos.z);
            
            // Actual position
            Vector3 actualPos = chunk.transform.position;
            
            // Check for mismatch
            float distance = Vector3.Distance(expectedPosV3, actualPos);
            
            if (distance > 0.01f)
            {
                Debug.LogWarning($"POSITION MISMATCH for chunk {coord}:");
                Debug.LogWarning($"  Expected: {expectedPos}");
                Debug.LogWarning($"  Actual: {actualPos}");
                Debug.LogWarning($"  Distance: {distance}");
            }
            else
            {
                Debug.Log($"Chunk {coord} OK at position {actualPos}");
            }
        }
    }
    
    void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;
        if (worldManager == null || meshBuilder == null) return;
        
        float chunkWorldSize = worldManager.VoxelSize * TerrainWorldManager.CHUNK_SIZE;
        
        // Draw expected positions
        if (showExpectedChunkPositions)
        {
            Gizmos.color = expectedColor;
            
            var chunkStates = worldManager.GetChunkStates();
            foreach (var kvp in chunkStates)
            {
                if (kvp.Value.isGenerated)
                {
                    float3 expectedPos = worldManager.ChunkToWorldPos(kvp.Key);
                    Vector3 expectedPosV3 = new Vector3(expectedPos.x, expectedPos.y, expectedPos.z);
                    Vector3 center = expectedPosV3 + Vector3.one * chunkWorldSize * 0.5f;
                    
                    // Draw wireframe cube at expected position
                    Gizmos.DrawWireCube(center, Vector3.one * chunkWorldSize);
                    
                    // Draw coordinate label
                    #if UNITY_EDITOR
                    UnityEditor.Handles.Label(center, $"Expected\n{kvp.Key}");
                    #endif
                }
            }
        }
        
        // Draw actual mesh positions
        if (showActualMeshPositions)
        {
            Gizmos.color = actualColor;
            
            foreach (var kvp in meshBuilder.activeChunks)
            {
                Vector3 actualPos = kvp.Value.transform.position;
                Vector3 center = actualPos + Vector3.one * chunkWorldSize * 0.5f;
                
                // Draw sphere at actual position
                Gizmos.DrawWireSphere(center, chunkWorldSize * 0.1f);
                
                // Draw label
                #if UNITY_EDITOR
                UnityEditor.Handles.Label(center + Vector3.up * chunkWorldSize * 0.2f, 
                    $"Actual\n{kvp.Key}\n{actualPos}");
                #endif
            }
        }
    }
    
    void OnGUI()
    {
        if (worldManager == null || meshBuilder == null) return;
        
        int y = 400;
        GUI.Label(new Rect(10, y, 500, 20), "=== CHUNK DEBUG INFO ===");
        y += 20;
        
        float chunkWorldSize = worldManager.VoxelSize * TerrainWorldManager.CHUNK_SIZE;
        GUI.Label(new Rect(10, y, 500, 20), $"Expected chunk spacing: {chunkWorldSize}");
        y += 20;
        
        // Find two adjacent chunks and show their actual spacing
        foreach (var kvp1 in meshBuilder.activeChunks)
        {
            foreach (var kvp2 in meshBuilder.activeChunks)
            {
                int3 diff = kvp2.Key - kvp1.Key;
                if (diff.Equals(new int3(1, 0, 0))) // X neighbors
                {
                    float actualSpacing = Vector3.Distance(
                        kvp1.Value.transform.position,
                        kvp2.Value.transform.position
                    );
                    
                    GUI.Label(new Rect(10, y, 500, 20), 
                        $"Actual spacing {kvp1.Key} to {kvp2.Key}: {actualSpacing:F3}");
                    y += 20;
                    
                    if (Mathf.Abs(actualSpacing - chunkWorldSize) > 0.01f)
                    {
                        GUI.color = Color.red;
                        GUI.Label(new Rect(10, y, 500, 20), 
                            $"ERROR: Spacing is {actualSpacing / chunkWorldSize:F2}x expected!");
                        GUI.color = Color.white;
                        y += 20;
                    }
                    
                    break;
                }
            }
        }
    }
}