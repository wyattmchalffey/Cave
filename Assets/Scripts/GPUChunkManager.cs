// GPUChunkManager.cs - Complete Implementation with Async Optimization
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
    private ComputeBuffer normalBuffer;
    private ComputeBuffer triangleBuffer;
    private ComputeBuffer counterBuffer;
    
    // Marching cubes tables
    private ComputeBuffer edgeTableBuffer;
    private ComputeBuffer triTableBuffer;
    
    private int generateNoiseFieldKernel = -1;
    private int generateNoisePreviewKernel = -1;
    private int marchingCubesKernel = -1;
    
    private const int CHUNK_SIZE = 32;
    private const float VOXEL_SIZE = 0.25f;
    private const int MAX_VERTICES = 65536;
    private const int MAX_TRIANGLES = MAX_VERTICES / 3;
    
    private bool isInitialized = false;
    private WorldManager worldManager;
    
    // Queue for chunk requests
    private Queue<GPUChunkRequest> requestQueue = new Queue<GPUChunkRequest>();
    private bool isProcessing = false;
    
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
            Debug.LogError("Failed to find required compute shader kernels!");
            return false;
        }
        
        return true;
    }
    
    void InitializeBuffers()
    {
        int densitySize = (CHUNK_SIZE + 1) * (CHUNK_SIZE + 1) * (CHUNK_SIZE + 1);
        
        // Density field buffer
        densityBuffer = new ComputeBuffer(densitySize, sizeof(float));
        
        // Vertex and triangle buffers with counter
        vertexBuffer = new ComputeBuffer(MAX_VERTICES, sizeof(float) * 3, ComputeBufferType.Append);
        normalBuffer = new ComputeBuffer(MAX_VERTICES, sizeof(float) * 3, ComputeBufferType.Append);
        triangleBuffer = new ComputeBuffer(MAX_TRIANGLES * 3, sizeof(int), ComputeBufferType.Append);
        
        // Counter buffer to track vertex/triangle count
        counterBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.IndirectArguments);
        
        // Initialize marching cubes tables
        InitializeMarchingCubesTables();
        
        // Initialize chamber buffer with at least one element
        chamberBuffer = new ComputeBuffer(1, sizeof(float) * 3);
        chamberBuffer.SetData(new Vector3[] { Vector3.zero });
        
        // Noise preview texture
        if (showNoisePreview)
        {
            CreatePreviewTexture();
        }
    }
    
    void InitializeMarchingCubesTables()
    {
        // Edge table - which edges are intersected for each cube configuration
        int[] edgeTable = MarchingCubesTables.EdgeTable;
        edgeTableBuffer = new ComputeBuffer(256, sizeof(int));
        edgeTableBuffer.SetData(edgeTable);
        
        // Triangle table - how to connect the edges to form triangles
        int[] triTable = MarchingCubesTables.TriTable;
        triTableBuffer = new ComputeBuffer(256 * 16, sizeof(int));
        triTableBuffer.SetData(triTable);
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
        requestQueue.Enqueue(request);
        
        if (!isProcessing)
        {
            StartCoroutine(ProcessRequestQueue());
        }
    }
    
    public void RequestChunkBatch(List<GPUChunkRequest> requests)
    {
        StartCoroutine(ProcessBatchAsync(requests));
    }
    
    IEnumerator ProcessRequestQueue()
    {
        isProcessing = true;
        
        while (requestQueue.Count > 0)
        {
            var request = requestQueue.Dequeue();
            yield return StartCoroutine(GenerateChunkAsync(request));
        }
        
        isProcessing = false;
    }

    void GenerateChunkOnGPU(Vector3 chunkOrigin, int lodLevel)
    {
        // Dispatch noise generation
        noiseGenerationShader.SetVector("ChunkOrigin", chunkOrigin);
        noiseGenerationShader.SetInt("LayerCount", worldManager.noiseLayerStack.layers.Count);
        noiseGenerationShader.SetBuffer(generateNoiseFieldKernel, "Layers", noiseLayerBuffer);
        noiseGenerationShader.SetBuffer(generateNoiseFieldKernel, "DensityField", densityBuffer);
        noiseGenerationShader.SetBuffer(generateNoiseFieldKernel, "ChamberCenters", chamberBuffer);
        noiseGenerationShader.SetInt("ChamberCount", 0);

        int threadGroups = Mathf.CeilToInt((CHUNK_SIZE + 1) / 8f);
        noiseGenerationShader.Dispatch(generateNoiseFieldKernel, threadGroups, threadGroups, threadGroups);

        // Reset buffers
        vertexBuffer.SetCounterValue(0);
        normalBuffer.SetCounterValue(0);
        triangleBuffer.SetCounterValue(0);

        // IMPORTANT: Copy the counter after reset
        ComputeBuffer.CopyCount(vertexBuffer, counterBuffer, 0);

        // Dispatch mesh generation
        meshGenerationShader.SetVector("ChunkOrigin", chunkOrigin);
        meshGenerationShader.SetInt("LODLevel", lodLevel);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "DensityField", densityBuffer);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "Vertices", vertexBuffer);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "Normals", normalBuffer);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "Triangles", triangleBuffer);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "EdgeTable", edgeTableBuffer);
        meshGenerationShader.SetBuffer(marchingCubesKernel, "TriTable", triTableBuffer);

        threadGroups = Mathf.CeilToInt(CHUNK_SIZE / 8f);
        meshGenerationShader.Dispatch(marchingCubesKernel, threadGroups, threadGroups, threadGroups);

        // Copy the final count
        ComputeBuffer.CopyCount(vertexBuffer, counterBuffer, 0);
    }

    IEnumerator GenerateChunkAsync(GPUChunkRequest request)
    {
        if (!isInitialized || worldManager == null)
        {
            Debug.LogError("GPUChunkManager not properly initialized!");
            yield break;
        }
        
        // Update buffers
        UpdateNoiseLayerBuffer();
        UpdateChamberBuffer();
        
        // Calculate chunk origin
        Vector3 chunkOrigin = new Vector3(
            request.coordinate.x * CHUNK_SIZE * VOXEL_SIZE,
            request.coordinate.y * CHUNK_SIZE * VOXEL_SIZE,
            request.coordinate.z * CHUNK_SIZE * VOXEL_SIZE
        );
        
        // Generate on GPU
        GenerateChunkOnGPU(chunkOrigin, request.lodLevel);
        
        // Use async readback for vertex count
        bool countReceived = false;
        int vertexCount = 0;
        
        AsyncGPUReadback.Request(counterBuffer, (countRequest) =>
        {
            if (countRequest.hasError)
            {
                Debug.LogError("GPU readback error!");
                countReceived = true;
                return;
            }
            
            var data = countRequest.GetData<int>();
            vertexCount = data[0];
            
            // Fix for the vertex count bug
            if (vertexCount > MAX_VERTICES)
            {
                Debug.LogWarning($"Vertex count {vertexCount} exceeds max, clamping to {MAX_VERTICES}");
                vertexCount = Mathf.Min(vertexCount, MAX_VERTICES);
            }
            
            countReceived = true;
        });
        
        // Wait for count
        while (!countReceived)
        {
            yield return null;
        }
        
        if (vertexCount > 0 && vertexCount <= MAX_VERTICES)
        {
            // Now do async readback for actual vertex data
            bool dataReceived = false;
            ChunkMeshData meshData = new ChunkMeshData();
            
            // Request vertices
            AsyncGPUReadback.Request(vertexBuffer, vertexCount * 12, 0, (vertRequest) =>
            {
                if (vertRequest.hasError)
                {
                    dataReceived = true;
                    return;
                }
                
                var vertices = new Vector3[vertexCount];
                var vertexData = vertRequest.GetData<Vector3>();
                for (int i = 0; i < vertexCount; i++)
                {
                    vertices[i] = vertexData[i];
                }
                
                // Request normals
                AsyncGPUReadback.Request(normalBuffer, vertexCount * 12, 0, (normRequest) =>
                {
                    if (normRequest.hasError)
                    {
                        dataReceived = true;
                        return;
                    }
                    
                    var normals = new Vector3[vertexCount];
                    var normalData = normRequest.GetData<Vector3>();
                    for (int i = 0; i < vertexCount; i++)
                    {
                        normals[i] = normalData[i];
                    }
                    
                    // Generate triangle indices
                    int[] triangles = new int[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                    {
                        triangles[i] = i;
                    }
                    
                    // Generate UVs
                    Vector2[] uvs = CalculateUVs(vertices);
                    
                    // Create mesh data
                    meshData = new ChunkMeshData
                    {
                        vertices = vertices,
                        triangles = triangles,
                        normals = normals,
                        uvs = uvs
                    };
                    
                    dataReceived = true;
                });
            });
            
            // Wait for all data
            while (!dataReceived)
            {
                yield return null;
            }
            
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
    
    IEnumerator ProcessBatchAsync(List<GPUChunkRequest> requests)
    {
        // Generate all chunks on GPU first
        foreach (var request in requests)
        {
            UpdateNoiseLayerBuffer();
            UpdateChamberBuffer();
            
            Vector3 chunkOrigin = new Vector3(
                request.coordinate.x * CHUNK_SIZE * VOXEL_SIZE,
                request.coordinate.y * CHUNK_SIZE * VOXEL_SIZE,
                request.coordinate.z * CHUNK_SIZE * VOXEL_SIZE
            );
            
            GenerateChunkOnGPU(chunkOrigin, request.lodLevel);
        }
        
        // Then read them all back asynchronously
        int completed = 0;
        
        foreach (var request in requests)
        {
            AsyncGPUReadback.Request(counterBuffer, (countRequest) =>
            {
                // Process each chunk asynchronously
                StartCoroutine(ProcessSingleChunkReadback(countRequest, request, () => completed++));
            });
        }
        
        // Wait for all to complete
        while (completed < requests.Count)
        {
            yield return null;
        }
    }
    
    IEnumerator ProcessSingleChunkReadback(AsyncGPUReadbackRequest countRequest, GPUChunkRequest request, System.Action onComplete)
    {
        if (countRequest.hasError)
        {
            onComplete?.Invoke();
            yield break;
        }
        
        var data = countRequest.GetData<int>();
        int vertexCount = Mathf.Min(data[0], MAX_VERTICES);
        
        if (vertexCount > 0)
        {
            // Async readback vertices and normals
            bool done = false;
            
            AsyncGPUReadback.Request(vertexBuffer, vertexCount * 12, 0, (vertRequest) =>
            {
                if (!vertRequest.hasError)
                {
                    var vertices = new Vector3[vertexCount];
                    vertRequest.GetData<Vector3>().CopyTo(vertices);
                    
                    AsyncGPUReadback.Request(normalBuffer, vertexCount * 12, 0, (normRequest) =>
                    {
                        if (!normRequest.hasError)
                        {
                            var normals = new Vector3[vertexCount];
                            normRequest.GetData<Vector3>().CopyTo(normals);
                            
                            // Create mesh data
                            var meshData = new ChunkMeshData
                            {
                                vertices = vertices,
                                triangles = GenerateSequentialIndices(vertexCount),
                                normals = normals,
                                uvs = CalculateUVs(vertices)
                            };
                            
                            request.onComplete?.Invoke(meshData);
                        }
                        done = true;
                        onComplete?.Invoke();
                    });
                }
                else
                {
                    done = true;
                    onComplete?.Invoke();
                }
            });
            
            while (!done) yield return null;
        }
        else
        {
            request.onComplete?.Invoke(new ChunkMeshData
            {
                vertices = new Vector3[0],
                triangles = new int[0],
                normals = new Vector3[0],
                uvs = new Vector2[0]
            });
            onComplete?.Invoke();
        }
    }
    
    void UpdateNoiseLayerBuffer()
    {
        var layers = worldManager.noiseLayerStack.layers;
        int layerCount = Mathf.Min(layers.Count, 16); // Max 16 layers
        
        // Always create at least one element
        int bufferSize = Mathf.Max(1, layerCount);
        
        // Create or resize buffer if needed
        if (noiseLayerBuffer == null || noiseLayerBuffer.count != bufferSize)
        {
            noiseLayerBuffer?.Release();
            noiseLayerBuffer = new ComputeBuffer(bufferSize, System.Runtime.InteropServices.Marshal.SizeOf<GPUNoiseLayer>());
        }
        
        // Convert layers to GPU format
        GPUNoiseLayer[] gpuLayers = new GPUNoiseLayer[bufferSize];
        
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
                useHeightConstraints = layer.useHeightConstraints ? 1 : 0,
                _padding = 0
            };
        }
        
        // Fill remaining slots with disabled layers if needed
        for (int i = layerCount; i < bufferSize; i++)
        {
            gpuLayers[i] = new GPUNoiseLayer { enabled = 0 };
        }
        
        noiseLayerBuffer.SetData(gpuLayers);
    }
    
    void UpdateChamberBuffer()
    {
        // TODO: Implement chamber system
        // For now, just ensure buffer exists
        if (chamberBuffer == null || !chamberBuffer.IsValid())
        {
            chamberBuffer = new ComputeBuffer(1, sizeof(float) * 3);
            chamberBuffer.SetData(new Vector3[] { Vector3.zero });
        }
    }
    
    Vector2[] CalculateUVs(Vector3[] vertices)
    {
        Vector2[] uvs = new Vector2[vertices.Length];
        
        for (int i = 0; i < vertices.Length; i++)
        {
            // Simple planar mapping - can be improved with triplanar
            uvs[i] = new Vector2(vertices[i].x * 0.1f, vertices[i].z * 0.1f);
        }
        
        return uvs;
    }
    
    int[] GenerateSequentialIndices(int count)
    {
        int[] indices = new int[count];
        for (int i = 0; i < count; i++)
        {
            indices[i] = i;
        }
        return indices;
    }
    
    public void GenerateNoisePreview(float height = 0f)
    {
        if (!isInitialized || noisePreviewTexture == null)
            return;
        
        UpdateNoiseLayerBuffer();
        
        noiseGenerationShader.SetTexture(generateNoisePreviewKernel, "PreviewTexture", noisePreviewTexture);
        noiseGenerationShader.SetBuffer(generateNoisePreviewKernel, "Layers", noiseLayerBuffer);
        noiseGenerationShader.SetInt("LayerCount", worldManager.noiseLayerStack.layers.Count);
        noiseGenerationShader.SetFloat("PreviewHeight", height);
        
        int threadGroupsX = Mathf.CeilToInt(256 / 8f);
        int threadGroupsY = Mathf.CeilToInt(256 / 8f);
        noiseGenerationShader.Dispatch(generateNoisePreviewKernel, threadGroupsX, threadGroupsY, 1);
    }
    
    void OnDestroy()
    {
        // Release all buffers
        noiseLayerBuffer?.Release();
        densityBuffer?.Release();
        chamberBuffer?.Release();
        vertexBuffer?.Release();
        normalBuffer?.Release();
        triangleBuffer?.Release();
        counterBuffer?.Release();
        edgeTableBuffer?.Release();
        triTableBuffer?.Release();
        
        if (noisePreviewTexture != null)
        {
            noisePreviewTexture.Release();
        }
    }
    
    void OnGUI()
    {
        if (showNoisePreview && noisePreviewTexture != null)
        {
            GUI.DrawTexture(new Rect(10, 10, 256, 256), noisePreviewTexture);
        }
    }
    
    // Performance testing methods
    [ContextMenu("Test Pure GPU Speed")]
    public void TestPureGPUSpeed()
    {
        if (!isInitialized)
        {
            Debug.LogError("GPU Manager not initialized!");
            return;
        }
        
        UpdateNoiseLayerBuffer();
        
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        // Generate 10 chunks on GPU without any readback
        for (int i = 0; i < 10; i++)
        {
            Vector3 chunkOrigin = new Vector3(i * CHUNK_SIZE * VOXEL_SIZE, 0, 0);
            GenerateChunkOnGPU(chunkOrigin, 0);
        }
        
        // Force GPU to finish by reading a single value
        GL.Flush();
        int[] dummy = new int[1];
        counterBuffer.GetData(dummy, 0, 0, 1);
        
        sw.Stop();
        Debug.Log($"Pure GPU time for 10 chunks: {sw.ElapsedMilliseconds}ms = {sw.ElapsedMilliseconds/10f}ms per chunk");
    }
    
    [ContextMenu("Test With Async Readback")]
    public void TestAsyncReadback()
    {
        StartCoroutine(TestAsyncReadbackCoroutine());
    }
    
    IEnumerator TestAsyncReadbackCoroutine()
    {
        if (!isInitialized)
        {
            Debug.LogError("GPU Manager not initialized!");
            yield break;
        }
        
        UpdateNoiseLayerBuffer();
        
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int completedChunks = 0;
        
        // Start 10 async requests
        for (int i = 0; i < 10; i++)
        {
            Vector3 chunkOrigin = new Vector3(i * CHUNK_SIZE * VOXEL_SIZE, 0, 0);
            
            // Generate on GPU
            GenerateChunkOnGPU(chunkOrigin, 0);
            
            // Async readback
            AsyncGPUReadback.Request(counterBuffer, (request) =>
            {
                if (!request.hasError)
                {
                    var data = request.GetData<int>();
                    int vertexCount = data[0];
                    Debug.Log($"Async chunk completed with {vertexCount} vertices");
                    completedChunks++;
                }
            });
        }
        
        // Wait for all to complete
        while (completedChunks < 10)
        {
            yield return null;
        }
        
        sw.Stop();
        Debug.Log($"Async GPU time for 10 chunks: {sw.ElapsedMilliseconds}ms = {sw.ElapsedMilliseconds/10f}ms per chunk");
    }
    
    [ContextMenu("Benchmark Performance")]
    public void BenchmarkPerformance()
    {
        Debug.Log("Starting GPU benchmark...");
        float startTime = Time.realtimeSinceStartup;
        
        for (int i = 0; i < 10; i++)
        {
            RequestChunk(new GPUChunkRequest
            {
                coordinate = new Vector3Int(i * 2, 0, 0),
                lodLevel = 0,
                onComplete = (data) => 
                {
                    float elapsed = Time.realtimeSinceStartup - startTime;
                    Debug.Log($"Chunk completed in {elapsed*1000:F1}ms with {data.vertices.Length} vertices");
                }
            });
        }
    }
}