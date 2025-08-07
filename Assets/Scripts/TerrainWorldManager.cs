// TerrainWorldManager.cs - Fixed version with proper chunk generation pattern
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;

namespace GPUTerrain
{
    public class TerrainWorldManager : MonoBehaviour
    {
        [Header("GPU Resources")]
        [SerializeField] private ComputeShader terrainGenerationShader;
        [SerializeField] private ComputeShader meshExtractionShader;
        [SerializeField] private ComputeShader worldUpdateShader;
        [SerializeField] private ComputeShader frustumCullingShader;
        
        [Header("Rendering")]
        [SerializeField] private Material terrainMaterial;
        
        [Header("World Settings")]
        [SerializeField] private int worldSizeChunks = 16;
        [SerializeField] private int worldHeightChunks = 8;
        [SerializeField] private float voxelSize = 0.25f;
        [SerializeField] private int viewDistance = 5;
        
        [Header("Generation Settings")]
        [SerializeField] private TerrainGenerationSettings generationSettings;
        
        [Header("Performance")]
        [SerializeField] private int chunksPerFrame = 4;
        [SerializeField] private float updateInterval = 0.1f;
        
        // Constants
        public const int CHUNK_SIZE = 32;
        public const int CHUNK_SIZE_PLUS_ONE = CHUNK_SIZE + 1;
        public const int MAX_VERTICES_PER_CHUNK = 15000;
        public const int MAX_CHUNKS = 2048;
        
        // Persistent GPU resources
        private RenderTexture worldDataTexture;
        private ComputeBuffer chunkMetadataBuffer;
        private ComputeBuffer vertexPoolBuffer;
        private ComputeBuffer indexPoolBuffer;
        private ComputeBuffer drawArgsBuffer;
        private ComputeBuffer visibleChunksBuffer;
        private ComputeBuffer updateCommandBuffer;
        
        // World state
        private Dictionary<int3, ChunkState> chunkStates = new Dictionary<int3, ChunkState>();
        private Queue<ChunkUpdateCommand> updateQueue = new Queue<ChunkUpdateCommand>();
        private HashSet<int3> chunksInQueue = new HashSet<int3>();
        private Camera mainCamera;
        private Transform playerTransform;
        
        // Compute shader kernels
        private int generateTerrainKernel = -1;
        private int clearWorldKernel = -1;
        private int extractMeshKernel = -1;
        private int updateWorldKernel = -1;
        private int frustumCullKernel = -1;
        
        // Statistics
        private int totalChunksGenerated = 0;
        private int totalChunksWithMesh = 0;
        
        // Public properties
        public int WorldSizeChunks => worldSizeChunks;
        public int WorldHeightChunks => worldHeightChunks;
        public float VoxelSize => voxelSize;
        public Dictionary<int3, ChunkState> GetChunkStates() => new Dictionary<int3, ChunkState>(chunkStates);
        public int GetUpdateQueueSize() => updateQueue.Count;
        public RenderTexture GetWorldDataTexture() => worldDataTexture;
        
        // Events
        public System.Action<int3> OnChunkGenerated;
        public System.Action<int3> OnChunkMeshBuilt;
        
        // Data structures
        public struct ChunkState
        {
            public int3 coordinate;
            public bool isGenerated;
            public bool hasMesh;
            public bool isGenerating;
            public int vertexCount;
            public int vertexOffset;
            public float lastAccessTime;
            public int lodLevel;
        }
        
        public struct ChunkUpdateCommand
        {
            public enum CommandType { Generate, Modify, Extract, Regenerate }
            public CommandType type;
            public int3 chunkCoord;
            public float3 modifyCenter;
            public float modifyRadius;
            public float modifyStrength;
        }
        
        void Awake()
        {
            ValidateComponents();
            FindPlayer();
        }
        
