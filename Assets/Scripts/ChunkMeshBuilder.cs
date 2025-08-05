// ChunkMeshBuilder.cs - Fixed version that properly reads GPU data
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace GPUTerrain
{
    public class ChunkMeshBuilder : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ComputeShader meshExtractionShader;
        [SerializeField] private Material chunkMaterial;
        
        [Header("Settings")]
        [SerializeField] private int maxVerticesPerChunk = 15000;
        [SerializeField] private bool debugLogging = false;
        
        // Buffers for mesh extraction
        private ComputeBuffer vertexBuffer;
        private ComputeBuffer normalBuffer;
        private ComputeBuffer indexBuffer;
        private ComputeBuffer counterBuffer;
        
        // Mesh pool
        private Queue<GameObject> meshPool = new Queue<GameObject>();
        private Dictionary<int3, GameObject> activeChunks = new Dictionary<int3, GameObject>();
        
        // References
        private TerrainWorldManager worldManager;
        private RenderTexture worldDataTexture;
        
        // Stats
        private int totalMeshesCreated = 0;
        private int emptyChunksSkipped = 0;
        
        void Start()
        {
            worldManager = GetComponent<TerrainWorldManager>();
            if (worldManager == null)
            {
                Debug.LogError("ChunkMeshBuilder requires TerrainWorldManager!");
                enabled = false;
                return;
            }
            
            InitializeBuffers();
            StartCoroutine(MeshBuildingLoop());
        }
        
        void InitializeBuffers()
        {
            // Create buffers for mesh extraction - using structured buffers for better control
            vertexBuffer = new ComputeBuffer(maxVerticesPerChunk, sizeof(float) * 3);
            normalBuffer = new ComputeBuffer(maxVerticesPerChunk, sizeof(float) * 3);
            indexBuffer = new ComputeBuffer(maxVerticesPerChunk * 3, sizeof(uint));
            
            // Counter buffer to track how many vertices were generated
            counterBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
            
            Debug.Log("ChunkMeshBuilder initialized buffers");
        }
        
        public void SetWorldDataTexture(RenderTexture texture)
        {
            worldDataTexture = texture;
            Debug.Log($"World data texture set: {texture?.width}x{texture?.height}x{texture?.volumeDepth}");
        }
        
        IEnumerator MeshBuildingLoop()
        {
            yield return new WaitForSeconds(1f); // Wait for world to initialize
            
            Debug.Log("ChunkMeshBuilder starting mesh building loop");
            
            while (true)
            {
                // Check for chunks that need meshes
                var chunkStates = worldManager.GetChunkStates();
                
                // Create a list of chunks to process to avoid collection modification error
                List<int3> chunksToProcess = new List<int3>();
                
                foreach (var kvp in chunkStates)
                {
                    var coord = kvp.Key;
                    var state = kvp.Value;
                    
                    if (state.isGenerated && !state.hasMesh && !activeChunks.ContainsKey(coord))
                    {
                        // Additional check - only process chunks that are visible
                        if (worldManager.IsChunkVisible(coord))
                        {
                            chunksToProcess.Add(coord);
                        }
                    }
                }
                
                // Process the chunks
                foreach (var coord in chunksToProcess)
                {
                    if (debugLogging)
                        Debug.Log($"Building mesh for chunk {coord}");
                        
                    yield return StartCoroutine(BuildChunkMesh(coord));
                    yield return null; // Spread work across frames
                }
                
                yield return new WaitForSeconds(0.1f);
            }
        }
        
        IEnumerator BuildChunkMesh(int3 chunkCoord)
        {
            if (meshExtractionShader == null || worldDataTexture == null)
            {
                Debug.LogError($"Cannot build mesh - shader or texture is null");
                yield break;
            }
            
            // Find kernel
            int kernel = -1;
            try
            {
                kernel = meshExtractionShader.FindKernel("ExtractMesh");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to find ExtractMesh kernel: {e.Message}");
                yield break;
            }
            
            // Reset buffers
            counterBuffer.SetData(new uint[] { 0 });
            
            // Set parameters
            meshExtractionShader.SetTexture(kernel, "WorldData", worldDataTexture);
            meshExtractionShader.SetVector("ChunkCoord", new Vector4(chunkCoord.x, chunkCoord.y, chunkCoord.z, 0));
            meshExtractionShader.SetFloat("VoxelSize", worldManager.VoxelSize);
            meshExtractionShader.SetInt("ChunkSize", TerrainWorldManager.CHUNK_SIZE);
            
            // Set buffers
            meshExtractionShader.SetBuffer(kernel, "Vertices", vertexBuffer);
            meshExtractionShader.SetBuffer(kernel, "Normals", normalBuffer);
            meshExtractionShader.SetBuffer(kernel, "Indices", indexBuffer);
            meshExtractionShader.SetBuffer(kernel, "VertexCount", counterBuffer);
            
            // Dispatch
            int threadGroups = Mathf.CeilToInt(TerrainWorldManager.CHUNK_SIZE / 4.0f);
            meshExtractionShader.Dispatch(kernel, threadGroups, threadGroups, threadGroups);
            
            // Wait a frame for GPU to finish
            yield return null;
            
            // Get the actual vertex count
            uint[] countData = new uint[1];
            counterBuffer.GetData(countData);
            int vertexCount = (int)countData[0];
            int indexCount = vertexCount; // Since we're writing indices directly
            
            if (debugLogging)
                Debug.Log($"Chunk {chunkCoord}: {vertexCount} vertices, {indexCount} indices");
            
            if (vertexCount > 0 && vertexCount < maxVerticesPerChunk && indexCount > 0)
            {
                // Read back data using regular GetData for now (AsyncGPUReadback can be tricky with append buffers)
                Vector3[] vertices = new Vector3[vertexCount];
                Vector3[] normals = new Vector3[vertexCount];
                uint[] indices = new uint[indexCount];
                
                // Get data directly
                vertexBuffer.GetData(vertices, 0, 0, vertexCount);
                normalBuffer.GetData(normals, 0, 0, vertexCount);
                indexBuffer.GetData(indices, 0, 0, indexCount);
                
                // Convert uint indices to int
                int[] intIndices = new int[indexCount];
                for (int i = 0; i < indexCount; i++)
                {
                    intIndices[i] = (int)indices[i];
                }
                
                // Create mesh
                GameObject chunkObj = GetOrCreateChunkObject(chunkCoord);
                MeshFilter meshFilter = chunkObj.GetComponent<MeshFilter>();
                
                // Destroy old mesh if it exists
                if (meshFilter.mesh != null)
                {
                    DestroyImmediate(meshFilter.mesh);
                }
                
                Mesh mesh = new Mesh();
                mesh.name = $"Chunk_{chunkCoord}";
                
                if (vertexCount > 65535)
                {
                    mesh.indexFormat = IndexFormat.UInt32;
                }
                
                mesh.vertices = vertices;
                mesh.normals = normals;
                mesh.triangles = intIndices;
                
                // Calculate bounds
                mesh.RecalculateBounds();
                
                // Optimize mesh
                mesh.Optimize();
                
                // Assign mesh
                meshFilter.mesh = mesh;
                
                // Make sure the chunk is active
                chunkObj.SetActive(true);
                
                // Store reference
                activeChunks[chunkCoord] = chunkObj;
                
                // Update chunk state
                worldManager.MarkChunkHasMesh(chunkCoord);
                
                totalMeshesCreated++;
                
                if (debugLogging)
                    Debug.Log($"Created mesh for chunk {chunkCoord} with {vertexCount} vertices");
            }
            else if (vertexCount == 0)
            {
                // Empty chunk - still mark as having mesh to avoid reprocessing
                worldManager.MarkChunkHasMesh(chunkCoord);
                emptyChunksSkipped++;
                
                if (debugLogging)
                    Debug.Log($"Chunk {chunkCoord} is empty, skipping");
            }
            else
            {
                Debug.LogWarning($"Chunk {chunkCoord} has invalid vertex count: {vertexCount} or index count: {indexCount}");
            }
        }
        
        GameObject GetOrCreateChunkObject(int3 coord)
        {
            GameObject chunkObj;
            
            if (meshPool.Count > 0)
            {
                chunkObj = meshPool.Dequeue();
                chunkObj.SetActive(true);
            }
            else
            {
                chunkObj = new GameObject($"Chunk_{coord}");
                chunkObj.AddComponent<MeshFilter>();
                var renderer = chunkObj.AddComponent<MeshRenderer>();
                renderer.material = chunkMaterial != null ? chunkMaterial : new Material(Shader.Find("Standard"));
            }
            
            // Position chunk
            float chunkWorldSize = worldManager.VoxelSize * TerrainWorldManager.CHUNK_SIZE;
            chunkObj.transform.position = new Vector3(
                coord.x * chunkWorldSize,
                coord.y * chunkWorldSize,
                coord.z * chunkWorldSize
            );
            
            chunkObj.transform.parent = transform;
            chunkObj.name = $"Chunk_{coord}";
            
            return chunkObj;
        }
        
        public void ClearChunk(int3 coord)
        {
            if (activeChunks.TryGetValue(coord, out GameObject chunkObj))
            {
                // Clear the mesh to free memory
                MeshFilter meshFilter = chunkObj.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.mesh != null)
                {
                    DestroyImmediate(meshFilter.mesh);
                }
                
                chunkObj.SetActive(false);
                meshPool.Enqueue(chunkObj);
                activeChunks.Remove(coord);
            }
        }
        
        public void EnableDebugLogging(bool enable)
        {
            debugLogging = enable;
        }
        
        public void LogStats()
        {
            Debug.Log($"ChunkMeshBuilder Stats:");
            Debug.Log($"  Total meshes created: {totalMeshesCreated}");
            Debug.Log($"  Empty chunks skipped: {emptyChunksSkipped}");
            Debug.Log($"  Active chunks: {activeChunks.Count}");
            Debug.Log($"  Pooled chunks: {meshPool.Count}");
        }
        
        void OnDestroy()
        {
            // Clean up all meshes
            foreach (var chunk in activeChunks.Values)
            {
                MeshFilter meshFilter = chunk.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.mesh != null)
                {
                    DestroyImmediate(meshFilter.mesh);
                }
                Destroy(chunk);
            }
            
            while (meshPool.Count > 0)
            {
                var chunk = meshPool.Dequeue();
                Destroy(chunk);
            }
            
            // Release buffers
            vertexBuffer?.Release();
            normalBuffer?.Release();
            indexBuffer?.Release();
            counterBuffer?.Release();
        }
        
        void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying) return;
            
            // Draw active chunks
            Gizmos.color = Color.green;
            float chunkSize = worldManager.VoxelSize * TerrainWorldManager.CHUNK_SIZE;
            
            foreach (var kvp in activeChunks)
            {
                Vector3 center = kvp.Value.transform.position + Vector3.one * chunkSize * 0.5f;
                Gizmos.DrawWireCube(center, Vector3.one * chunkSize);
            }
        }
    }
}