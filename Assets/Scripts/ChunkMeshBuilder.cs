// ChunkMeshBuilder.cs - Builds Unity meshes from GPU-extracted data
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
        
        // Buffers for mesh extraction
        private ComputeBuffer vertexBuffer;
        private ComputeBuffer normalBuffer;
        private ComputeBuffer indexBuffer;
        private ComputeBuffer counterBuffer;
        private ComputeBuffer argsBuffer;
        
        // Mesh pool
        private Queue<GameObject> meshPool = new Queue<GameObject>();
        private Dictionary<int3, GameObject> activeChunks = new Dictionary<int3, GameObject>();
        
        // References
        private TerrainWorldManager worldManager;
        private RenderTexture worldDataTexture;
        
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
            // Create buffers for mesh extraction - using append buffers
            vertexBuffer = new ComputeBuffer(maxVerticesPerChunk, sizeof(float) * 3, ComputeBufferType.Append);
            normalBuffer = new ComputeBuffer(maxVerticesPerChunk, sizeof(float) * 3, ComputeBufferType.Append);
            indexBuffer = new ComputeBuffer(maxVerticesPerChunk * 3, sizeof(uint), ComputeBufferType.Append);
            
            // Counter buffer to track how many vertices were generated
            counterBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
            
            // Args buffer for indirect drawing (optional)
            argsBuffer = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
        }
        
        public void SetWorldDataTexture(RenderTexture texture)
        {
            worldDataTexture = texture;
        }
        
        IEnumerator MeshBuildingLoop()
        {
            yield return new WaitForSeconds(1f); // Wait for world to initialize
            
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
                        chunksToProcess.Add(coord);
                    }
                }
                
                // Process the chunks
                foreach (var coord in chunksToProcess)
                {
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
            
            if (kernel < 0)
            {
                yield break;
            }
            
            // Reset buffers
            vertexBuffer.SetCounterValue(0);
            normalBuffer.SetCounterValue(0);
            indexBuffer.SetCounterValue(0);
            counterBuffer.SetData(new uint[] { 0 });
            
            // Set parameters
            meshExtractionShader.SetTexture(kernel, "WorldData", worldDataTexture);
            meshExtractionShader.SetVector("ChunkCoord", new Vector4(chunkCoord.x, chunkCoord.y, chunkCoord.z, 0));
            meshExtractionShader.SetFloat("VoxelSize", worldManager.VoxelSize);
            meshExtractionShader.SetInt("ChunkSize", TerrainWorldManager.CHUNK_SIZE);
            
            // Set buffers - these must match the names in the compute shader
            meshExtractionShader.SetBuffer(kernel, "Vertices", vertexBuffer);
            meshExtractionShader.SetBuffer(kernel, "Normals", normalBuffer);
            meshExtractionShader.SetBuffer(kernel, "Indices", indexBuffer);
            meshExtractionShader.SetBuffer(kernel, "VertexCount", counterBuffer);
            
            // Dispatch
            int threadGroups = Mathf.CeilToInt(TerrainWorldManager.CHUNK_SIZE / 4.0f); // Match the [4,4,4] thread group size
            meshExtractionShader.Dispatch(kernel, threadGroups, threadGroups, threadGroups);
            
            // Wait a frame for GPU to finish
            yield return null;
            
            // Get the actual vertex count from the append buffer
            ComputeBuffer.CopyCount(vertexBuffer, argsBuffer, 0);
            uint[] args = new uint[4];
            argsBuffer.GetData(args);
            int vertexCount = (int)args[0];
            
            if (vertexCount > 0 && vertexCount < maxVerticesPerChunk)
            {
                // Read back data
                Vector3[] vertices = new Vector3[vertexCount];
                Vector3[] normals = new Vector3[vertexCount];
                int[] indices = new int[vertexCount];
                
                // Create native arrays for async readback (more efficient)
                var vertexRequest = AsyncGPUReadback.Request(vertexBuffer, vertexCount * sizeof(float) * 3, 0);
                var normalRequest = AsyncGPUReadback.Request(normalBuffer, vertexCount * sizeof(float) * 3, 0);
                var indexRequest = AsyncGPUReadback.Request(indexBuffer, vertexCount * sizeof(uint), 0);
                
                // Wait for readback to complete
                yield return new WaitUntil(() => vertexRequest.done && normalRequest.done && indexRequest.done);
                
                if (vertexRequest.hasError || normalRequest.hasError || indexRequest.hasError)
                {
                    yield break;
                }
                
                // Get data from readback
                var vertexData = vertexRequest.GetData<Vector3>();
                var normalData = normalRequest.GetData<Vector3>();
                var indexData = indexRequest.GetData<uint>();
                
                vertexData.CopyTo(vertices);
                normalData.CopyTo(normals);
                
                // Convert uint indices to int
                indices = new int[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    indices[i] = (int)indexData[i];
                }
                
                // Create mesh
                GameObject chunkObj = GetOrCreateChunkObject(chunkCoord);
                MeshFilter meshFilter = chunkObj.GetComponent<MeshFilter>();
                
                Mesh mesh = new Mesh();
                mesh.name = $"Chunk_{chunkCoord}";
                
                if (vertexCount > 65535)
                {
                    mesh.indexFormat = IndexFormat.UInt32;
                }
                
                mesh.vertices = vertices;
                mesh.normals = normals;
                mesh.triangles = indices;
                
                // Calculate bounds
                mesh.RecalculateBounds();
                
                // Optimize mesh
                mesh.Optimize();
                
                // Assign mesh
                meshFilter.mesh = mesh;
                
                // Store reference
                activeChunks[chunkCoord] = chunkObj;
                
                // Update chunk state
                worldManager.MarkChunkHasMesh(chunkCoord);
            }
            else if (vertexCount == 0)
            {
                // Empty chunk - still mark as having mesh to avoid reprocessing
                worldManager.MarkChunkHasMesh(chunkCoord);
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
            }
            
            // Release buffers
            vertexBuffer?.Release();
            normalBuffer?.Release();
            indexBuffer?.Release();
            counterBuffer?.Release();
            argsBuffer?.Release();
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