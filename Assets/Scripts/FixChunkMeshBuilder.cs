using UnityEngine;
using GPUTerrain;
using Unity.Mathematics;
using System.Collections;

public class FixChunkMeshBuilder : MonoBehaviour
{
    private ChunkMeshBuilder meshBuilder;
    private TerrainWorldManager worldManager;
    
    void Start()
    {
        meshBuilder = GetComponent<ChunkMeshBuilder>();
        worldManager = GetComponent<TerrainWorldManager>();
    }
    
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.I))
        {
            InspectMeshBuilder();
        }
        
        if (Input.GetKeyDown(KeyCode.O))
        {
            StartCoroutine(ForceRebuildNearbyChunk());
        }
    }
    
    void InspectMeshBuilder()
    {
        Debug.Log("=== INSPECTING MESH BUILDER ===");
        
        // Check if mesh builder is running
        Debug.Log($"MeshBuilder enabled: {meshBuilder.enabled}");
        Debug.Log($"MeshBuilder gameObject active: {meshBuilder.gameObject.activeSelf}");
        
        // Check compute shader
        var shaderField = meshBuilder.GetType().GetField("meshExtractionShader", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var shader = shaderField?.GetValue(meshBuilder) as ComputeShader;
        Debug.Log($"Mesh extraction shader: {(shader != null ? shader.name : "NULL")}");
        
        // Check material
        var matField = meshBuilder.GetType().GetField("chunkMaterial", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var mat = matField?.GetValue(meshBuilder) as Material;
        Debug.Log($"Chunk material: {(mat != null ? mat.name : "NULL")}");
        
        // Check world data texture
        var texField = meshBuilder.GetType().GetField("worldDataTexture", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var tex = texField?.GetValue(meshBuilder) as RenderTexture;
        Debug.Log($"World data texture: {(tex != null ? $"{tex.width}x{tex.height}x{tex.volumeDepth}" : "NULL")}");
        
        // Find chunks that should have meshes but don't
        var states = worldManager.GetChunkStates();
        int shouldHaveMesh = 0;
        int actuallyHaveMesh = 0;
        
        foreach (var kvp in states)
        {
            if (kvp.Value.hasMesh)
            {
                shouldHaveMesh++;
                
                // Check if GameObject exists
                GameObject chunk = GameObject.Find($"Chunk_{kvp.Key}");
                if (chunk != null)
                {
                    actuallyHaveMesh++;
                }
            }
        }
        
        Debug.Log($"Chunks marked as having mesh: {shouldHaveMesh}");
        Debug.Log($"Actual chunk GameObjects found: {actuallyHaveMesh}");
    }
    
    IEnumerator ForceRebuildNearbyChunk()
    {
        Debug.Log("=== FORCE REBUILD NEARBY CHUNK ===");
        
        // Find a chunk near the player that's marked as generated
        Vector3 playerPos = Camera.main.transform.position;
        int3 playerChunk = worldManager.WorldToChunkCoord(playerPos);
        
        // Look for a nearby generated chunk
        int3 targetChunk = playerChunk;
        bool found = false;
        
        for (int y = 0; y < 3 && !found; y++)
        {
            for (int x = -1; x <= 1 && !found; x++)
            {
                for (int z = -1; z <= 1 && !found; z++)
                {
                    int3 testChunk = playerChunk + new int3(x, y, z);
                    var state = worldManager.GetChunkState(testChunk);
                    
                    if (state.isGenerated)
                    {
                        targetChunk = testChunk;
                        found = true;
                        Debug.Log($"Found generated chunk at {targetChunk}");
                    }
                }
            }
        }
        
        if (!found)
        {
            Debug.LogError("No generated chunks found nearby!");
            yield break;
        }
        
        // Clear the chunk's mesh state
        var state2 = worldManager.GetChunkState(targetChunk);
        state2.hasMesh = false;
        
        // Force rebuild by calling the private method
        var method = meshBuilder.GetType().GetMethod("BuildChunkMesh", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        if (method != null)
        {
            Debug.Log($"Forcing rebuild of chunk {targetChunk}...");
            yield return StartCoroutine((IEnumerator)method.Invoke(meshBuilder, new object[] { targetChunk }));
            Debug.Log("Rebuild complete");
            
            // Check if it exists now
            GameObject chunk = GameObject.Find($"Chunk_{targetChunk}");
            if (chunk != null)
            {
                Debug.Log($"SUCCESS! Chunk created at {chunk.transform.position}");
                var mf = chunk.GetComponent<MeshFilter>();
                if (mf?.mesh != null)
                {
                    Debug.Log($"Mesh has {mf.mesh.vertexCount} vertices, {mf.mesh.triangles.Length / 3} triangles");
                }
            }
            else
            {
                Debug.LogError("Chunk GameObject still not found after rebuild!");
            }
        }
    }
    
    void OnGUI()
    {
        int y = 850;
        GUI.Label(new Rect(10, y, 300, 20), "=== FIX MESH BUILDER ===");
        y += 20;
        GUI.Label(new Rect(10, y, 300, 20), "I - Inspect mesh builder state");
        y += 20;
        GUI.Label(new Rect(10, y, 300, 20), "O - Force rebuild nearby chunk");
    }
}