// ChunkGPUIntegration.cs - Integrates GPU generation with existing chunk system
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(WorldManager))]
[RequireComponent(typeof(GPUChunkManager))]
public class ChunkGPUIntegration : MonoBehaviour
{
    private WorldManager worldManager;
    private GPUChunkManager gpuChunkManager;
    
    [Header("Performance Settings")]
    [SerializeField] private bool useGPUGeneration = true;
    
    private Dictionary<Vector3Int, GameObject> activeChunks = new Dictionary<Vector3Int, GameObject>();
    private HashSet<Vector3Int> processingChunks = new HashSet<Vector3Int>();
    
    void Start()
    {
        worldManager = GetComponent<WorldManager>();
        gpuChunkManager = GetComponent<GPUChunkManager>();
        
        if (useGPUGeneration)
        {
            Debug.Log("GPU Terrain Generation Enabled");
        }
    }
    
    public void RequestChunk(Vector3Int coordinate, int lodLevel = 0)
    {
        if (activeChunks.ContainsKey(coordinate) || processingChunks.Contains(coordinate))
            return;
        
        processingChunks.Add(coordinate);
        
        if (useGPUGeneration && gpuChunkManager != null)
        {
            // Use GPU generation
            var request = new GPUChunkManager.GPUChunkRequest
            {
                coordinate = coordinate,
                lodLevel = lodLevel,
                onComplete = (meshData) => OnGPUChunkComplete(coordinate, meshData)
            };
            
            gpuChunkManager.RequestChunk(request);
        }
        else
        {
            // Fallback to CPU generation
            StartCoroutine(GenerateChunkCPU(coordinate, lodLevel));
        }
    }
    
    void OnGPUChunkComplete(Vector3Int coordinate, GPUChunkManager.ChunkMeshData meshData)
    {
        if (meshData.vertices.Length == 0)
        {
            processingChunks.Remove(coordinate);
            return;
        }
        
        // Create chunk GameObject
        GameObject chunkObj = new GameObject($"Chunk_{coordinate}");
        chunkObj.transform.position = new Vector3(
            coordinate.x * ChunkConstants.CHUNK_SIZE * ChunkConstants.VOXEL_SIZE,
            coordinate.y * ChunkConstants.CHUNK_SIZE * ChunkConstants.VOXEL_SIZE,
            coordinate.z * ChunkConstants.CHUNK_SIZE * ChunkConstants.VOXEL_SIZE
        );
        
        // Add mesh components
        MeshFilter meshFilter = chunkObj.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = chunkObj.AddComponent<MeshRenderer>();
        
        // Create mesh
        Mesh mesh = new Mesh();
        mesh.name = $"ChunkMesh_{coordinate}";
        
        // Check if we need 32-bit indices
        if (meshData.vertices.Length > 65535)
        {
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }
        
        // Assign mesh data
        mesh.vertices = meshData.vertices;
        mesh.triangles = meshData.triangles;
        mesh.normals = meshData.normals;
        mesh.uv = meshData.uvs;
        
        // Optimize mesh
        mesh.RecalculateBounds();
        mesh.Optimize();
        
        meshFilter.mesh = mesh;
        meshRenderer.material = gpuChunkManager.caveMaterial;
        
        // Add collision if needed (you can add a toggle for this)
        bool generateColliders = true; // Or make this a public field
        if (generateColliders)
        {
            MeshCollider collider = chunkObj.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
        }
        
        // Store chunk
        activeChunks[coordinate] = chunkObj;
        processingChunks.Remove(coordinate);
    }
    
    IEnumerator GenerateChunkCPU(Vector3Int coordinate, int lodLevel)
    {
        // CPU fallback implementation
        Debug.LogWarning($"CPU generation fallback for chunk {coordinate}");
        yield return null;
        processingChunks.Remove(coordinate);
    }
    
    public void UnloadChunk(Vector3Int coordinate)
    {
        if (activeChunks.TryGetValue(coordinate, out GameObject chunk))
        {
            Destroy(chunk);
            activeChunks.Remove(coordinate);
        }
    }
    
    public bool IsChunkLoaded(Vector3Int coordinate)
    {
        return activeChunks.ContainsKey(coordinate);
    }
    
    public bool IsChunkProcessing(Vector3Int coordinate)
    {
        return processingChunks.Contains(coordinate);
    }
    
    void OnDestroy()
    {
        foreach (var chunk in activeChunks.Values)
        {
            if (chunk != null)
                Destroy(chunk);
        }
        activeChunks.Clear();
    }
}

// Helper class for chunk constants
public static class ChunkConstants
{
    public const int CHUNK_SIZE = 32;
    public const float VOXEL_SIZE = 0.25f;
}