        void Start()
        {
            mainCamera = Camera.main;
            if (mainCamera == null)
            {
                Debug.LogError("No main camera found!");
                enabled = false;
                return;
            }
            
            if (!InitializeSystem())
            {
                Debug.LogError("Failed to initialize terrain system!");
                enabled = false;
                return;
            }
            
            StartCoroutine(WorldUpdateLoop());
            StartCoroutine(ChunkMaintenanceLoop());
        }
        
        void ValidateComponents()
        {
            if (terrainGenerationShader == null)
                Debug.LogError("Terrain Generation Shader not assigned!");
            
            if (meshExtractionShader == null)
                Debug.LogError("Mesh Extraction Shader not assigned!");
            
            if (terrainMaterial == null)
            {
                Debug.LogWarning("Terrain Material not assigned! Using default.");
                terrainMaterial = new Material(Shader.Find("Standard"));
            }
            
            // Validate world size
            worldSizeChunks = Mathf.Clamp(worldSizeChunks, 4, 64);
            worldHeightChunks = Mathf.Clamp(worldHeightChunks, 2, 32);
            voxelSize = Mathf.Clamp(voxelSize, 0.1f, 2f);
            viewDistance = Mathf.Clamp(viewDistance, 2, 16);
        }
        
        void FindPlayer()
        {
            // Try to find player by tag first
            GameObject playerGO = GameObject.FindGameObjectWithTag("Player");
            if (playerGO != null)
            {
                playerTransform = playerGO.transform;
            }
            else
            {
                // Try to find by component
                var controller = FindFirstObjectByType<TerrainTestController>();
                if (controller != null)
                {
                    playerTransform = controller.transform;
                }
            }
            
            if (playerTransform == null)
            {
                Debug.LogWarning("No player found - using camera as player position");
            }
        }
        
        bool InitializeSystem()
        {
            // Create default settings if needed
            if (generationSettings == null)
            {
                Debug.LogWarning("No TerrainGenerationSettings assigned, creating defaults");
                generationSettings = TerrainGenerationSettings.CreateDefault();
            }
            
            // Initialize GPU resources
            if (!InitializeGPUResources())
                return false;
            
            // Find compute kernels
            if (!FindComputeKernels())
                return false;
            
            // Initialize world
            InitializeWorld();
            
            // Initialize mesh builder if present
            var meshBuilder = GetComponent<ChunkMeshBuilder>();
            if (meshBuilder != null)
            {
                meshBuilder.SetWorldDataTexture(worldDataTexture);
            }
            else
            {
                Debug.LogWarning("No ChunkMeshBuilder component found - adding one");
                gameObject.AddComponent<ChunkMeshBuilder>();
            }
            
            return true;
        }
        
