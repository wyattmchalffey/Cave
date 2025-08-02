using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

public class GPUChunkManager : MonoBehaviour
{
    [Header("Compute Shaders")]
    public ComputeShader noiseGenerationShader;
    public ComputeShader meshGenerationShader;
    
    [Header("Settings")]
    public CaveSettings caveSettings = CaveSettings.Default();
    public Material caveMaterial;
    
    [Header("Debug")]
    public bool showNoisePreview = false;
    private RenderTexture noisePreviewTexture;
    
    // Request structure
    public struct GPUChunkRequest
    {
        public Vector3Int coordinate;
        public int lodLevel;
        public System.Action<ChunkMeshData> onComplete;
    }
    
    // Mesh data structure
    public struct ChunkMeshData
    {
        public Vector3[] vertices;
        public int[] triangles;
        public Vector3[] normals;
        public Vector2[] uvs;
    }
    
    // Noise layer data for GPU
    private ComputeBuffer noiseLayerBuffer;
    private ComputeBuffer densityBuffer;
    private ComputeBuffer chamberBuffer;
    private ComputeBuffer vertexBuffer;
    private ComputeBuffer triangleBuffer;
    private ComputeBuffer counterBuffer;
    
    // Additional buffers for marching cubes
    private ComputeBuffer voxelOccupiedBuffer;
    private ComputeBuffer voxelVertexCountBuffer;
    private ComputeBuffer voxelVertexOffsetBuffer;
    
    // Marching cubes tables
    private ComputeBuffer edgeTableBuffer;
    private ComputeBuffer triTableBuffer;
    
    private int generateNoiseFieldKernel = -1;
    private int generateNoisePreviewKernel = -1;
    private int marchingCubesKernel = -1;
    
    private const int CHUNK_SIZE = 32;
    private const float VOXEL_SIZE = 0.25f;
    private const int MAX_VERTICES = 65536;
    private const int MAX_TRIANGLES = MAX_VERTICES * 3;
    
    private bool isInitialized = false;
    private WorldManager worldManager;
    
    // Struct matching the compute shader
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct GPUNoiseLayer
    {
        public int enabled;
        public int noiseType;
        public int blendMode;
        public float frequency;
        public float amplitude;
        public int octaves;
        public float persistence;
        public float lacunarity;
        public Vector3 offset;
        public float verticalSquash;
        public float densityBias;
        public float power;
        public float minHeight;
        public float maxHeight;
        public int useHeightConstraints;
        public float _padding; // Align to 16 bytes
    }
    
    void Start()
    {
        worldManager = GetComponent<WorldManager>();
        if (!InitializeComputeShaders())
        {
            enabled = false;
            return;
        }
        InitializeBuffers();
        isInitialized = true;
    }
    
    bool InitializeComputeShaders()
    {
        if (noiseGenerationShader == null)
        {
            Debug.LogError("Noise generation compute shader not assigned!");
            return false;
        }
        
        if (meshGenerationShader == null)
        {
            Debug.LogError("Mesh generation compute shader not assigned!");
            return false;
        }
        
        // Find kernels
        generateNoiseFieldKernel = noiseGenerationShader.FindKernel("GenerateNoiseField");
        generateNoisePreviewKernel = noiseGenerationShader.FindKernel("GenerateNoisePreview");
        marchingCubesKernel = meshGenerationShader.FindKernel("MarchingCubes");
        
        if (generateNoiseFieldKernel < 0 || marchingCubesKernel < 0)
        {
            Debug.LogError("Failed to find compute shader kernels!");
            return false;
        }
        
        return true;
    }
    
