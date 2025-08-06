// ChunkMeshBuilder.cs - Updated for smooth mesh generation
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
        [SerializeField] private bool smoothNormals = true;
        
        // Buffers for mesh extraction - Changed to structured buffers
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
            // Create structured buffers for better control
            vertexBuffer = new ComputeBuffer(maxVerticesPerChunk, sizeof(float) * 3);
            normalBuffer = new ComputeBuffer(maxVerticesPerChunk, sizeof(float) * 3);
            indexBuffer = new ComputeBuffer(maxVerticesPerChunk, sizeof(uint));
            
            // Counter buffer
            counterBuffer = new ComputeBuffer(1, sizeof(uint));
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
                var chunkStates = worldManager.GetChunkStates();
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
            
            // Clear buffers
            counterBuffer.SetData(new uint[] { 0 });
            
            // Clear vertex and normal buffers
            Vector3[] clearVerts = new Vector3[maxVerticesPerChunk];
            vertexBuffer.SetData(clearVerts);
            normalBuffer.SetData(clearVerts);
            
            uint[] clearIndices = new uint[maxVerticesPerChunk];
            indexBuffer.SetData(clearIndices);
            
            // Set parameters
            meshExtractionShader.SetTexture(kernel, "WorldData", worldDataTexture);
            meshExtractionShader.SetVector("ChunkCoord", new Vector4(chunkCoord.x, chunkCoord.y, chunkCoord.z, 0));
            meshExtractionShader.SetFloat("VoxelSize", worldManager.VoxelSize);
            meshExtractionShader.SetInt("ChunkSize", TerrainWorldManager.CHUNK_SIZE);
            
            // Set buffers - using structured buffers now
            meshExtractionShader.SetBuffer(kernel, "VertexBuffer", vertexBuffer);
            meshExtractionShader.SetBuffer(kernel, "NormalBuffer", normalBuffer);
            meshExtractionShader.SetBuffer(kernel, "IndexBuffer", indexBuffer);
            meshExtractionShader.SetBuffer(kernel, "VertexCounter", counterBuffer);
            
            // Dispatch - using single thread group for entire chunk processing
            meshExtractionShader.Dispatch(kernel, 1, 1, 1);
            
            // Wait for GPU to finish
            yield return null;
            
            // Get vertex count
            uint[] count = new uint[1];
            counterBuffer.GetData(count);
            int vertexCount = (int)count[0];
            
            Debug.Log($"Chunk {chunkCoord}: Generated {vertexCount} vertices");
            
            if (vertexCount > 0 && vertexCount < maxVerticesPerChunk)
            {
                // Read back data
                Vector3[] vertices = new Vector3[vertexCount];
                Vector3[] normals = new Vector3[vertexCount];
                int[] indices = new int[vertexCount];
                
                // Use async readback for better performance
                var vertexRequest = AsyncGPUReadback.Request(vertexBuffer, vertexCount * sizeof(float) * 3, 0);
                var normalRequest = AsyncGPUReadback.Request(normalBuffer, vertexCount * sizeof(float) * 3, 0);
                var indexRequest = AsyncGPUReadback.Request(indexBuffer, vertexCount * sizeof(uint), 0);
                
                yield return new WaitUntil(() => vertexRequest.done && normalRequest.done && indexRequest.done);
                
                if (vertexRequest.hasError || normalRequest.hasError || indexRequest.hasError)
                {
                    Debug.LogError($"GPU Readback error for chunk {chunkCoord}");
                    yield break;
                }
                
                // Get data from readback
                var vertexData = vertexRequest.GetData<Vector3>();
                var normalData = normalRequest.GetData<Vector3>();
                var indexData = indexRequest.GetData<uint>();
                
                vertexData.CopyTo(vertices);
                normalData.CopyTo(normals);
                
                // Convert indices
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
                
                // Optional: Weld vertices to remove duplicates and create smoother mesh
                if (smoothNormals)
                {
                    mesh.RecalculateNormals();
                    mesh.RecalculateTangents();
                }
                
                // Calculate bounds
                mesh.RecalculateBounds();
                
                // Optimize mesh
                mesh.Optimize();
                mesh.UploadMeshData(true); // Mark as no longer readable to save memory
                
                // Assign mesh
                meshFilter.mesh = mesh;
                
                // Update material properties for better rendering
                MeshRenderer renderer = chunkObj.GetComponent<MeshRenderer>();
                if (renderer != null && chunkMaterial != null)
                {
                    renderer.material = chunkMaterial;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    renderer.receiveShadows = true;
                }
                
                // Store reference
                activeChunks[chunkCoord] = chunkObj;
                
                // Update chunk state
                worldManager.MarkChunkHasMesh(chunkCoord);
                
                Debug.Log($"Successfully created smooth mesh for chunk {chunkCoord}");
            }
            else if (vertexCount == 0)
            {
                Debug.Log($"Chunk {chunkCoord} is empty");
                worldManager.MarkChunkHasMesh(chunkCoord);
            }
            else if (vertexCount >= maxVerticesPerChunk)
            {
                Debug.LogWarning($"Chunk {chunkCoord} exceeded max vertices: {vertexCount}");
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
            foreach (var chunk in activeChunks.Values)
            {
                MeshFilter meshFilter = chunk.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.mesh != null)
                {
                    DestroyImmediate(meshFilter.mesh);
                }
            }
            
            vertexBuffer?.Release();
            normalBuffer?.Release();
            indexBuffer?.Release();
            counterBuffer?.Release();
        }
        
        void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying) return;
            
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