        bool InitializeGPUResources()
        {
            try
            {
                // Create 3D world data texture (now with padding) 
                int paddedChunkSize = CHUNK_SIZE_PLUS_ONE; // Should be 33
                int texSize = worldSizeChunks * paddedChunkSize;
                int texHeight = worldHeightChunks * paddedChunkSize;

                worldDataTexture = new RenderTexture(texSize, texHeight, 0, RenderTextureFormat.ARGBFloat);
                worldDataTexture.dimension = TextureDimension.Tex3D;
                worldDataTexture.volumeDepth = texSize;
                worldDataTexture.enableRandomWrite = true;
                worldDataTexture.filterMode = FilterMode.Bilinear;
                worldDataTexture.wrapMode = TextureWrapMode.Clamp;
                worldDataTexture.name = "WorldDataTexture";
                worldDataTexture.Create();
                
                if (!worldDataTexture.IsCreated())
                {
                    Debug.LogError("Failed to create world data texture!");
                    return false;
                }
                
                // Initialize buffers
                int totalChunks = worldSizeChunks * worldHeightChunks * worldSizeChunks;
                chunkMetadataBuffer = new ComputeBuffer(totalChunks, ChunkMetadata.Size);
                
                // Initialize chunk metadata
                ChunkMetadata[] initialMetadata = new ChunkMetadata[totalChunks];
                for (int i = 0; i < totalChunks; i++)
                {
                    initialMetadata[i] = new ChunkMetadata
                    {
                        position = float3.zero,
                        vertexOffset = 0,
                        vertexCount = 0,
                        indexOffset = 0,
                        lodLevel = 0,
                        flags = 0
                    };
                }
                chunkMetadataBuffer.SetData(initialMetadata);
                
                // Vertex and index pools
                int totalVertices = MAX_VERTICES_PER_CHUNK * MAX_CHUNKS;
                vertexPoolBuffer = new ComputeBuffer(totalVertices, TerrainVertex.Size);
                indexPoolBuffer = new ComputeBuffer(totalVertices * 3, sizeof(uint));
                
                // Draw args for indirect rendering
                drawArgsBuffer = new ComputeBuffer(5, sizeof(uint), ComputeBufferType.IndirectArguments);
                drawArgsBuffer.SetData(new uint[] { 0, 1, 0, 0, 0 });
                
                // Visible chunks buffer
                visibleChunksBuffer = new ComputeBuffer(MAX_CHUNKS, ChunkMetadata.Size, ComputeBufferType.Append);
                
                // Update command buffer
                updateCommandBuffer = new ComputeBuffer(64, WorldUpdateCommand.Size);
                
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to initialize GPU resources: {e.Message}");
                return false;
            }
        }
        
        bool FindComputeKernels()
        {
            bool success = true;
            
            // Find terrain generation kernels
            if (terrainGenerationShader != null)
            {
                try
                {
                    generateTerrainKernel = terrainGenerationShader.FindKernel("GenerateTerrain");
                    clearWorldKernel = terrainGenerationShader.FindKernel("ClearWorld");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Failed to find terrain generation kernels: {e.Message}");
                    success = false;
                }
            }
            else
            {
                Debug.LogError("Terrain generation shader is null!");
                success = false;
            }
            
            // Find mesh extraction kernel
            if (meshExtractionShader != null)
            {
                try
                {
                    extractMeshKernel = meshExtractionShader.FindKernel("ExtractMesh");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Failed to find mesh extraction kernel: {e.Message}");
                    // Don't fail completely - mesh extraction is handled by ChunkMeshBuilder
                }
            }
            
            // Optional shaders
            if (worldUpdateShader != null)
            {
                try 
                { 
                    updateWorldKernel = worldUpdateShader.FindKernel("UpdateWorld");
                }
                catch 
                { 
                    updateWorldKernel = -1;
                }
            }
            
            if (frustumCullingShader != null)
            {
                try 
                { 
                    frustumCullKernel = frustumCullingShader.FindKernel("FrustumCull");
                }
                catch 
                { 
                    frustumCullKernel = -1;
                }
            }
            
            return success;
        }
        
        void InitializeWorld()
        {
            // Clear world data
            if (terrainGenerationShader != null && clearWorldKernel >= 0)
            {
                terrainGenerationShader.SetTexture(clearWorldKernel, "WorldData", worldDataTexture);

                int paddedChunkSize = CHUNK_SIZE_PLUS_ONE;
                int texSize = worldSizeChunks * paddedChunkSize;
                int texHeight = worldHeightChunks * paddedChunkSize;
                int threadGroups = Mathf.CeilToInt(texSize / 8.0f);
                int threadGroupsY = Mathf.CeilToInt(texHeight / 8.0f);
                
                terrainGenerationShader.Dispatch(clearWorldKernel, threadGroups, threadGroupsY, threadGroups);
            }
            
            // Initialize chunk states
            chunkStates.Clear();
            chunksInQueue.Clear();
            totalChunksGenerated = 0;
            totalChunksWithMesh = 0;
            
            for (int y = 0; y < worldHeightChunks; y++)
            {
                for (int z = 0; z < worldSizeChunks; z++)
                {
                    for (int x = 0; x < worldSizeChunks; x++)
                    {
                        int3 coord = new int3(x, y, z);
                        chunkStates[coord] = new ChunkState
                        {
                            coordinate = coord,
                            isGenerated = false,
                            hasMesh = false,
                            isGenerating = false,
                            vertexCount = 0,
                            vertexOffset = 0,
                            lastAccessTime = 0,
                            lodLevel = 0
                        };
                    }
                }
            }
        }
        
