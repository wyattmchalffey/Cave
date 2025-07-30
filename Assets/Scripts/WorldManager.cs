using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public class WorldManager : MonoBehaviour
{
    [Header("World Settings")]
    public Transform player;
    public int viewDistance = 5;
    public int chunksPerFrame = 2;
    
    [Header("Cave Settings")]
    public CaveSettings caveSettings = CaveSettings.Default();
    public Material caveMaterial;
    
    [Header("Optimization")]
    public bool useJobSystem = true;
    public int maxConcurrentJobs = 4;
    
    [Header("Debug")]
    public bool showDebugInfo = false;
    public int totalChunksLoaded = 0;
    
    // Chunk management
    private Dictionary<Vector3Int, Chunk> activeChunks = new Dictionary<Vector3Int, Chunk>();
    private Queue<Vector3Int> chunkGenerationQueue = new Queue<Vector3Int>();
    private Stack<Chunk> chunkPool = new Stack<Chunk>();
    private List<ChunkJobData> activeJobs = new List<ChunkJobData>();
    
    // Performance tracking
    private float lastChunkUpdate = 0f;
    private const float CHUNK_UPDATE_INTERVAL = 0.1f;
    
    private struct ChunkJobData
    {
        public Vector3Int coordinate;
        public JobHandle jobHandle;
        public NativeArray<float> voxelData;
        public Chunk chunk;
    }
    
    void Start()
    {
        // Initialize settings with random offsets based on seed
        InitializeCaveSettings();
        
        // Pre-populate chunk pool for better performance
        PrewarmChunkPool(50);
    }
    
    void InitializeCaveSettings()
    {
        UnityEngine.Random.InitState(caveSettings.octaves);
        caveSettings.noiseOffset = new float3(
            UnityEngine.Random.Range(-1000f, 1000f),
            UnityEngine.Random.Range(-1000f, 1000f),
            UnityEngine.Random.Range(-1000f, 1000f)
        );
        caveSettings.tunnelOffset = new float3(
            UnityEngine.Random.Range(-1000f, 1000f),
            UnityEngine.Random.Range(-1000f, 1000f),
            UnityEngine.Random.Range(-1000f, 1000f)
        );
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
        // Complete any finished jobs
        ProcessCompletedJobs();
        
        // Update chunks around player periodically
        if (Time.time - lastChunkUpdate > CHUNK_UPDATE_INTERVAL)
        {
            UpdateChunksAroundPlayer();
            lastChunkUpdate = Time.time;
        }
        
        // Start new chunk generation jobs
        ProcessChunkQueue();
        
        if (showDebugInfo)
        {
            totalChunksLoaded = activeChunks.Count;
        }
    }
    
    void UpdateChunksAroundPlayer()
    {
        if (player == null) return;
        
        Vector3Int playerChunkCoord = WorldToChunkCoordinate(player.position);
        
        // Queue chunks that need to be loaded
        for (int x = -viewDistance; x <= viewDistance; x++)
        {
            for (int y = -viewDistance; y <= viewDistance; y++)
            {
                for (int z = -viewDistance; z <= viewDistance; z++)
                {
                    Vector3Int chunkCoord = playerChunkCoord + new Vector3Int(x, y, z);
                    
                    if (!activeChunks.ContainsKey(chunkCoord) && !IsChunkQueued(chunkCoord))
                    {
                        chunkGenerationQueue.Enqueue(chunkCoord);
                    }
                }
            }
        }
        
        // Unload distant chunks
        List<Vector3Int> chunksToUnload = new List<Vector3Int>();
        foreach (var kvp in activeChunks)
        {
            float distance = Vector3Int.Distance(kvp.Key, playerChunkCoord);
            if (distance > viewDistance + 2)
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
            Vector3Int coord = chunkGenerationQueue.Dequeue();
            
            if (useJobSystem)
            {
                StartChunkGenerationJob(coord);
            }
            else
            {
                // Fallback to immediate generation
                GenerateChunkImmediate(coord);
            }
        }
    }
    
    void StartChunkGenerationJob(Vector3Int coordinate)
    {
        // Get chunk from pool or create new
        Chunk chunk = GetPooledChunk();
        chunk.Initialize(coordinate, caveMaterial);
        
        // Create native array for job output
        int voxelCount = Chunk.CHUNK_SIZE * Chunk.CHUNK_SIZE * Chunk.CHUNK_SIZE;
        NativeArray<float> voxelData = new NativeArray<float>(voxelCount, Allocator.TempJob);
        
        // Create and schedule job
        CaveGenerationJob job = new CaveGenerationJob
        {
            chunkWorldPosition = new float3(
                coordinate.x * Chunk.CHUNK_SIZE,
                coordinate.y * Chunk.CHUNK_SIZE,
                coordinate.z * Chunk.CHUNK_SIZE
            ),
            chunkSize = Chunk.CHUNK_SIZE,
            settings = caveSettings,
            voxelData = voxelData
        };
        
        JobHandle handle = job.Schedule();
        
        // Track active job
        activeJobs.Add(new ChunkJobData
        {
            coordinate = coordinate,
            jobHandle = handle,
            voxelData = voxelData,
            chunk = chunk
        });
    }
    
    void ProcessCompletedJobs()
    {
        for (int i = activeJobs.Count - 1; i >= 0; i--)
        {
            var jobData = activeJobs[i];
            
            if (jobData.jobHandle.IsCompleted)
            {
                // Complete the job
                jobData.jobHandle.Complete();
                
                // Copy data to chunk and generate mesh
                jobData.chunk.SetVoxelDataFromJob(jobData.voxelData);
                jobData.chunk.GenerateMesh();
                
                // Clean up native array
                jobData.voxelData.Dispose();
                
                // Add to active chunks
                activeChunks[jobData.coordinate] = jobData.chunk;
                
                // Remove from active jobs
                activeJobs.RemoveAt(i);
            }
        }
    }
    
    void GenerateChunkImmediate(Vector3Int coordinate)
    {
        Chunk chunk = GetPooledChunk();
        chunk.Initialize(coordinate, caveMaterial);
        chunk.GenerateVoxelData(caveSettings);
        chunk.GenerateMesh();
        activeChunks[coordinate] = chunk;
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
        GameObject chunkObj = new GameObject("Chunk");
        chunkObj.transform.parent = transform;
        Chunk chunk = chunkObj.AddComponent<Chunk>();
        return chunk;
    }
    
    Vector3Int WorldToChunkCoordinate(Vector3 worldPos)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / Chunk.CHUNK_SIZE),
            Mathf.FloorToInt(worldPos.y / Chunk.CHUNK_SIZE),
            Mathf.FloorToInt(worldPos.z / Chunk.CHUNK_SIZE)
        );
    }
    
    bool IsChunkQueued(Vector3Int coord)
    {
        foreach (var c in chunkGenerationQueue)
        {
            if (c == coord) return true;
        }
        
        foreach (var job in activeJobs)
        {
            if (job.coordinate == coord) return true;
        }
        
        return false;
    }
    
    void OnDestroy()
    {
        // Clean up any active jobs
        foreach (var job in activeJobs)
        {
            job.jobHandle.Complete();
            job.voxelData.Dispose();
        }
    }
    
    void OnDrawGizmosSelected()
    {
        if (!showDebugInfo || player == null) return;
        
        Vector3Int playerChunk = WorldToChunkCoordinate(player.position);
        
        // Draw view distance
        Gizmos.color = Color.yellow;
        Vector3 center = new Vector3(
            playerChunk.x * Chunk.CHUNK_SIZE + Chunk.CHUNK_SIZE / 2f,
            playerChunk.y * Chunk.CHUNK_SIZE + Chunk.CHUNK_SIZE / 2f,
            playerChunk.z * Chunk.CHUNK_SIZE + Chunk.CHUNK_SIZE / 2f
        );
        
        Gizmos.DrawWireCube(
            center,
            Vector3.one * Chunk.CHUNK_SIZE * (viewDistance * 2 + 1)
        );
    }
}