    void InitializeBuffers()
    {
        int densitySize = (CHUNK_SIZE + 1) * (CHUNK_SIZE + 1) * (CHUNK_SIZE + 1);
        int voxelCount = CHUNK_SIZE * CHUNK_SIZE * CHUNK_SIZE;
        
        // Density field buffer
        densityBuffer = new ComputeBuffer(densitySize, sizeof(float));
        
        // Vertex and triangle buffers
        vertexBuffer = new ComputeBuffer(MAX_VERTICES, sizeof(float) * 3);
        triangleBuffer = new ComputeBuffer(MAX_TRIANGLES, sizeof(int));
        
        // Counter buffers
        counterBuffer = new ComputeBuffer(2, sizeof(int), ComputeBufferType.Counter);
        
        // Voxel processing buffers
        voxelOccupiedBuffer = new ComputeBuffer(voxelCount, sizeof(uint));
        voxelVertexCountBuffer = new ComputeBuffer(voxelCount, sizeof(uint));
        voxelVertexOffsetBuffer = new ComputeBuffer(voxelCount, sizeof(uint));
        
        // Initialize marching cubes tables
        InitializeMarchingCubesTables();
        
        // Initialize chamber buffer with at least one element (even if empty)
        if (chamberBuffer == null)
        {
            chamberBuffer = new ComputeBuffer(1, sizeof(float) * 3);
            chamberBuffer.SetData(new Vector3[] { Vector3.zero });
        }
        
        // Noise preview texture
        if (showNoisePreview)
        {
            CreatePreviewTexture();
        }
    }
    