        IEnumerator WorldUpdateLoop()
        {
            while (true)
            {
                ProcessUpdateQueue();
                UpdateVisibleChunks();
                
                yield return new WaitForSeconds(updateInterval);
            }
        }
        
        IEnumerator ChunkMaintenanceLoop()
        {
            yield return new WaitForSeconds(5f); // Initial delay
            
            while (true)
            {
                CleanupDistantChunks();
                UpdateChunkLODs();
                
                yield return new WaitForSeconds(2f);
            }
        }
        
        void ProcessUpdateQueue()
        {
            int commandsThisFrame = 0;
            
            while (updateQueue.Count > 0 && commandsThisFrame < chunksPerFrame)
            {
                var command = updateQueue.Dequeue();
                chunksInQueue.Remove(command.chunkCoord);
                
                switch (command.type)
                {
                    case ChunkUpdateCommand.CommandType.Generate:
                        GenerateChunkTerrain(command.chunkCoord);
                        break;
                    case ChunkUpdateCommand.CommandType.Extract:
                        ExtractChunkMesh(command.chunkCoord);
                        break;
                    case ChunkUpdateCommand.CommandType.Modify:
                        ModifyTerrain(command.modifyCenter, command.modifyRadius, command.modifyStrength);
                        break;
                    case ChunkUpdateCommand.CommandType.Regenerate:
                        RegenerateChunk(command.chunkCoord);
                        break;
                }
                
                commandsThisFrame++;
            }
        }
        
