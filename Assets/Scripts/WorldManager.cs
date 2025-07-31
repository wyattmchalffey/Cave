using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public partial class WorldManager : MonoBehaviour
{
    [Header("World Settings")]
    public Transform player;
    public int viewDistance = 5;
    public int lodDistance = 8;
    public int unloadDistance = 2;
    public int chunksPerFrame = 2;
    
    [Header("Cave Settings")]
    public CaveSettings caveSettings = CaveSettings.Default();
    public Material caveMaterial;
    
    [Header("Optimization")]
    public bool useJobSystem = true;
    public int maxConcurrentJobs = 4;
    public bool enableLOD = true;
    
    [Header("Preprocessing")]
    public bool generateNetworkOnStart = true;
    public CaveNetworkPreprocessor networkPreprocessor;
    
    [Header("GPU Acceleration")]
    public bool useGPUGeneration = false;
    public GPUChunkManager gpuChunkManager;
    public ComputeShader caveGenerationShader;
    public ComputeShader meshGenerationShader;
    
    [Header("Debug")]
    public bool showDebugInfo = false;
    public int totalChunksLoaded = 0;
    public int totalVertices = 0;
    
    // Chunk management
    private Dictionary<Vector3Int, Chunk> activeChunks = new Dictionary<Vector3Int, Chunk>();
    private Queue<ChunkGenerationRequest> chunkGenerationQueue = new Queue<ChunkGenerationRequest>();
    private Stack<Chunk> chunkPool = new Stack<Chunk>();
    private List<ChunkJobData> activeJobs = new List<ChunkJobData>();
    
    // Cave network data
    private NativeArray<float3> chamberCenters;
    private List<CaveNetworkPreprocessor.TunnelData> tunnelNetwork;
    
    // Performance tracking
    private float lastChunkUpdate = 0f;
    private const float CHUNK_UPDATE_INTERVAL = 0.1f;
    
    private struct ChunkGenerationRequest
    {
        public Vector3Int coordinate;
        public int lodLevel;
    }
    
    private struct ChunkJobData
    {
        public Vector3Int coordinate;
        public JobHandle jobHandle;
        public NativeArray<float> voxelData;
        public Chunk chunk;
        public int lodLevel;
        // Tunnel data arrays for disposal
        public NativeArray<float3> allTunnelPoints;
        public NativeArray<int> tunnelPointCounts;
        public NativeArray<int> tunnelStartIndices;
        public NativeArray<float> tunnelRadii;
    }
    
    void Start()
    {
        InitializeCaveSettings();
        
        if (generateNetworkOnStart)
        {
            GenerateCaveNetwork();
        }
        
        PrewarmChunkPool(50);
        
        if (useGPUGeneration)
        {
            InitializeGPU();
        }
    }
    
    void InitializeCaveSettings()
    {
        UnityEngine.Random.InitState(caveSettings.seed);
        caveSettings.noiseOffset = new float3(
            UnityEngine.Random.Range(-1000f, 1000f),
            UnityEngine.Random.Range(-1000f, 1000f),
            UnityEngine.Random.Range(-1000f, 1000f)
        );
    }
    
    void GenerateCaveNetwork()
    {
        if (networkPreprocessor == null)
        {
            networkPreprocessor = gameObject.AddComponent<CaveNetworkPreprocessor>();
        }
        
        networkPreprocessor.GenerateCaveNetwork(caveSettings);
        chamberCenters = networkPreprocessor.chamberCenters;
        tunnelNetwork = networkPreprocessor.tunnelNetwork;
        
        UnityEngine.Debug.Log($"Generated cave network with {chamberCenters.Length} chambers and {tunnelNetwork.Count} tunnels");
    }
    
    void PrewarmChunkPool(int count)
    {
        for (int i = 0; i < count; i++)
        {
            CreatePooledChunk();
        }
    }
    
    void Update()
    {
        ProcessCompletedJobs();
        
        if (Time.time - lastChunkUpdate > CHUNK_UPDATE_INTERVAL)
        {
            UpdateChunksAroundPlayer();
            lastChunkUpdate = Time.time;
        }
        
        ProcessChunkQueue();
        
        if (showDebugInfo)
        {
            UpdateDebugInfo();
        }
    }
    
    void UpdateChunksAroundPlayer()
    {
        if (player == null) return;
        
        Vector3Int playerChunkCoord = WorldToChunkCoordinate(player.position);
        
        // Queue chunks that need to be loaded
        for (int x = -lodDistance; x <= lodDistance; x++)
        {
            for (int y = -viewDistance; y <= viewDistance; y++)
            {
                for (int z = -lodDistance; z <= lodDistance; z++)
                {
                    Vector3Int chunkCoord = playerChunkCoord + new Vector3Int(x, y, z);
                    float distance = Vector3.Distance(
                        new Vector3(x, y, z), 
                        Vector3.zero
                    );
                    
                    // Skip if too far
                    if (distance > lodDistance) continue;
                    
                    // Skip if already loaded or queued
                    if (activeChunks.ContainsKey(chunkCoord) || IsChunkQueued(chunkCoord))
                        continue;
                    
                    // Determine LOD level
                    int lodLevel = 0;
                    if (enableLOD)
                    {
                        if (distance > viewDistance)
                            lodLevel = 1;
                        if (distance > viewDistance * 1.5f)
                            lodLevel = 2;
                    }
                    
                    // Queue for generation
                    chunkGenerationQueue.Enqueue(new ChunkGenerationRequest
                    {
                        coordinate = chunkCoord,
                        lodLevel = lodLevel
                    });
                }
            }
        }
        
        // Unload distant chunks
        List<Vector3Int> chunksToUnload = new List<Vector3Int>();
        foreach (var kvp in activeChunks)
        {
            float distance = Vector3Int.Distance(kvp.Key, playerChunkCoord);
            if (distance > lodDistance + unloadDistance)
            {
                chunksToUnload.Add(kvp.Key);
            }
        }
        
        foreach (var coord in chunksToUnload)
        {
            UnloadChunk(coord);
        }
    }
    
    void ProcessChunkQueue()
    {
        int jobsToStart = Mathf.Min(chunksPerFrame, maxConcurrentJobs - activeJobs.Count);
        
        for (int i = 0; i < jobsToStart && chunkGenerationQueue.Count > 0; i++)
        {
            var request = chunkGenerationQueue.Dequeue();
            
            if (useGPUGeneration && gpuChunkManager != null)
            {
                StartGPUChunkGeneration(request);
            }
            else if (useJobSystem && chamberCenters.IsCreated)
            {
                StartChunkGenerationJob(request);
            }
            else
            {
                GenerateChunkImmediate(request);
            }
        }
    }
    
    void StartChunkGenerationJob(ChunkGenerationRequest request)
    {
        Chunk chunk = GetPooledChunk();
        chunk.Initialize(request.coordinate, caveMaterial, request.lodLevel);
        
        // Calculate voxel count including boundary
        int voxelCount = (Chunk.CHUNK_SIZE + 1) * 
                        (Chunk.CHUNK_SIZE + 1) * 
                        (Chunk.CHUNK_SIZE + 1);
        
        NativeArray<float> voxelData = new NativeArray<float>(voxelCount, Allocator.TempJob);
        
        // Flatten tunnel data to avoid nested containers
        NativeArray<float3> allTunnelPoints;
        NativeArray<int> tunnelPointCounts;
        NativeArray<int> tunnelStartIndices;
        NativeArray<float> tunnelRadii;
        
        if (tunnelNetwork != null && tunnelNetwork.Count > 0)
        {
            // Count total points
            int totalPoints = 0;
            foreach (var tunnel in tunnelNetwork)
            {
                totalPoints += tunnel.pathPoints.Count;
            }
            
            // Allocate arrays
            allTunnelPoints = new NativeArray<float3>(totalPoints, Allocator.TempJob);
            tunnelPointCounts = new NativeArray<int>(tunnelNetwork.Count, Allocator.TempJob);
            tunnelStartIndices = new NativeArray<int>(tunnelNetwork.Count, Allocator.TempJob);
            tunnelRadii = new NativeArray<float>(tunnelNetwork.Count, Allocator.TempJob);
            
            // Fill arrays
            int currentIndex = 0;
            for (int i = 0; i < tunnelNetwork.Count; i++)
            {
                var tunnel = tunnelNetwork[i];
                tunnelStartIndices[i] = currentIndex;
                tunnelPointCounts[i] = tunnel.pathPoints.Count;
                tunnelRadii[i] = tunnel.radius;
                
                for (int j = 0; j < tunnel.pathPoints.Count; j++)
                {
                    allTunnelPoints[currentIndex++] = tunnel.pathPoints[j];
                }
            }
        }
        else
        {
            // Empty arrays
            allTunnelPoints = new NativeArray<float3>(0, Allocator.TempJob);
            tunnelPointCounts = new NativeArray<int>(0, Allocator.TempJob);
            tunnelStartIndices = new NativeArray<int>(0, Allocator.TempJob);
            tunnelRadii = new NativeArray<float>(0, Allocator.TempJob);
        }
        
        // Create and schedule job
        var job = new CaveGenerationJob
        {
            chunkWorldPosition = new float3(
                request.coordinate.x * Chunk.CHUNK_SIZE * Chunk.VOXEL_SIZE,
                request.coordinate.y * Chunk.CHUNK_SIZE * Chunk.VOXEL_SIZE,
                request.coordinate.z * Chunk.CHUNK_SIZE * Chunk.VOXEL_SIZE
            ),
            chunkSize = Chunk.CHUNK_SIZE,
            voxelSize = Chunk.VOXEL_SIZE * (1 << request.lodLevel), // Larger voxels for LOD
            settings = caveSettings,
            chamberCenters = chamberCenters.IsCreated ? chamberCenters : new NativeArray<float3>(0, Allocator.TempJob),
            allTunnelPoints = allTunnelPoints,
            tunnelPointCounts = tunnelPointCounts,
            tunnelStartIndices = tunnelStartIndices,
            tunnelRadii = tunnelRadii,
            voxelData = voxelData
        };
        
        JobHandle handle = job.Schedule(voxelCount, 64);
        
        activeJobs.Add(new ChunkJobData
        {
            coordinate = request.coordinate,
            jobHandle = handle,
            voxelData = voxelData,
            chunk = chunk,
            lodLevel = request.lodLevel,
            // Store tunnel arrays for disposal
            allTunnelPoints = allTunnelPoints,
            tunnelPointCounts = tunnelPointCounts,
            tunnelStartIndices = tunnelStartIndices,
            tunnelRadii = tunnelRadii
        });
    }
    
    void ProcessCompletedJobs()
    {
        for (int i = activeJobs.Count - 1; i >= 0; i--)
        {
            var jobData = activeJobs[i];
            
            if (jobData.jobHandle.IsCompleted)
            {
                jobData.jobHandle.Complete();
                
                // Remove old chunk if updating LOD
                if (activeChunks.ContainsKey(jobData.coordinate))
                {
                    UnloadChunk(jobData.coordinate);
                }
                
                jobData.chunk.SetVoxelDataFromJob(jobData.voxelData);
                jobData.chunk.GenerateSmoothMesh();
                
                // Dispose all native arrays
                jobData.voxelData.Dispose();
                if (jobData.allTunnelPoints.IsCreated) jobData.allTunnelPoints.Dispose();
                if (jobData.tunnelPointCounts.IsCreated) jobData.tunnelPointCounts.Dispose();
                if (jobData.tunnelStartIndices.IsCreated) jobData.tunnelStartIndices.Dispose();
                if (jobData.tunnelRadii.IsCreated) jobData.tunnelRadii.Dispose();
                
                activeChunks[jobData.coordinate] = jobData.chunk;
                activeJobs.RemoveAt(i);
            }
        }
    }
    
    void GenerateChunkImmediate(ChunkGenerationRequest request)
    {
        Chunk chunk = GetPooledChunk();
        chunk.Initialize(request.coordinate, caveMaterial, request.lodLevel);
        
        // For immediate generation, we'd need a non-job version
        // This is simplified for the example
        chunk.GenerateSmoothMesh();
        
        activeChunks[request.coordinate] = chunk;
    }
    
    void UnloadChunk(Vector3Int coordinate)
    {
        if (activeChunks.TryGetValue(coordinate, out Chunk chunk))
        {
            chunk.gameObject.SetActive(false);
            chunkPool.Push(chunk);
            activeChunks.Remove(coordinate);
        }
    }
    
    Chunk GetPooledChunk()
    {
        if (chunkPool.Count > 0)
        {
            var chunk = chunkPool.Pop();
            chunk.gameObject.SetActive(true);
            return chunk;
        }
        
        return CreatePooledChunk();
    }
    
    Chunk CreatePooledChunk()
    {
        GameObject chunkObj = new GameObject("Cave Chunk");
        chunkObj.transform.parent = transform;
        Chunk chunk = chunkObj.AddComponent<Chunk>();
        return chunk;
    }
    
    Vector3Int WorldToChunkCoordinate(Vector3 worldPos)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / (Chunk.CHUNK_SIZE * Chunk.VOXEL_SIZE)),
            Mathf.FloorToInt(worldPos.y / (Chunk.CHUNK_SIZE * Chunk.VOXEL_SIZE)),
            Mathf.FloorToInt(worldPos.z / (Chunk.CHUNK_SIZE * Chunk.VOXEL_SIZE))
        );
    }
    
    bool IsChunkQueued(Vector3Int coord)
    {
        foreach (var request in chunkGenerationQueue)
        {
            if (request.coordinate == coord) return true;
        }
        
        foreach (var job in activeJobs)
        {
            if (job.coordinate == coord) return true;
        }
        
        return false;
    }
    
    void UpdateDebugInfo()
    {
        totalChunksLoaded = activeChunks.Count;
        totalVertices = 0;
        
        foreach (var chunk in activeChunks.Values)
        {
            var meshFilter = chunk.GetComponent<MeshFilter>();
            if (meshFilter && meshFilter.mesh)
            {
                totalVertices += meshFilter.mesh.vertexCount;
            }
        }
    }
    
    // GPU Generation Methods
    void InitializeGPU()
    {
        if (gpuChunkManager == null)
        {
            UnityEngine.Debug.LogError("GPU Chunk Manager not assigned to WorldManager!");
            return;
        }
        
        // Initialize the GPU chunk manager if it has specific initialization needs
        // The GPUChunkManager's Start() method already handles initialization,
        // but you can add any additional setup here if needed
        
        UnityEngine.Debug.Log("GPU acceleration initialized for cave generation");
    }
    
    void StartGPUChunkGeneration(ChunkGenerationRequest request)
    {
        if (!useGPUGeneration || gpuChunkManager == null)
        {
            UnityEngine.Debug.LogWarning("GPU generation requested but not available");
            return;
        }
        
        // Convert the chunk generation request to GPU chunk request format
        GPUChunkManager.GPUChunkRequest gpuRequest = new GPUChunkManager.GPUChunkRequest
        {
            coordinate = request.coordinate,
            lodLevel = request.lodLevel,
            onComplete = (meshData) => OnGPUChunkComplete(request.coordinate, meshData, request.lodLevel)
        };
        
        // Submit the request to the GPU chunk manager
        gpuChunkManager.RequestChunk(gpuRequest);
    }
    
    void OnGPUChunkComplete(Vector3Int coordinate, GPUChunkManager.ChunkMeshData meshData, int lodLevel)
    {
        // Get or create the chunk
        Chunk chunk = GetOrCreateChunk(coordinate, lodLevel);
        
        if (chunk != null && meshData.vertices.Length > 0)
        {
            // Get the mesh filter component
            MeshFilter chunkMeshFilter = chunk.GetComponent<MeshFilter>();
            if (chunkMeshFilter != null)
            {
                // Create or get mesh
                if (chunkMeshFilter.mesh == null)
                {
                    chunkMeshFilter.mesh = new Mesh();
                    chunkMeshFilter.mesh.name = $"Chunk_{coordinate}";
                }
                
                // Apply the mesh data to the chunk
                chunkMeshFilter.mesh.Clear();
                chunkMeshFilter.mesh.vertices = meshData.vertices;
                chunkMeshFilter.mesh.triangles = meshData.triangles;
                chunkMeshFilter.mesh.normals = meshData.normals;
                chunkMeshFilter.mesh.uv = meshData.uvs;
                chunkMeshFilter.mesh.RecalculateBounds();
            }
            
            // Update statistics
            totalVertices += meshData.vertices.Length;
        }
    }
    
    Chunk GetOrCreateChunk(Vector3Int coordinate, int lodLevel)
    {
        if (activeChunks.TryGetValue(coordinate, out Chunk existingChunk))
        {
            return existingChunk;
        }
        
        // Get chunk from pool or create new
        Chunk chunk = GetPooledChunk();
        chunk.Initialize(coordinate, caveMaterial, lodLevel);
        activeChunks[coordinate] = chunk;
        
        return chunk;
    }
    
    void OnDestroy()
    {
        foreach (var job in activeJobs)
        {
            job.jobHandle.Complete();
            job.voxelData.Dispose();
            if (job.allTunnelPoints.IsCreated) job.allTunnelPoints.Dispose();
            if (job.tunnelPointCounts.IsCreated) job.tunnelPointCounts.Dispose();
            if (job.tunnelStartIndices.IsCreated) job.tunnelStartIndices.Dispose();
            if (job.tunnelRadii.IsCreated) job.tunnelRadii.Dispose();
        }
        
        if (chamberCenters.IsCreated)
            chamberCenters.Dispose();
    }
    
    void OnDrawGizmosSelected()
    {
        if (!showDebugInfo || player == null) return;
        
        Vector3Int playerChunk = WorldToChunkCoordinate(player.position);
        float chunkWorldSize = Chunk.CHUNK_SIZE * Chunk.VOXEL_SIZE;
        
        Vector3 center = new Vector3(
            playerChunk.x * chunkWorldSize + chunkWorldSize / 2f,
            playerChunk.y * chunkWorldSize + chunkWorldSize / 2f,
            playerChunk.z * chunkWorldSize + chunkWorldSize / 2f
        );
        
        // Draw view distance (green)
        Gizmos.color = Color.green;
        Gizmos.DrawWireCube(center, Vector3.one * chunkWorldSize * (viewDistance * 2 + 1));
        
        // Draw LOD distance (yellow)
        if (enableLOD)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(center, Vector3.one * chunkWorldSize * (lodDistance * 2 + 1));
        }
        
        // Draw unload distance (red)
        Gizmos.color = Color.red;
        Gizmos.DrawWireCube(center, Vector3.one * chunkWorldSize * ((lodDistance + unloadDistance) * 2 + 1));
        
        // Draw chamber centers if available
        if (chamberCenters.IsCreated)
        {
            Gizmos.color = Color.cyan;
            foreach (var chamber in chamberCenters)
            {
                Gizmos.DrawWireSphere(chamber, 2f);
            }
        }
    }
}