    void CreatePreviewTexture()
    {
        if (noisePreviewTexture != null)
        {
            noisePreviewTexture.Release();
        }
        
        noisePreviewTexture = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGB32);
        noisePreviewTexture.enableRandomWrite = true;
        noisePreviewTexture.Create();
    }
    
    public void RequestChunk(GPUChunkRequest request)
    {
        StartCoroutine(GenerateChunkAsync(request));
    }
    
    IEnumerator GenerateChunkAsync(GPUChunkRequest request)
    {
        if (!isInitialized || worldManager == null)
        {
            Debug.LogError("GPUChunkManager not properly initialized!");
            yield break;
        }
        
        // Update noise layer buffer
        UpdateNoiseLayerBuffer();
        
        // Update chamber buffer if available
        UpdateChamberBuffer();
        
        // Generate density field
        Vector3 chunkOrigin = new Vector3(
            request.coordinate.x * CHUNK_SIZE * VOXEL_SIZE,
            request.coordinate.y * CHUNK_SIZE * VOXEL_SIZE,
            request.coordinate.z * CHUNK_SIZE * VOXEL_SIZE
        );
        
        // Set noise generation parameters
        noiseGenerationShader.SetVector("ChunkOrigin", chunkOrigin);
        noiseGenerationShader.SetInt("LayerCount", worldManager.noiseLayerStack.layers.Count);
        noiseGenerationShader.SetBuffer(generateNoiseFieldKernel, "Layers", noiseLayerBuffer);
        noiseGenerationShader.SetBuffer(generateNoiseFieldKernel, "DensityField", densityBuffer);
        
        // Always set chamber buffer
        if (chamberBuffer == null)
        {
            chamberBuffer = new ComputeBuffer(1, sizeof(float) * 3);
            chamberBuffer.SetData(new Vector3[] { Vector3.zero });
        }
        
        noiseGenerationShader.SetBuffer(generateNoiseFieldKernel, "ChamberCenters", chamberBuffer);
        noiseGenerationShader.SetInt("ChamberCount", 
            (worldManager.networkPreprocessor != null && 
             worldManager.networkPreprocessor.chamberCenters.IsCreated) ? 
             worldManager.networkPreprocessor.chamberCenters.Length : 0);
        
        // Dispatch noise generation
        int threadGroups = Mathf.CeilToInt((CHUNK_SIZE + 1) / 8f);
        noiseGenerationShader.Dispatch(generateNoiseFieldKernel, threadGroups, threadGroups, threadGroups);
        
        yield return null; // Wait a frame
        
        // Reset counters
        counterBuffer.SetCounterValue(0);
        
        // Set mesh generation parameters
        meshGenerationShader.SetVector("ChunkOrigin", chunkOrigin);
        meshGenerationShader.SetInt("LODLevel", request.lodLevel);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "DensityField", densityBuffer);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "Vertices", vertexBuffer);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "Triangles", triangleBuffer);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "VertexCount", counterBuffer);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "TriangleCount", counterBuffer);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "EdgeTable", edgeTableBuffer);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "TriTable", triTableBuffer);
        
        // Set the voxel processing buffers
        meshGenerationShader.SetBuffer(marchingCubesKernel, "VoxelOccupied", voxelOccupiedBuffer);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "VoxelVertexCount", voxelVertexCountBuffer);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "VoxelVertexOffset", voxelVertexOffsetBuffer);
        
        // Dispatch mesh generation
        meshGenerationShader.Dispatch(marchingCubesKernel, threadGroups, threadGroups, threadGroups);
        
        yield return null; // Wait a frame
        
        // Read back results
        int[] counts = new int[2];
        counterBuffer.GetData(counts);
        int vertexCount = counts[0];
        int triangleCount = vertexCount; // Assuming triangle count equals vertex count
        
        if (vertexCount > 0 && vertexCount < MAX_VERTICES)
        {
            // Read vertex data
            Vector3[] vertices = new Vector3[vertexCount];
            vertexBuffer.GetData(vertices, 0, 0, vertexCount);
            
            // Read triangle data
            int[] triangles = new int[triangleCount];
            triangleBuffer.GetData(triangles, 0, 0, triangleCount);
            
            // Create mesh data
            ChunkMeshData meshData = new ChunkMeshData
            {
                vertices = vertices,
                triangles = triangles,
                normals = CalculateNormals(vertices, triangles),
                uvs = CalculateUVs(vertices)
            };
            
            // Invoke callback
            request.onComplete?.Invoke(meshData);
        }
        else
        {
            // Empty chunk
            request.onComplete?.Invoke(new ChunkMeshData
            {
                vertices = new Vector3[0],
                triangles = new int[0],
                normals = new Vector3[0],
                uvs = new Vector2[0]
            });
        }
    }
    
    void UpdateNoiseLayerBuffer()
    {
        var layers = worldManager.noiseLayerStack.layers;
        int layerCount = Mathf.Min(layers.Count, 16); // Max 16 layers
        
        // Create or resize buffer if needed
        if (noiseLayerBuffer == null || noiseLayerBuffer.count != layerCount)
        {
            noiseLayerBuffer?.Release();
            if (layerCount > 0)
            {
                noiseLayerBuffer = new ComputeBuffer(layerCount, System.Runtime.InteropServices.Marshal.SizeOf<GPUNoiseLayer>());
            }
        }
        
        if (layerCount > 0)
        {
            // Convert layers to GPU format
            GPUNoiseLayer[] gpuLayers = new GPUNoiseLayer[layerCount];
            for (int i = 0; i < layerCount; i++)
            {
                var layer = layers[i];
                gpuLayers[i] = new GPUNoiseLayer
                {
                    enabled = layer.enabled ? 1 : 0,
                    noiseType = (int)layer.noiseType,
                    blendMode = (int)layer.blendMode,
                    frequency = layer.frequency,
                    amplitude = layer.amplitude,
                    octaves = layer.octaves,
                    persistence = layer.persistence,
                    lacunarity = layer.lacunarity,
                    offset = layer.offset,
                    verticalSquash = layer.verticalSquash,
                    densityBias = layer.densityBias,
                    power = layer.power,
                    minHeight = layer.minHeight,
                    maxHeight = layer.maxHeight,
                    useHeightConstraints = layer.useHeightConstraints ? 1 : 0
                };
            }
            
            noiseLayerBuffer.SetData(gpuLayers);
        }
    }
    
    void UpdateChamberBuffer()
    {
        if (worldManager.networkPreprocessor != null && 
            worldManager.networkPreprocessor.chamberCenters.IsCreated &&
            worldManager.networkPreprocessor.chamberCenters.Length > 0)
        {
            int chamberCount = worldManager.networkPreprocessor.chamberCenters.Length;
            
            if (chamberBuffer == null || chamberBuffer.count != chamberCount)
            {
                chamberBuffer?.Release();
                chamberBuffer = new ComputeBuffer(chamberCount, sizeof(float) * 3);
            }
            
            // Copy chamber data
            Vector3[] chambers = new Vector3[chamberCount];
            for (int i = 0; i < chamberCount; i++)
            {
                chambers[i] = worldManager.networkPreprocessor.chamberCenters[i];
            }
            chamberBuffer.SetData(chambers);
        }
    }
    
    public void GenerateNoisePreview(int layerIndex = -1)
    {
        if (!isInitialized || noisePreviewTexture == null) return;
        
        UpdateNoiseLayerBuffer();
        
        // Set preview parameters
        noiseGenerationShader.SetInt("LayerCount", 
            layerIndex >= 0 ? layerIndex + 1 : worldManager.noiseLayerStack.layers.Count);
        noiseGenerationShader.SetBuffer(generateNoisePreviewKernel, "Layers", noiseLayerBuffer);
        noiseGenerationShader.SetTexture(generateNoisePreviewKernel, "PreviewTexture", noisePreviewTexture);
        
        // Dispatch preview generation
        int threadGroupsX = Mathf.CeilToInt(noisePreviewTexture.width / 8f);
        int threadGroupsY = Mathf.CeilToInt(noisePreviewTexture.height / 8f);
        noiseGenerationShader.Dispatch(generateNoisePreviewKernel, threadGroupsX, threadGroupsY, 1);
    }
    
    Vector3[] CalculateNormals(Vector3[] vertices, int[] triangles)
    {
        Vector3[] normals = new Vector3[vertices.Length];
        
        // Calculate face normals and accumulate
        for (int i = 0; i < triangles.Length; i += 3)
        {
            int i0 = triangles[i];
            int i1 = triangles[i + 1];
            int i2 = triangles[i + 2];
            
            Vector3 v0 = vertices[i0];
            Vector3 v1 = vertices[i1];
            Vector3 v2 = vertices[i2];
            
            Vector3 faceNormal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
            
            normals[i0] += faceNormal;
            normals[i1] += faceNormal;
            normals[i2] += faceNormal;
        }
        
        // Normalize
        for (int i = 0; i < normals.Length; i++)
        {
            normals[i] = normals[i].normalized;
        }
        
        return normals;
    }
    
    Vector2[] CalculateUVs(Vector3[] vertices)
    {
        Vector2[] uvs = new Vector2[vertices.Length];
        
        for (int i = 0; i < vertices.Length; i++)
        {
            // Simple triplanar mapping
            uvs[i] = new Vector2(vertices[i].x * 0.1f, vertices[i].z * 0.1f);
        }
        
        return uvs;
    }
    
    void InitializeMarchingCubesTables()
    {
        // Initialize edge table
        edgeTableBuffer = new ComputeBuffer(256, sizeof(int));
        edgeTableBuffer.SetData(MarchingCubesTables.EdgeTable);
        
        // Initialize triangle table
        triTableBuffer = new ComputeBuffer(256 * 16, sizeof(int));
        triTableBuffer.SetData(MarchingCubesTables.TriTable);
    }
    
    void OnDestroy()
    {
        // Clean up buffers
        if (densityBuffer != null && densityBuffer.IsValid())
            densityBuffer.Release();
        
        if (noiseLayerBuffer != null && noiseLayerBuffer.IsValid())
            noiseLayerBuffer.Release();
            
        if (chamberBuffer != null && chamberBuffer.IsValid())
            chamberBuffer.Release();
            
        if (vertexBuffer != null && vertexBuffer.IsValid())
            vertexBuffer.Release();
            
        if (triangleBuffer != null && triangleBuffer.IsValid())
            triangleBuffer.Release();
            
        if (counterBuffer != null && counterBuffer.IsValid())
            counterBuffer.Release();
            
        if (voxelOccupiedBuffer != null && voxelOccupiedBuffer.IsValid())
            voxelOccupiedBuffer.Release();
            
        if (voxelVertexCountBuffer != null && voxelVertexCountBuffer.IsValid())
            voxelVertexCountBuffer.Release();
            
        if (voxelVertexOffsetBuffer != null && voxelVertexOffsetBuffer.IsValid())
            voxelVertexOffsetBuffer.Release();
            
        if (edgeTableBuffer != null && edgeTableBuffer.IsValid())
            edgeTableBuffer.Release();
            
        if (triTableBuffer != null && triTableBuffer.IsValid())
            triTableBuffer.Release();
            
        // Safely release render texture
        if (noisePreviewTexture != null)
        {
            noisePreviewTexture.Release();
            if (Application.isPlaying)
                Destroy(noisePreviewTexture);
            else
                DestroyImmediate(noisePreviewTexture);
            noisePreviewTexture = null;
        }
    }
    
    void OnGUI()
    {
        if (showNoisePreview && noisePreviewTexture != null)
        {
            GUI.DrawTexture(new Rect(10, 10, 256, 256), noisePreviewTexture);
            GUI.Label(new Rect(10, 270, 256, 20), "Noise Preview (2D Slice at Y=0)");
        }
    }
}