        void UpdateVisibleChunks()
        {
            if (mainCamera == null) return;
            
            float3 viewerPos = playerTransform != null ? playerTransform.position : mainCamera.transform.position;
            int3 viewerChunk = WorldToChunkCoord(viewerPos);
            
            // Update chunk visibility based on distance
            // FIXED: Generate chunks in a rectangular pattern, not circular
            for (int y = 0; y < worldHeightChunks; y++)
            {
                for (int z = -viewDistance; z <= viewDistance; z++)
                {
                    for (int x = -viewDistance; x <= viewDistance; x++)
                    {
                        int3 chunkCoord = new int3(
                            viewerChunk.x + x,
                            y,
                            viewerChunk.z + z
                        );
                        
                        // Check bounds
                        if (IsChunkInBounds(chunkCoord))
                        {
                            // FIXED: Use rectangular pattern instead of circular distance check
                            // This ensures all chunks in the view distance are generated
                            bool shouldGenerate = true;
                            
                            // Optional: Use a slightly larger circular check if you want rounded corners
                            // float distance = math.length(new float3(x, 0, z));
                            // bool shouldGenerate = distance <= viewDistance * 1.42f; // sqrt(2) for corners
                            
                            if (shouldGenerate)
                            {
                                if (chunkStates.TryGetValue(chunkCoord, out ChunkState state))
                                {
                                    // Update last access time
                                    state.lastAccessTime = Time.time;
                                    chunkStates[chunkCoord] = state;
                                    
                                    // Queue for generation if needed
                                    if (!state.isGenerated && !state.isGenerating && !chunksInQueue.Contains(chunkCoord))
                                    {
                                        QueueChunkGeneration(chunkCoord);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        
        void GenerateChunkTerrain(int3 chunkCoord)
        {
            if (terrainGenerationShader == null || generateTerrainKernel < 0)
            {
                return;
            }
            
            // Mark as generating
            if (chunkStates.TryGetValue(chunkCoord, out ChunkState state))
            {
                state.isGenerating = true;
                chunkStates[chunkCoord] = state;
            }

            // Set parameters
            terrainGenerationShader.SetTexture(generateTerrainKernel, "WorldData", worldDataTexture);
            terrainGenerationShader.SetInts("ChunkCoord", new int[] { chunkCoord.x, chunkCoord.y, chunkCoord.z });
            terrainGenerationShader.SetFloat("VoxelSize", voxelSize);
            terrainGenerationShader.SetInt("ChunkSize", CHUNK_SIZE);

            // Generate N+1 voxels for seamless Marching Cubes boundaries.
            const int generationSize = CHUNK_SIZE + 1;
            terrainGenerationShader.SetInt("GenerationSize", generationSize);

            // Apply generation settings
            if (generationSettings != null)
            {
                generationSettings.ApplyToComputeShader(terrainGenerationShader, generateTerrainKernel);
            }

            // Dispatch
            int threadGroups = Mathf.CeilToInt(generationSize / 8.0f);
            terrainGenerationShader.Dispatch(generateTerrainKernel, threadGroups, threadGroups, threadGroups);

            // Update state
            if (chunkStates.TryGetValue(chunkCoord, out state))
            {
                state.isGenerated = true;
                state.isGenerating = false;
                chunkStates[chunkCoord] = state;
                totalChunksGenerated++;
                
                OnChunkGenerated?.Invoke(chunkCoord);
            }
        }
        
        void ExtractChunkMesh(int3 chunkCoord)
        {
            // Mesh extraction is handled by ChunkMeshBuilder component
            // This method is here for future GPU-only implementation
        }
        
        void ModifyTerrain(float3 worldPos, float radius, float strength)
        {
            if (worldUpdateShader == null || updateWorldKernel < 0)
            {
                return;
            }
            
            // Find affected chunks
            int3 minChunk = WorldToChunkCoord(worldPos - radius);
            int3 maxChunk = WorldToChunkCoord(worldPos + radius);
            
            // Queue affected chunks for regeneration
            for (int y = minChunk.y; y <= maxChunk.y; y++)
            {
                for (int z = minChunk.z; z <= maxChunk.z; z++)
                {
                    for (int x = minChunk.x; x <= maxChunk.x; x++)
                    {
                        int3 coord = new int3(x, y, z);
                        if (IsChunkInBounds(coord))
                        {
                            QueueChunkRegeneration(coord);
                        }
                    }
                }
            }
            
            // Apply modification
            worldUpdateShader.SetTexture(updateWorldKernel, "WorldData", worldDataTexture);
            worldUpdateShader.SetVector("ModifyCenter", new Vector4(worldPos.x, worldPos.y, worldPos.z, 0));
            worldUpdateShader.SetFloat("ModifyRadius", radius);
            worldUpdateShader.SetFloat("ModifyStrength", strength);
            
            int groups = Mathf.CeilToInt((radius * 2) / voxelSize / 8);
            worldUpdateShader.Dispatch(updateWorldKernel, groups, groups, groups);
        }
        
        void RegenerateChunk(int3 chunkCoord)
        {
            if (chunkStates.TryGetValue(chunkCoord, out ChunkState state))
            {
                state.hasMesh = false;
                chunkStates[chunkCoord] = state;
                
                // Notify mesh builder to rebuild
                var meshBuilder = GetComponent<ChunkMeshBuilder>();
                if (meshBuilder != null)
                {
                    meshBuilder.ClearChunk(chunkCoord);
                }
            }
        }
        
        void CleanupDistantChunks()
        {
            if (playerTransform == null && mainCamera == null) return;
            
            float3 viewerPos = playerTransform != null ? playerTransform.position : mainCamera.transform.position;
            int3 viewerChunk = WorldToChunkCoord(viewerPos);
            float maxDistance = viewDistance * 1.5f;
            
            List<int3> chunksToClean = new List<int3>();
            
            foreach (var kvp in chunkStates)
            {
                if (kvp.Value.hasMesh)
                {
                    // FIXED: Use chunk grid distance, not world distance
                    int3 chunkOffset = kvp.Key - viewerChunk;
                    float distance = math.max(math.abs(chunkOffset.x), math.abs(chunkOffset.z));
                    
                    if (distance > maxDistance)
                    {
                        chunksToClean.Add(kvp.Key);
                    }
                }
            }
            
            // Clean up distant chunks
            var meshBuilder = GetComponent<ChunkMeshBuilder>();
            foreach (var coord in chunksToClean)
            {
                if (meshBuilder != null)
                {
                    meshBuilder.ClearChunk(coord);
                }
                
                if (chunkStates.TryGetValue(coord, out ChunkState state))
                {
                    state.hasMesh = false;
                    state.isGenerated = false;
                    chunkStates[coord] = state;
                    totalChunksWithMesh--;
                }
            }
        }
        
        void UpdateChunkLODs()
        {
            // TODO: Implement LOD system
            // For now, all chunks use LOD 0
        }
        
        public bool IsChunkInBounds(int3 coord)
        {
            return coord.x >= 0 && coord.x < worldSizeChunks &&
                   coord.y >= 0 && coord.y < worldHeightChunks &&
                   coord.z >= 0 && coord.z < worldSizeChunks;
        }
        
        public int3 WorldToChunkCoord(float3 worldPos)
        {
            return new int3(
                Mathf.FloorToInt(worldPos.x / (CHUNK_SIZE * voxelSize)),
                Mathf.FloorToInt(worldPos.y / (CHUNK_SIZE * voxelSize)),
                Mathf.FloorToInt(worldPos.z / (CHUNK_SIZE * voxelSize))
            );
        }
        
        public float3 ChunkToWorldPos(int3 chunkCoord)
        {
            return new float3(chunkCoord) * CHUNK_SIZE * voxelSize;
        }
        
        void QueueChunkGeneration(int3 coord)
        {
            if (!chunksInQueue.Contains(coord))
            {
                updateQueue.Enqueue(new ChunkUpdateCommand
                {
                    type = ChunkUpdateCommand.CommandType.Generate,
                    chunkCoord = coord
                });
                chunksInQueue.Add(coord);
            }
        }
        
        void QueueMeshExtraction(int3 coord)
        {
            if (!chunksInQueue.Contains(coord))
            {
                updateQueue.Enqueue(new ChunkUpdateCommand
                {
                    type = ChunkUpdateCommand.CommandType.Extract,
                    chunkCoord = coord
                });
                chunksInQueue.Add(coord);
            }
        }
        
        void QueueChunkRegeneration(int3 coord)
        {
            if (!chunksInQueue.Contains(coord))
            {
                updateQueue.Enqueue(new ChunkUpdateCommand
                {
                    type = ChunkUpdateCommand.CommandType.Regenerate,
                    chunkCoord = coord
                });
                chunksInQueue.Add(coord);
            }
        }
        
        public void ModifyTerrainAt(Vector3 worldPos, float radius, float strength = 1f)
        {
            updateQueue.Enqueue(new ChunkUpdateCommand
            {
                type = ChunkUpdateCommand.CommandType.Modify,
                modifyCenter = worldPos,
                modifyRadius = radius,
                modifyStrength = strength
            });
        }
        
        public void RegenerateWorld()
        {
            StopAllCoroutines();
            updateQueue.Clear();
            chunksInQueue.Clear();
            InitializeWorld();
            StartCoroutine(WorldUpdateLoop());
            StartCoroutine(ChunkMaintenanceLoop());
        }
        
        public void MarkChunkHasMesh(int3 coord)
        {
            if (chunkStates.ContainsKey(coord))
            {
                var state = chunkStates[coord];
                if (!state.hasMesh)
                {
                    state.hasMesh = true;
                    chunkStates[coord] = state;
                    totalChunksWithMesh++;
                    
                    OnChunkMeshBuilt?.Invoke(coord);
                }
            }
        }
        
        public ChunkState GetChunkState(int3 coord)
        {
            return chunkStates.TryGetValue(coord, out ChunkState state) ? state : default;
        }
        
        public bool IsChunkGenerated(int3 coord)
        {
            return chunkStates.TryGetValue(coord, out ChunkState state) && state.isGenerated;
        }
        
        public bool IsChunkVisible(int3 coord)
        {
            if (!chunkStates.TryGetValue(coord, out ChunkState state))
                return false;
                
            if (playerTransform == null && mainCamera == null)
                return false;
                
            float3 viewerPos = playerTransform != null ? playerTransform.position : mainCamera.transform.position;
            int3 viewerChunk = WorldToChunkCoord(viewerPos);
            
            // FIXED: Use rectangular check
            int3 chunkOffset = coord - viewerChunk;
            bool inRange = math.abs(chunkOffset.x) <= viewDistance && 
                          math.abs(chunkOffset.z) <= viewDistance;
            
            return inRange;
        }
        
        void OnDestroy()
        {
            // Clean up GPU resources
            worldDataTexture?.Release();
            chunkMetadataBuffer?.Release();
            vertexPoolBuffer?.Release();
            indexPoolBuffer?.Release();
            drawArgsBuffer?.Release();
            visibleChunksBuffer?.Release();
            updateCommandBuffer?.Release();
        }
        
        void OnDrawGizmosSelected()
        {
            // Draw world bounds when selected
            if (Application.isPlaying)
            {
                Gizmos.color = Color.yellow;
                float worldSize = worldSizeChunks * CHUNK_SIZE * voxelSize;
                float worldHeight = worldHeightChunks * CHUNK_SIZE * voxelSize;
                Vector3 center = new Vector3(worldSize * 0.5f, worldHeight * 0.5f, worldSize * 0.5f);
                Vector3 size = new Vector3(worldSize, worldHeight, worldSize);
                Gizmos.DrawWireCube(center, size);
                
                // Draw view distance
                if (playerTransform != null || mainCamera != null)
                {
                    Vector3 viewerPos = playerTransform != null ? playerTransform.position : mainCamera.transform.position;
                    Gizmos.color = Color.cyan;
                    
                    // FIXED: Draw rectangular view area
                    float chunkSize = CHUNK_SIZE * voxelSize;
                    Vector3 viewSize = new Vector3(
                        viewDistance * 2 * chunkSize,
                        worldHeight,
                        viewDistance * 2 * chunkSize
                    );
                    Vector3 viewCenter = viewerPos;
                    viewCenter.y = worldHeight * 0.5f;
                    Gizmos.DrawWireCube(viewCenter, viewSize);
                }
            }
        }
        
        // Debug methods
        public void LogStatistics()
        {
            Debug.Log($"=== Terrain Statistics ===");
            Debug.Log($"Total chunks: {chunkStates.Count}");
            Debug.Log($"Generated chunks: {totalChunksGenerated}");
            Debug.Log($"Chunks with mesh: {totalChunksWithMesh}");
            Debug.Log($"Update queue size: {updateQueue.Count}");
            Debug.Log($"GPU Memory estimate: {EstimateGPUMemoryUsage():F1} MB");
        }
        
        float EstimateGPUMemoryUsage()
        {
            float totalMB = 0;
            
            // World texture
            if (worldDataTexture != null)
            {
                int texSize = worldSizeChunks * CHUNK_SIZE;
                int texHeight = worldHeightChunks * CHUNK_SIZE;
                totalMB += (texSize * texHeight * texSize * 16) / (1024f * 1024f);
            }
            
            // Buffers
            if (vertexPoolBuffer != null)
            {
                totalMB += (MAX_VERTICES_PER_CHUNK * MAX_CHUNKS * TerrainVertex.Size) / (1024f * 1024f);
            }
            
            return totalMB;
        }
    }
}