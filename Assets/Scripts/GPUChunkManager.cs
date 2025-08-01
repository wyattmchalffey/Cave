using System.Collections;
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
    public CaveSettings caveSettings = CaveSettings.Default();
    public Material caveMaterial;
    
    [Header("Debug")]
    public bool useDebugKernel = false;
    public bool logDensityStats = true;
    
    private ComputeBuffer densityBuffer;
    private ComputeBuffer chamberBuffer;
    private ComputeBuffer vertexBuffer;
    private ComputeBuffer triangleBuffer;
    private ComputeBuffer counterBuffer;
    
    private int generateDensityKernel = -1;
    private int generateChamberKernel = -1;
    private int generateTunnelKernel = -1;
    private int debugKernel = -1;
    
    private const int CHUNK_SIZE = 32;
    private const float VOXEL_SIZE = 0.25f;
    
    private bool isInitialized = false;
    
    // Marching cubes tables
    private ComputeBuffer edgeTableBuffer;
    private ComputeBuffer triTableBuffer;
    
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
        if (!InitializeComputeShaders())
        {
            enabled = false;
            return;
        }
        InitializeBuffers();
        InitializeMarchingCubesTables();
        isInitialized = true;
    }
    
    bool InitializeComputeShaders()
    {
        if (caveGenerationShader == null)
        {
            UnityEngine.Debug.LogError("Cave generation compute shader not assigned to GPUChunkManager!");
            return false;
        }
        
        // Find kernels with error checking
        try
        {
            generateDensityKernel = caveGenerationShader.FindKernel("GenerateDensityField");
            generateChamberKernel = caveGenerationShader.FindKernel("GenerateChamberField");
            generateTunnelKernel = caveGenerationShader.FindKernel("GenerateTunnelField");
            
            // Try to find debug kernel (optional)
            try
            {
                debugKernel = caveGenerationShader.FindKernel("DebugDensityField");
            }
            catch
            {
                UnityEngine.Debug.LogWarning("Debug kernel not found in compute shader");
                debugKernel = -1;
            }
            
            UnityEngine.Debug.Log($"Found kernels: Density={generateDensityKernel}, Chamber={generateChamberKernel}, Tunnel={generateTunnelKernel}, Debug={debugKernel}");
        }
        catch (System.Exception e)
        {
            UnityEngine.Debug.LogError($"Failed to find compute shader kernels: {e.Message}");
            return false;
        }
        
        // Validate kernels
        if (generateDensityKernel < 0)
        {
            UnityEngine.Debug.LogError("GenerateDensityField kernel not found in compute shader!");
            return false;
        }
        
        return true;
    }
    
    void InitializeBuffers()
    {
        // Density field buffer
        int voxelCount = (CHUNK_SIZE + 1) * (CHUNK_SIZE + 1) * (CHUNK_SIZE + 1);
        densityBuffer = new ComputeBuffer(voxelCount, sizeof(float));
        
        // Vertex and triangle buffers for mesh generation
        int maxVertices = voxelCount * 4;
        int maxTriangles = voxelCount * 6;
        
        vertexBuffer = new ComputeBuffer(maxVertices, sizeof(float) * 3);
        triangleBuffer = new ComputeBuffer(maxTriangles, sizeof(int));
        counterBuffer = new ComputeBuffer(2, sizeof(uint), ComputeBufferType.Counter);
        
        UnityEngine.Debug.Log($"Initialized GPU buffers: voxelCount={voxelCount}");
    }

    void InitializeMarchingCubesTables()
    {
        edgeTableBuffer = new ComputeBuffer(256, sizeof(int));
        edgeTableBuffer.SetData(MarchingCubesTables.EdgeTable);

        triTableBuffer = new ComputeBuffer(256 * 16, sizeof(int));
        triTableBuffer.SetData(MarchingCubesTables.TriTable);
    }

    public void RequestChunk(GPUChunkRequest request)
    {
        if (!isInitialized)
        {
            UnityEngine.Debug.LogError("GPUChunkManager not initialized!");
            return;
        }
        
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
    
    IEnumerator GenerateChunkGPU(GPUChunkRequest request)
    {
        // Validate shader and kernel before use
        if (caveGenerationShader == null || generateDensityKernel < 0)
        {
            UnityEngine.Debug.LogError("Compute shader or kernel not properly initialized!");
            request.onComplete?.Invoke(CreateEmptyMeshData());
            ProcessNextRequest();
            yield break;
        }
        
        // Calculate chunk origin
        Vector3 chunkOrigin = new Vector3(
            request.coordinate.x * CHUNK_SIZE * VOXEL_SIZE,
            request.coordinate.y * CHUNK_SIZE * VOXEL_SIZE,
            request.coordinate.z * CHUNK_SIZE * VOXEL_SIZE
        );
        
        UnityEngine.Debug.Log($"Generating chunk at: {chunkOrigin} (coord: {request.coordinate})");
        
        // Set shader parameters
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
        int kernelToUse = useDebugKernel && debugKernel >= 0 ? debugKernel : generateDensityKernel;
        caveGenerationShader.SetBuffer(kernelToUse, "DensityField", densityBuffer);
        
        // Set chamber data if available
        SetChamberData(kernelToUse);
        
        // Dispatch density generation
        int threadGroups = Mathf.CeilToInt((CHUNK_SIZE + 1) / 8.0f);
        caveGenerationShader.Dispatch(kernelToUse, threadGroups, threadGroups, threadGroups);
        
        // Wait for GPU
        yield return new WaitForEndOfFrame();
        
        // Read back density data
        float[] densityData = new float[(CHUNK_SIZE + 1) * (CHUNK_SIZE + 1) * (CHUNK_SIZE + 1)];
        densityBuffer.GetData(densityData);
        
        // Generate mesh
        ChunkMeshData meshData = GenerateMeshFromDensity(densityData, request.lodLevel, request.coordinate);
        
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
    
    void SetChamberData(int kernel)
    {
        var worldManager = GetComponent<WorldManager>();
        if (worldManager != null && worldManager.networkPreprocessor != null)
        {
            var networkPreprocessor = worldManager.networkPreprocessor;
            if (networkPreprocessor.chamberCenters != null && 
                networkPreprocessor.chamberCenters.IsCreated && 
                networkPreprocessor.chamberCenters.Length > 0)
            {
                chamberBuffer = new ComputeBuffer(networkPreprocessor.chamberCenters.Length, sizeof(float) * 3);
                chamberBuffer.SetData(networkPreprocessor.chamberCenters.ToArray());
                
                caveGenerationShader.SetBuffer(kernel, "ChamberCenters", chamberBuffer);
                caveGenerationShader.SetInt("ChamberCount", networkPreprocessor.chamberCenters.Length);
            }
            else
            {
                caveGenerationShader.SetInt("ChamberCount", 0);
            }
        }
        else
        {
            caveGenerationShader.SetInt("ChamberCount", 0);
        }
    }
    
    ChunkMeshData GenerateMeshFromDensity(float[] densityData, int lodLevel, Vector3Int coordinate)
    {
        // Debug density values
        if (logDensityStats)
        {
            LogDensityStats(densityData, coordinate);
        }
        
        // Use marching cubes to generate mesh
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        
        float isoLevel = 0.5f;
        int step = 1 << lodLevel;
        
        for (int x = 0; x < CHUNK_SIZE; x += step)
        {
            for (int y = 0; y < CHUNK_SIZE; y += step)
            {
                for (int z = 0; z < CHUNK_SIZE; z += step)
                {
                    ProcessVoxel(x, y, z, step, densityData, isoLevel, vertices, triangles);
                }
            }
        }
        
        if (vertices.Count == 0)
        {
            return CreateEmptyMeshData();
        }
        
        // Calculate normals
        Vector3[] normalsArray = CalculateNormals(vertices, triangles);
        
        // Generate UVs
        Vector2[] uvs = new Vector2[vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            uvs[i] = new Vector2(vertices[i].x * 0.1f, vertices[i].z * 0.1f);
        }
        
        UnityEngine.Debug.Log($"Chunk {coordinate}: Generated {vertices.Count} vertices, {triangles.Count / 3} triangles");
        
        return new ChunkMeshData
        {
            vertices = vertices.ToArray(),
            triangles = triangles.ToArray(),
            normals = normalsArray,
            uvs = uvs
        };
    }

    void ProcessVoxel(int x, int y, int z, int step, float[] densityData, float isoLevel,
                      List<Vector3> vertices, List<int> triangles)
    {
        // Get density values at cube corners
        float[] cube = new float[8];
        cube[0] = GetDensity(densityData, x, y, z);
        cube[1] = GetDensity(densityData, x + step, y, z);
        cube[2] = GetDensity(densityData, x + step, y + step, z);
        cube[3] = GetDensity(densityData, x, y + step, z);
        cube[4] = GetDensity(densityData, x, y, z + step);
        cube[5] = GetDensity(densityData, x + step, y, z + step);
        cube[6] = GetDensity(densityData, x + step, y + step, z + step);
        cube[7] = GetDensity(densityData, x, y + step, z + step);

        // Determine cube configuration
        int cubeIndex = 0;
        if (cube[0] < isoLevel) cubeIndex |= 1;
        if (cube[1] < isoLevel) cubeIndex |= 2;
        if (cube[2] < isoLevel) cubeIndex |= 4;
        if (cube[3] < isoLevel) cubeIndex |= 8;
        if (cube[4] < isoLevel) cubeIndex |= 16;
        if (cube[5] < isoLevel) cubeIndex |= 32;
        if (cube[6] < isoLevel) cubeIndex |= 64;
        if (cube[7] < isoLevel) cubeIndex |= 128;

        // Skip if cube is entirely inside or outside
        if (cubeIndex == 0 || cubeIndex == 255)
            return;

        // Use the edge table to find which edges are intersected
        if (MarchingCubesTables.EdgeTable[cubeIndex] == 0)
            return;

        // Find the vertices where the surface intersects the cube edges
        Vector3[] vertList = new Vector3[12];
        float size = step * VOXEL_SIZE;

        if ((MarchingCubesTables.EdgeTable[cubeIndex] & 1) != 0)
            vertList[0] = VertexInterp(isoLevel,
                new Vector3(x, y, z) * VOXEL_SIZE,
                new Vector3(x + step, y, z) * VOXEL_SIZE,
                cube[0], cube[1]);

        if ((MarchingCubesTables.EdgeTable[cubeIndex] & 2) != 0)
            vertList[1] = VertexInterp(isoLevel,
                new Vector3(x + step, y, z) * VOXEL_SIZE,
                new Vector3(x + step, y + step, z) * VOXEL_SIZE,
                cube[1], cube[2]);

        if ((MarchingCubesTables.EdgeTable[cubeIndex] & 4) != 0)
            vertList[2] = VertexInterp(isoLevel,
                new Vector3(x + step, y + step, z) * VOXEL_SIZE,
                new Vector3(x, y + step, z) * VOXEL_SIZE,
                cube[2], cube[3]);

        if ((MarchingCubesTables.EdgeTable[cubeIndex] & 8) != 0)
            vertList[3] = VertexInterp(isoLevel,
                new Vector3(x, y + step, z) * VOXEL_SIZE,
                new Vector3(x, y, z) * VOXEL_SIZE,
                cube[3], cube[0]);

        if ((MarchingCubesTables.EdgeTable[cubeIndex] & 16) != 0)
            vertList[4] = VertexInterp(isoLevel,
                new Vector3(x, y, z + step) * VOXEL_SIZE,
                new Vector3(x + step, y, z + step) * VOXEL_SIZE,
                cube[4], cube[5]);

        if ((MarchingCubesTables.EdgeTable[cubeIndex] & 32) != 0)
            vertList[5] = VertexInterp(isoLevel,
                new Vector3(x + step, y, z + step) * VOXEL_SIZE,
                new Vector3(x + step, y + step, z + step) * VOXEL_SIZE,
                cube[5], cube[6]);

        if ((MarchingCubesTables.EdgeTable[cubeIndex] & 64) != 0)
            vertList[6] = VertexInterp(isoLevel,
                new Vector3(x + step, y + step, z + step) * VOXEL_SIZE,
                new Vector3(x, y + step, z + step) * VOXEL_SIZE,
                cube[6], cube[7]);

        if ((MarchingCubesTables.EdgeTable[cubeIndex] & 128) != 0)
            vertList[7] = VertexInterp(isoLevel,
                new Vector3(x, y + step, z + step) * VOXEL_SIZE,
                new Vector3(x, y, z + step) * VOXEL_SIZE,
                cube[7], cube[4]);

        if ((MarchingCubesTables.EdgeTable[cubeIndex] & 256) != 0)
            vertList[8] = VertexInterp(isoLevel,
                new Vector3(x, y, z) * VOXEL_SIZE,
                new Vector3(x, y, z + step) * VOXEL_SIZE,
                cube[0], cube[4]);

        if ((MarchingCubesTables.EdgeTable[cubeIndex] & 512) != 0)
            vertList[9] = VertexInterp(isoLevel,
                new Vector3(x + step, y, z) * VOXEL_SIZE,
                new Vector3(x + step, y, z + step) * VOXEL_SIZE,
                cube[1], cube[5]);

        if ((MarchingCubesTables.EdgeTable[cubeIndex] & 1024) != 0)
            vertList[10] = VertexInterp(isoLevel,
                new Vector3(x + step, y + step, z) * VOXEL_SIZE,
                new Vector3(x + step, y + step, z + step) * VOXEL_SIZE,
                cube[2], cube[6]);

        if ((MarchingCubesTables.EdgeTable[cubeIndex] & 2048) != 0)
            vertList[11] = VertexInterp(isoLevel,
                new Vector3(x, y + step, z) * VOXEL_SIZE,
                new Vector3(x, y + step, z + step) * VOXEL_SIZE,
                cube[3], cube[7]);

        // Create triangles
        for (int i = 0; MarchingCubesTables.TriTable[cubeIndex * 16 + i] != -1; i += 3)
        {
            int baseIndex = vertices.Count;

            vertices.Add(vertList[MarchingCubesTables.TriTable[cubeIndex * 16 + i]]);
            vertices.Add(vertList[MarchingCubesTables.TriTable[cubeIndex * 16 + i + 1]]);
            vertices.Add(vertList[MarchingCubesTables.TriTable[cubeIndex * 16 + i + 2]]);

            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);
        }
    }

    void AddSimpleCube(List<Vector3> vertices, List<int> triangles, Vector3 position, float size)
    {
        int startIndex = vertices.Count;
        
        // Add vertices (8 corners)
        vertices.Add(position + new Vector3(0, 0, 0));
        vertices.Add(position + new Vector3(size, 0, 0));
        vertices.Add(position + new Vector3(size, size, 0));
        vertices.Add(position + new Vector3(0, size, 0));
        vertices.Add(position + new Vector3(0, 0, size));
        vertices.Add(position + new Vector3(size, 0, size));
        vertices.Add(position + new Vector3(size, size, size));
        vertices.Add(position + new Vector3(0, size, size));
        
        // Add triangles (12 triangles, 2 per face)
        int[,] faces = new int[,] {
            {0, 2, 1, 0, 3, 2}, // Front
            {5, 6, 4, 4, 6, 7}, // Back
            {3, 7, 6, 3, 6, 2}, // Top
            {1, 5, 4, 1, 4, 0}, // Bottom
            {1, 2, 6, 1, 6, 5}, // Right
            {4, 7, 3, 4, 3, 0}  // Left
        };
        
        for (int i = 0; i < 6; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                triangles.Add(startIndex + faces[i, j]);
            }
        }
    }
    
    float GetDensity(float[] densityData, int x, int y, int z)
    {
        x = Mathf.Clamp(x, 0, CHUNK_SIZE);
        y = Mathf.Clamp(y, 0, CHUNK_SIZE);
        z = Mathf.Clamp(z, 0, CHUNK_SIZE);
        
        int index = x + y * (CHUNK_SIZE + 1) + z * (CHUNK_SIZE + 1) * (CHUNK_SIZE + 1);
        return densityData[index];
    }
    
    Vector3[] CalculateNormals(List<Vector3> vertices, List<int> triangles)
    {
        Vector3[] normals = new Vector3[vertices.Count];
        
        // Calculate face normals
        for (int i = 0; i < triangles.Count; i += 3)
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
    
    void LogDensityStats(float[] densityData, Vector3Int coordinate)
    {
        float minDensity = float.MaxValue;
        float maxDensity = float.MinValue;
        int solidCount = 0;
        int airCount = 0;
        int mixedCount = 0;
        
        for (int i = 0; i < densityData.Length; i++)
        {
            float density = densityData[i];
            minDensity = Mathf.Min(minDensity, density);
            maxDensity = Mathf.Max(maxDensity, density);
            
            if (density > 0.9f) solidCount++;
            else if (density < 0.1f) airCount++;
            else mixedCount++;
        }
        
        UnityEngine.Debug.Log($"Chunk {coordinate} density: Min={minDensity:F3}, Max={maxDensity:F3}, " +
                            $"Solid={solidCount}, Air={airCount}, Mixed={mixedCount}");
        
        if (Mathf.Approximately(minDensity, maxDensity))
        {
            UnityEngine.Debug.LogWarning($"Chunk {coordinate}: All density values are {minDensity}!");
        }
    }
    
    ChunkMeshData CreateEmptyMeshData()
    {
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
        // Release all buffers
        densityBuffer?.Release();
        vertexBuffer?.Release();
        triangleBuffer?.Release();
        chamberBuffer?.Release();
        counterBuffer?.Release();
        edgeTableBuffer?.Release();
        triTableBuffer?.Release();
    }

    Vector3 VertexInterp(float isoLevel, Vector3 p1, Vector3 p2, float v1, float v2)
    {
        if (Mathf.Abs(isoLevel - v1) < 0.00001f)
            return p1;
        if (Mathf.Abs(isoLevel - v2) < 0.00001f)
            return p2;
        if (Mathf.Abs(v1 - v2) < 0.00001f)
            return p1;

        float t = (isoLevel - v1) / (v2 - v1);
        return p1 + t * (p2 - p1);
    }
}