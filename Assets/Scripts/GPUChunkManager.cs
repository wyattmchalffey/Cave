using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

public class GPUChunkManager : MonoBehaviour
{
    [Header("Compute Shaders")]
    public ComputeShader caveGenerationShader;
    public ComputeShader meshGenerationShader;
    
    [Header("Settings")]
    public CaveSettings caveSettings;
    public Material caveMaterial;
    
    private ComputeBuffer densityBuffer;
    private ComputeBuffer chamberBuffer;
    private ComputeBuffer vertexBuffer;
    private ComputeBuffer triangleBuffer;
    
    private int generateDensityKernel;
    private int generateChamberKernel;
    private int generateTunnelKernel;
    
    private const int CHUNK_SIZE = Chunk.CHUNK_SIZE;
    private const float VOXEL_SIZE = Chunk.VOXEL_SIZE;
    
    public struct GPUChunkRequest
    {
        public Vector3Int coordinate;
        public int lodLevel;
        public System.Action<ChunkMeshData> onComplete;
    }
    
    public struct ChunkMeshData
    {
        public Vector3[] vertices;
        public int[] triangles;
        public Vector3[] normals;
        public Vector2[] uvs;
    }
    
    private Queue<GPUChunkRequest> requestQueue = new Queue<GPUChunkRequest>();
    private bool isProcessing = false;
    
    void Start()
    {
        InitializeComputeShaders();
        InitializeBuffers();
    }
    
    void InitializeComputeShaders()
    {
        if (caveGenerationShader == null)
        {
            Debug.LogError("Cave generation compute shader not assigned!");
            return;
        }
        
        // Find kernels
        generateDensityKernel = caveGenerationShader.FindKernel("GenerateDensityField");
        generateChamberKernel = caveGenerationShader.FindKernel("GenerateChamberField");
        generateTunnelKernel = caveGenerationShader.FindKernel("GenerateTunnelField");
    }
    
    void InitializeBuffers()
    {
        // Density field buffer
        int voxelCount = (CHUNK_SIZE + 1) * (CHUNK_SIZE + 1) * (CHUNK_SIZE + 1);
        densityBuffer = new ComputeBuffer(voxelCount, sizeof(float));
        
        // Vertex and triangle buffers for mesh generation
        int maxVertices = voxelCount * 4; // Conservative estimate
        int maxTriangles = voxelCount * 6;
        
        vertexBuffer = new ComputeBuffer(maxVertices, sizeof(float) * 3);
        triangleBuffer = new ComputeBuffer(maxTriangles, sizeof(int));
    }
    
    public void RequestChunk(GPUChunkRequest request)
    {
        requestQueue.Enqueue(request);
        
        if (!isProcessing)
        {
            ProcessNextRequest();
        }
    }
    
    void ProcessNextRequest()
    {
        if (requestQueue.Count == 0)
        {
            isProcessing = false;
            return;
        }
        
        isProcessing = true;
        var request = requestQueue.Dequeue();
        
        StartCoroutine(GenerateChunkGPU(request));
    }
    
    System.Collections.IEnumerator GenerateChunkGPU(GPUChunkRequest request)
    {
        // Set shader parameters
        Vector3 chunkOrigin = new Vector3(
            request.coordinate.x * CHUNK_SIZE * VOXEL_SIZE,
            request.coordinate.y * CHUNK_SIZE * VOXEL_SIZE,
            request.coordinate.z * CHUNK_SIZE * VOXEL_SIZE
        );
        
        caveGenerationShader.SetVector("ChunkOrigin", chunkOrigin);
        
        // Cave settings
        caveGenerationShader.SetVector("CaveSettings1", new Vector4(
            caveSettings.chamberFrequency,
            caveSettings.chamberMinRadius,
            caveSettings.chamberMaxRadius,
            caveSettings.chamberFloorFlatness
        ));
        
        caveGenerationShader.SetVector("CaveSettings2", new Vector4(
            caveSettings.tunnelMinRadius,
            caveSettings.tunnelMaxRadius,
            caveSettings.stratificationStrength,
            caveSettings.stratificationFrequency
        ));
        
        caveGenerationShader.SetVector("CaveSettings3", new Vector4(
            caveSettings.erosionStrength,
            caveSettings.rockHardness,
            caveSettings.minCaveHeight,
            caveSettings.maxCaveHeight
        ));
        
        caveGenerationShader.SetVector("NoiseSettings", new Vector4(
            caveSettings.seed,
            caveSettings.worleyOctaves,
            caveSettings.worleyPersistence,
            Time.time
        ));
        
        // Set buffers
        caveGenerationShader.SetBuffer(generateDensityKernel, "DensityField", densityBuffer);
        
        // Set chamber data if available
        var worldManager = GetComponent<WorldManager>();
        if (worldManager != null && worldManager.networkPreprocessor != null)
        {
            var chamberCenters = worldManager.networkPreprocessor.chamberCenters;
            if (chamberCenters.IsCreated && chamberCenters.Length > 0)
            {
                chamberBuffer = new ComputeBuffer(chamberCenters.Length, sizeof(float) * 3);
                chamberBuffer.SetData(chamberCenters.ToArray());
                
                caveGenerationShader.SetBuffer(generateDensityKernel, "ChamberCenters", chamberBuffer);
                caveGenerationShader.SetInt("ChamberCount", chamberCenters.Length);
            }
        }
        
        // Dispatch density generation
        int threadGroups = Mathf.CeilToInt((CHUNK_SIZE + 1) / 8.0f);
        caveGenerationShader.Dispatch(generateDensityKernel, threadGroups, threadGroups, threadGroups);
        
        // Wait for GPU
        yield return new WaitForEndOfFrame();
        
        // Read back density data
        float[] densityData = new float[(CHUNK_SIZE + 1) * (CHUNK_SIZE + 1) * (CHUNK_SIZE + 1)];
        densityBuffer.GetData(densityData);
        
        // Generate mesh on CPU for now (could be GPU accelerated)
        ChunkMeshData meshData = GenerateMeshFromDensity(densityData, request.lodLevel);
        
        // Callback with mesh data
        request.onComplete?.Invoke(meshData);
        
        // Clean up temporary buffer
        if (chamberBuffer != null)
        {
            chamberBuffer.Release();
            chamberBuffer = null;
        }
        
        // Process next request
        ProcessNextRequest();
    }
    
    ChunkMeshData GenerateMeshFromDensity(float[] densityData, int lodLevel)
    {
        // This would use dual contouring or marching cubes
        // For now, return empty mesh data
        return new ChunkMeshData
        {
            vertices = new Vector3[0],
            triangles = new int[0],
            normals = new Vector3[0],
            uvs = new Vector2[0]
        };
    }
    
    void OnDestroy()
    {
        // Release buffers
        densityBuffer?.Release();
        vertexBuffer?.Release();
        triangleBuffer?.Release();
        chamberBuffer?.Release();
    }
}