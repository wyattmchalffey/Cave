using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public class WorldManager : MonoBehaviour
{
    [Header("Chunk Management")]
    public GameObject chunkPrefab;
    public int renderDistance = 5;
    public int chunkSize = 32;
    public float voxelSize = 0.25f;
    
    [Header("Cave Settings")]
    public CaveSettings caveSettings = CaveSettings.Default();
    
    [Header("Noise Layer System")]
    public NoiseLayerStack noiseLayerStack = new NoiseLayerStack();
    
    [Header("Optimization")]
    public bool useJobSystem = true;
    public bool useGPUGeneration = false;
    public int maxConcurrentJobs = 4;
    
    [Header("Debug")]
    public bool showDebugInfo = false;
    public int totalChunksLoaded = 0;
    public int totalVertices = 0;
    
    // Components
    private GPUChunkManager gpuChunkManager;
    
    // Chunk management
    public Dictionary<Vector3Int, Chunk> chunks { get; private set; } = new Dictionary<Vector3Int, Chunk>();
    public Queue<Vector3Int> chunkGenerationQueue { get; private set; } = new Queue<Vector3Int>();
    private List<JobHandle> activeJobs = new List<JobHandle>();
    private Dictionary<Vector3Int, Coroutine> chunkCoroutines = new Dictionary<Vector3Int, Coroutine>();
    
    // Player reference
    private Transform player;
    
    void Start()
    {
        // Find player
        player = GameObject.FindGameObjectWithTag("Player")?.transform;
        if (player == null)
        {
            Debug.LogWarning("No player found! Tag your player GameObject with 'Player'");
        }
        
        if (useGPUGeneration)
        {
            gpuChunkManager = GetComponent<GPUChunkManager>();
            if (gpuChunkManager == null)
            {
                gpuChunkManager = gameObject.AddComponent<GPUChunkManager>();
            }
        }
        
        // Generate initial chunks
        UpdateChunks();
    }
    
    void Update()
    {
        if (player != null)
        {
            UpdateChunks();
        }
        
        // Process generation queue
        ProcessGenerationQueue();
        
        // Update debug info
        if (showDebugInfo)
        {
            UpdateDebugInfo();
        }
    }
    
    public void UpdateChunks()
    {
        Vector3Int playerChunk = WorldToChunkCoord(player.position);
        
        // Check which chunks should exist
        HashSet<Vector3Int> chunksToKeep = new HashSet<Vector3Int>();
        
        for (int x = -renderDistance; x <= renderDistance; x++)
        {
            for (int y = -renderDistance; y <= renderDistance; y++)
            {
                for (int z = -renderDistance; z <= renderDistance; z++)
                {
                    Vector3Int chunkCoord = playerChunk + new Vector3Int(x, y, z);
                    float distance = Vector3.Distance(player.position, ChunkToWorldPos(chunkCoord));
                    
                    if (distance <= renderDistance * chunkSize * voxelSize)
                    {
                        chunksToKeep.Add(chunkCoord);
                        
                        // Generate chunk if it doesn't exist
                        if (!chunks.ContainsKey(chunkCoord) && !chunkGenerationQueue.Contains(chunkCoord))
                        {
                            chunkGenerationQueue.Enqueue(chunkCoord);
                        }
                    }
                }
            }
        }
        
        // Remove distant chunks
        List<Vector3Int> chunksToRemove = new List<Vector3Int>();
        foreach (var kvp in chunks)
        {
            if (!chunksToKeep.Contains(kvp.Key))
            {
                chunksToRemove.Add(kvp.Key);
            }
        }
        
        foreach (var coord in chunksToRemove)
        {
            RemoveChunk(coord);
        }
    }
    
    void ProcessGenerationQueue()
    {
        // Clean up completed jobs
        activeJobs.RemoveAll(handle => handle.IsCompleted);
        
        // Start new jobs
        while (chunkGenerationQueue.Count > 0 && activeJobs.Count < maxConcurrentJobs)
        {
            Vector3Int chunkCoord = chunkGenerationQueue.Dequeue();
            
            if (useJobSystem && !useGPUGeneration)
            {
                StartChunkGenerationJob(chunkCoord);
            }
            else if (useGPUGeneration && gpuChunkManager != null)
            {
                GenerateChunkGPU(chunkCoord);
            }
            else
            {
                GenerateChunkImmediate(chunkCoord);
            }
        }
    }
    
    void StartChunkGenerationJob(Vector3Int chunkCoord)
    {
        // Create chunk GameObject
        GameObject chunkGO = Instantiate(chunkPrefab, ChunkToWorldPos(chunkCoord), Quaternion.identity);
        Chunk chunk = chunkGO.GetComponent<Chunk>();
        
        if (chunk == null)
        {
            chunk = chunkGO.AddComponent<Chunk>();
        }
        
        chunk.Initialize(chunkCoord, chunkSize, voxelSize);
        chunks[chunkCoord] = chunk;
        
        // Prepare native arrays
        int arraySize = (chunkSize + 1) * (chunkSize + 1) * (chunkSize + 1);
        NativeArray<float> densityField = new NativeArray<float>(arraySize, Allocator.TempJob);
        
        // Create job with noise layer evaluation
        var job = new CaveGenerationJobWithLayers
        {
            chunkCoord = new int3(chunkCoord.x, chunkCoord.y, chunkCoord.z),
            chunkSize = chunkSize,
            voxelSize = voxelSize,
            settings = caveSettings,
            densityField = densityField,
            noiseLayerStack = ConvertNoiseStackToJobData(noiseLayerStack)
        };
        
        // Schedule job
        JobHandle handle = job.Schedule();
        activeJobs.Add(handle);
        
        // Store handle in chunk for completion checking
        chunk.generationHandle = handle;
        chunk.densityField = densityField;
        
        // Start coroutine to check completion and track it
        var coroutine = StartCoroutine(WaitForChunkGeneration(chunk, handle, densityField));
        chunkCoroutines[chunkCoord] = coroutine;
    }
    
    System.Collections.IEnumerator WaitForChunkGeneration(Chunk chunk, JobHandle handle, NativeArray<float> densityData)
    {
        // Store chunk coordinate to check if it still exists
        Vector3Int chunkCoord = chunk.coordinate;
        
        try
        {
            // Wait for job to complete
            while (!handle.IsCompleted)
            {
                yield return null;
                
                // Check if chunk was removed while we were waiting
                if (!chunks.ContainsKey(chunkCoord))
                {
                    // Complete and dispose immediately
                    handle.Complete();
                    if (densityData.IsCreated)
                    {
                        densityData.Dispose();
                    }
                    yield break;
                }
            }
            
            handle.Complete();
            
            // Double-check chunk still exists
            if (!chunks.ContainsKey(chunkCoord) || chunk == null)
            {
                if (densityData.IsCreated)
                {
                    densityData.Dispose();
                }
                yield break;
            }
            
            // Copy density data to chunk
            if (densityData.IsCreated)
            {
                chunk.SetDensityFromNativeArray(densityData);
                
                // Generate mesh
                chunk.GenerateMesh();
                
                // Clean up native array
                densityData.Dispose();
                chunk.densityField = default;
            }
            
            chunk.generationHandle = null;
        }
        finally
        {
            // Ensure cleanup happens even if coroutine is interrupted
            if (handle.IsCompleted && densityData.IsCreated)
            {
                densityData.Dispose();
            }
            
            // Remove coroutine tracking
            if (chunkCoroutines.ContainsKey(chunkCoord))
            {
                chunkCoroutines.Remove(chunkCoord);
            }
        }
    }
    
    void GenerateChunkGPU(Vector3Int chunkCoord)
    {
        if (gpuChunkManager != null)
        {
            gpuChunkManager.RequestChunk(new GPUChunkManager.GPUChunkRequest
            {
                coordinate = chunkCoord,
                lodLevel = 0,
                onComplete = (meshData) =>
                {
                    CreateChunkFromMeshData(chunkCoord, meshData);
                }
            });
        }
    }
    
    void GenerateChunkImmediate(Vector3Int chunkCoord)
    {
        GameObject chunkGO = Instantiate(chunkPrefab, ChunkToWorldPos(chunkCoord), Quaternion.identity);
        Chunk chunk = chunkGO.GetComponent<Chunk>();
        
        if (chunk == null)
        {
            chunk = chunkGO.AddComponent<Chunk>();
        }
        
        chunk.Initialize(chunkCoord, chunkSize, voxelSize);
        
        // Generate density field using noise layers
        float[,,] densityField = new float[chunkSize + 1, chunkSize + 1, chunkSize + 1];
        
        for (int x = 0; x <= chunkSize; x++)
        {
            for (int y = 0; y <= chunkSize; y++)
            {
                for (int z = 0; z <= chunkSize; z++)
                {
                    Vector3 worldPos = ChunkToWorldPos(chunkCoord) + new Vector3(x, y, z) * voxelSize;
                    densityField[x, y, z] = noiseLayerStack.EvaluateStack(worldPos);
                }
            }
        }
        
        chunk.voxelData = densityField;
        chunk.GenerateMesh();
        
        chunks[chunkCoord] = chunk;
        totalChunksLoaded = chunks.Count;
    }
    
    void CreateChunkFromMeshData(Vector3Int chunkCoord, GPUChunkManager.ChunkMeshData meshData)
    {
        GameObject chunkGO = Instantiate(chunkPrefab, ChunkToWorldPos(chunkCoord), Quaternion.identity);
        Chunk chunk = chunkGO.GetComponent<Chunk>();
        
        if (chunk == null)
        {
            chunk = chunkGO.AddComponent<Chunk>();
        }
        
        chunk.Initialize(chunkCoord, chunkSize, voxelSize);
        chunk.SetMeshData(meshData.vertices, meshData.triangles, meshData.normals, meshData.uvs);
        
        chunks[chunkCoord] = chunk;
        totalChunksLoaded = chunks.Count;
    }
    
    public void RemoveChunk(Vector3Int coord)
    {
        if (chunks.ContainsKey(coord))
        {
            Chunk chunk = chunks[coord];
            
            // Stop generation coroutine for this specific chunk
            if (chunkCoroutines.ContainsKey(coord))
            {
                StopCoroutine(chunkCoroutines[coord]);
                chunkCoroutines.Remove(coord);
            }
            
            // Complete any pending jobs immediately BEFORE disposing arrays
            if (chunk.generationHandle.HasValue)
            {
                // Force complete the job
                chunk.generationHandle.Value.Complete();
                chunk.generationHandle = null;
            }
            
            // Now safe to clean up native arrays
            if (chunk.densityField.IsCreated)
            {
                chunk.densityField.Dispose();
                chunk.densityField = default;
            }
            
            totalVertices -= chunk.GetVertexCount();
            
            Destroy(chunk.gameObject);
            chunks.Remove(coord);
        }
        
        totalChunksLoaded = chunks.Count;
    }
    
    public void ForceUpdateAllChunks()
    {
        // Clear and regenerate all chunks
        List<Vector3Int> coordsToRegenerate = new List<Vector3Int>(chunks.Keys);
        
        foreach (var coord in coordsToRegenerate)
        {
            RemoveChunk(coord);
            chunkGenerationQueue.Enqueue(coord);
        }
    }
    
    Vector3Int WorldToChunkCoord(Vector3 worldPos)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / (chunkSize * voxelSize)),
            Mathf.FloorToInt(worldPos.y / (chunkSize * voxelSize)),
            Mathf.FloorToInt(worldPos.z / (chunkSize * voxelSize))
        );
    }
    
    Vector3 ChunkToWorldPos(Vector3Int chunkCoord)
    {
        return new Vector3(
            chunkCoord.x * chunkSize * voxelSize,
            chunkCoord.y * chunkSize * voxelSize,
            chunkCoord.z * chunkSize * voxelSize
        );
    }
    
    void UpdateDebugInfo()
    {
        totalVertices = 0;
        foreach (var chunk in chunks.Values)
        {
            totalVertices += chunk.GetVertexCount();
        }
    }
    
    // Convert noise stack to job-friendly data
    JobNoiseLayerStack ConvertNoiseStackToJobData(NoiseLayerStack stack)
    {
        var jobStack = new JobNoiseLayerStack();
        
        // Ensure we have at least one layer
        int layerCount = Mathf.Max(1, stack.layers.Count);
        jobStack.layerCount = stack.layers.Count; // Actual count, can be 0
        
        // Allocate arrays with at least 1 element to avoid index out of range
        jobStack.enabled = new NativeArray<bool>(layerCount, Allocator.TempJob);
        jobStack.noiseTypes = new NativeArray<int>(layerCount, Allocator.TempJob);
        jobStack.blendModes = new NativeArray<int>(layerCount, Allocator.TempJob);
        jobStack.frequencies = new NativeArray<float>(layerCount, Allocator.TempJob);
        jobStack.amplitudes = new NativeArray<float>(layerCount, Allocator.TempJob);
        jobStack.octaves = new NativeArray<int>(layerCount, Allocator.TempJob);
        jobStack.persistences = new NativeArray<float>(layerCount, Allocator.TempJob);
        jobStack.lacunarities = new NativeArray<float>(layerCount, Allocator.TempJob);
        jobStack.offsets = new NativeArray<float3>(layerCount, Allocator.TempJob);
        jobStack.verticalSquashes = new NativeArray<float>(layerCount, Allocator.TempJob);
        jobStack.densityBiases = new NativeArray<float>(layerCount, Allocator.TempJob);
        jobStack.powers = new NativeArray<float>(layerCount, Allocator.TempJob);
        
        // Copy layer data if we have any
        for (int i = 0; i < stack.layers.Count; i++)
        {
            var layer = stack.layers[i];
            jobStack.enabled[i] = layer.enabled;
            jobStack.noiseTypes[i] = (int)layer.noiseType;
            jobStack.blendModes[i] = (int)layer.blendMode;
            jobStack.frequencies[i] = layer.frequency;
            jobStack.amplitudes[i] = layer.amplitude;
            jobStack.octaves[i] = layer.octaves;
            jobStack.persistences[i] = layer.persistence;
            jobStack.lacunarities[i] = layer.lacunarity;
            jobStack.offsets[i] = layer.offset;
            jobStack.verticalSquashes[i] = layer.verticalSquash;
            jobStack.densityBiases[i] = layer.densityBias;
            jobStack.powers[i] = layer.power;
        }
        
        // If no layers, set default values for the dummy element
        if (stack.layers.Count == 0)
        {
            jobStack.enabled[0] = false;
            jobStack.noiseTypes[0] = 0;
            jobStack.blendModes[0] = 0;
            jobStack.frequencies[0] = 0.02f;
            jobStack.amplitudes[0] = 0f;
            jobStack.octaves[0] = 1;
            jobStack.persistences[0] = 0.5f;
            jobStack.lacunarities[0] = 2f;
            jobStack.offsets[0] = float3.zero;
            jobStack.verticalSquashes[0] = 1f;
            jobStack.densityBiases[0] = 0f;
            jobStack.powers[0] = 1f;
        }
        
        return jobStack;
    }
    
    void OnDestroy()
    {
        // Stop all coroutines
        StopAllCoroutines();
        
        // Complete all active jobs first
        foreach (var handle in activeJobs)
        {
            if (!handle.IsCompleted)
            {
                handle.Complete();
            }
        }
        activeJobs.Clear();
        
        // Complete any chunk-specific jobs
        foreach (var chunk in chunks.Values)
        {
            if (chunk.generationHandle.HasValue)
            {
                chunk.generationHandle.Value.Complete();
                chunk.generationHandle = null;
            }
        }
        
        // Now safe to dispose native arrays
        foreach (var chunk in chunks.Values)
        {
            if (chunk.densityField.IsCreated)
            {
                chunk.densityField.Dispose();
            }
        }
    }
    
    void OnDrawGizmosSelected()
    {
        if (!showDebugInfo) return;
        
        // Draw chunk boundaries
        Gizmos.color = Color.yellow;
        foreach (var coord in chunks.Keys)
        {
            Vector3 center = ChunkToWorldPos(coord) + Vector3.one * chunkSize * voxelSize * 0.5f;
            Gizmos.DrawWireCube(center, Vector3.one * chunkSize * voxelSize);
        }
        
        // Draw player position
        if (player != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(player.position, 1f);
        }
    }
}