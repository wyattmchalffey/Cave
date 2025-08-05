// TerrainWorldManager.cs - Main controller for GPU-persistent terrain
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
        [SerializeField] private int worldSizeChunks = 8;
        [SerializeField] private int worldHeightChunks = 4;
        [SerializeField] private float voxelSize = 0.5f;
        
        [Header("Generation Settings")]
        [SerializeField] private TerrainGenerationSettings generationSettings;
        
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
        
        // World state
        private Dictionary<int3, ChunkState> chunkStates = new Dictionary<int3, ChunkState>();
        private Queue<ChunkUpdateCommand> updateQueue = new Queue<ChunkUpdateCommand>();
        private Camera mainCamera;
        
        // Compute shader kernels
        private int generateTerrainKernel = -1;
        private int clearWorldKernel = -1;
        private int extractMeshKernel = -1;
        private int updateWorldKernel = -1;
        private int frustumCullKernel = -1;
        
        // Public properties
        public int WorldSizeChunks => worldSizeChunks;
        public int WorldHeightChunks => worldHeightChunks;
        public float VoxelSize => voxelSize;
        public Dictionary<int3, ChunkState> GetChunkStates() => chunkStates;
        public int GetUpdateQueueSize() => updateQueue.Count;
        public RenderTexture GetWorldDataTexture() => worldDataTexture;
        
        // Data structures
        public struct ChunkState
        {
            public int3 coordinate;
            public bool isGenerated;
            public bool hasMesh;
            public int vertexCount;
            public int vertexOffset;
        }
        
        public struct ChunkUpdateCommand
        {
            public enum CommandType { Generate, Modify, Extract }
            public CommandType type;
            public int3 chunkCoord;
            public float3 modifyCenter;
            public float modifyRadius;
        }
        
        void Awake()
        {
            ValidateComponents();
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
        }
        
        void ValidateComponents()
        {
            if (terrainGenerationShader == null)
                Debug.LogError("Terrain Generation Shader not assigned!");
            
            if (meshExtractionShader == null)
                Debug.LogError("Mesh Extraction Shader not assigned!");
            
            if (terrainMaterial == null)
                Debug.LogWarning("Terrain Material not assigned!");
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
            
            return true;
        }
        
        bool InitializeGPUResources()
        {
            try
            {
                // Create 3D world data texture
                int texSize = worldSizeChunks * CHUNK_SIZE;
                int texHeight = worldHeightChunks * CHUNK_SIZE;
                
                worldDataTexture = new RenderTexture(texSize, texHeight, 0, RenderTextureFormat.ARGBFloat);
                worldDataTexture.dimension = TextureDimension.Tex3D;
                worldDataTexture.volumeDepth = texSize;
                worldDataTexture.enableRandomWrite = true;
                worldDataTexture.filterMode = FilterMode.Bilinear;
                worldDataTexture.wrapMode = TextureWrapMode.Clamp;
                worldDataTexture.Create();
                
                if (!worldDataTexture.IsCreated())
                {
                    Debug.LogError("Failed to create world data texture!");
                    return false;
                }
                
                // Initialize buffers
                int totalChunks = worldSizeChunks * worldHeightChunks * worldSizeChunks;
                chunkMetadataBuffer = new ComputeBuffer(totalChunks, ChunkMetadata.Size);
                
                int totalVertices = MAX_VERTICES_PER_CHUNK * MAX_CHUNKS;
                vertexPoolBuffer = new ComputeBuffer(totalVertices, TerrainVertex.Size);
                indexPoolBuffer = new ComputeBuffer(totalVertices * 3, sizeof(uint));
                
                drawArgsBuffer = new ComputeBuffer(5, sizeof(uint), ComputeBufferType.IndirectArguments);
                drawArgsBuffer.SetData(new uint[] { 0, 1, 0, 0, 0 });
                
                visibleChunksBuffer = new ComputeBuffer(MAX_CHUNKS, ChunkMetadata.Size, ComputeBufferType.Append);
                
                Debug.Log($"GPU resources initialized. World texture: {texSize}x{texHeight}x{texSize}");
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
                    Debug.Log($"Found terrain kernels: Generate={generateTerrainKernel}, Clear={clearWorldKernel}");
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
                    Debug.Log($"Found mesh extraction kernel: {extractMeshKernel}");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Failed to find mesh extraction kernel: {e.Message}");
                    success = false;
                }
            }
            
            // Optional shaders
            if (worldUpdateShader != null)
            {
                try { updateWorldKernel = worldUpdateShader.FindKernel("UpdateWorld"); }
                catch { updateWorldKernel = -1; }
            }
            
            if (frustumCullingShader != null)
            {
                try { frustumCullKernel = frustumCullingShader.FindKernel("FrustumCull"); }
                catch { frustumCullKernel = -1; }
            }
            
            return success;
        }
        
        void InitializeWorld()
        {
            // Clear world data
            if (terrainGenerationShader != null && clearWorldKernel >= 0)
            {
                Debug.Log($"Clearing world data texture: {worldSizeChunks * CHUNK_SIZE} x {worldHeightChunks * CHUNK_SIZE} x {worldSizeChunks * CHUNK_SIZE}");
                
                terrainGenerationShader.SetTexture(clearWorldKernel, "WorldData", worldDataTexture);
                
                int texSize = worldSizeChunks * CHUNK_SIZE;
                int texHeight = worldHeightChunks * CHUNK_SIZE;
                int threadGroups = Mathf.CeilToInt(texSize / 8.0f);
                int threadGroupsY = Mathf.CeilToInt(texHeight / 8.0f);
                
                terrainGenerationShader.Dispatch(clearWorldKernel, threadGroups, threadGroupsY, threadGroups);
                
                Debug.Log("World data cleared to solid (density = 1.0)");
            }
            else
            {
                Debug.LogWarning("Cannot clear world data - shader or kernel not ready");
            }
            
            // Initialize chunk states
            chunkStates.Clear();
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
                            vertexCount = 0,
                            vertexOffset = 0
                        };
                    }
                }
            }
            
            Debug.Log($"World initialized with {chunkStates.Count} chunks");
        }
        
        IEnumerator WorldUpdateLoop()
        {
            while (true)
            {
                ProcessUpdateQueue();
                UpdateVisibleChunks();
                yield return new WaitForSeconds(0.1f);
            }
        }
        
        void ProcessUpdateQueue()
        {
            int commandsPerFrame = 4;
            
            while (updateQueue.Count > 0 && commandsPerFrame > 0)
            {
                var command = updateQueue.Dequeue();
                
                switch (command.type)
                {
                    case ChunkUpdateCommand.CommandType.Generate:
                        GenerateChunkTerrain(command.chunkCoord);
                        break;
                    case ChunkUpdateCommand.CommandType.Extract:
                        ExtractChunkMesh(command.chunkCoord);
                        break;
                    case ChunkUpdateCommand.CommandType.Modify:
                        ModifyTerrain(command.modifyCenter, command.modifyRadius);
                        break;
                }
                
                commandsPerFrame--;
            }
        }
        
        void UpdateVisibleChunks()
        {
            if (mainCamera == null) return;
            
            float3 cameraPos = mainCamera.transform.position;
            int3 cameraChunk = WorldToChunkCoord(cameraPos);
            
            int viewDistance = 4;
            
            for (int y = 0; y < worldHeightChunks; y++)
            {
                for (int z = -viewDistance; z <= viewDistance; z++)
                {
                    for (int x = -viewDistance; x <= viewDistance; x++)
                    {
                        int3 chunkCoord = new int3(
                            cameraChunk.x + x,
                            y,
                            cameraChunk.z + z
                        );
                        
                        // Check bounds
                        if (chunkCoord.x >= 0 && chunkCoord.x < worldSizeChunks &&
                            chunkCoord.z >= 0 && chunkCoord.z < worldSizeChunks)
                        {
                            if (chunkStates.ContainsKey(chunkCoord))
                            {
                                var state = chunkStates[chunkCoord];
                                if (!state.isGenerated)
                                {
                                    QueueChunkGeneration(chunkCoord);
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
                Debug.LogError($"Cannot generate chunk {chunkCoord}: Shader not ready");
                return;
            }
            
            Debug.Log($"Generating chunk {chunkCoord}");
            
            // Set parameters
            terrainGenerationShader.SetTexture(generateTerrainKernel, "WorldData", worldDataTexture);
            terrainGenerationShader.SetVector("ChunkCoord", new Vector4(chunkCoord.x, chunkCoord.y, chunkCoord.z, 0));
            terrainGenerationShader.SetFloat("VoxelSize", voxelSize);
            terrainGenerationShader.SetInt("ChunkSize", CHUNK_SIZE);
            
            // Apply generation settings
            if (generationSettings != null)
            {
                generationSettings.ApplyToComputeShader(terrainGenerationShader, generateTerrainKernel);
            }
            else
            {
                // Use simple default values for testing
                terrainGenerationShader.SetFloat("CaveFrequency", 0.02f);
                terrainGenerationShader.SetFloat("CaveAmplitude", 1.0f);
                terrainGenerationShader.SetInt("Octaves", 4);
                terrainGenerationShader.SetFloat("Lacunarity", 2.0f);
                terrainGenerationShader.SetFloat("Persistence", 0.5f);
                terrainGenerationShader.SetFloat("MinCaveHeight", -50f);
                terrainGenerationShader.SetFloat("MaxCaveHeight", 100f);
            }
            
            // Dispatch
            int threadGroups = Mathf.CeilToInt(CHUNK_SIZE / 8.0f);
            terrainGenerationShader.Dispatch(generateTerrainKernel, threadGroups, threadGroups, threadGroups);
            
            // Update state
            if (chunkStates.ContainsKey(chunkCoord))
            {
                var state = chunkStates[chunkCoord];
                state.isGenerated = true;
                chunkStates[chunkCoord] = state;
                Debug.Log($"Chunk {chunkCoord} marked as generated");
            }
        }
        
        void ExtractChunkMesh(int3 chunkCoord)
        {
            // TODO: Implement mesh extraction
            if (chunkStates.ContainsKey(chunkCoord))
            {
                var state = chunkStates[chunkCoord];
                state.hasMesh = true;
                chunkStates[chunkCoord] = state;
            }
        }
        
        void ModifyTerrain(float3 worldPos, float radius)
        {
            // TODO: Implement terrain modification
        }
        
        int3 WorldToChunkCoord(float3 worldPos)
        {
            return new int3(
                Mathf.FloorToInt(worldPos.x / (CHUNK_SIZE * voxelSize)),
                Mathf.FloorToInt(worldPos.y / (CHUNK_SIZE * voxelSize)),
                Mathf.FloorToInt(worldPos.z / (CHUNK_SIZE * voxelSize))
            );
        }
        
        void QueueChunkGeneration(int3 coord)
        {
            updateQueue.Enqueue(new ChunkUpdateCommand
            {
                type = ChunkUpdateCommand.CommandType.Generate,
                chunkCoord = coord
            });
        }
        
        void QueueMeshExtraction(int3 coord)
        {
            updateQueue.Enqueue(new ChunkUpdateCommand
            {
                type = ChunkUpdateCommand.CommandType.Extract,
                chunkCoord = coord
            });
        }
        
        public void RegenerateWorld()
        {
            StopAllCoroutines();
            updateQueue.Clear();
            InitializeWorld();
            StartCoroutine(WorldUpdateLoop());
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
            }
        }
    }
}