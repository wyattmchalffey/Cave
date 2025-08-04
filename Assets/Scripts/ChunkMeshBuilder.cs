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
            // Create buffers for mesh extraction - using append buffers for CompleteMeshExtraction
            vertexBuffer = new ComputeBuffer(maxVerticesPerChunk, sizeof(float) * 3, ComputeBufferType.Append);
            normalBuffer = new ComputeBuffer(maxVerticesPerChunk, sizeof(float) * 3, ComputeBufferType.Append);
            indexBuffer = new ComputeBuffer(maxVerticesPerChunk, sizeof(int), ComputeBufferType.Append);
            
            // Counter buffer to track how many vertices were generated
            counterBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
            
            Debug.Log("ChunkMeshBuilder initialized with append buffers");
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
                Debug.LogError("Missing resources for mesh extraction!");
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
                Debug.LogError("Invalid kernel index for ExtractMesh");
                yield break;
            }
            
            // Reset counter buffer
            counterBuffer.SetData(new int[] { 0 });
            
            // Set parameters
            meshExtractionShader.SetTexture(kernel, "WorldData", worldDataTexture);
            meshExtractionShader.SetVector("ChunkCoord", new Vector4(chunkCoord.x, chunkCoord.y, chunkCoord.z, 0));
            meshExtractionShader.SetFloat("VoxelSize", worldManager.VoxelSize);
            meshExtractionShader.SetInt("ChunkSize", TerrainWorldManager.CHUNK_SIZE);
            
            // Set buffers - check what names the shader expects
            string vertexBufferName = "Vertices";
            string normalBufferName = "Normals";
            string indexBufferName = "Indices";
            string counterBufferName = "VertexCount";
            
            // Some shaders might use different names
            if (meshExtractionShader.name.Contains("MeshExtraction"))
            {
                // If it's looking for VertexPool, use that
                try
                {
                    meshExtractionShader.SetBuffer(kernel, "VertexPool", vertexBuffer);
                }
                catch
                {
                    meshExtractionShader.SetBuffer(kernel, vertexBufferName, vertexBuffer);
                }
            }
            else
            {
                meshExtractionShader.SetBuffer(kernel, vertexBufferName, vertexBuffer);
            }
            
            meshExtractionShader.SetBuffer(kernel, normalBufferName, normalBuffer);
            meshExtractionShader.SetBuffer(kernel, indexBufferName, indexBuffer);
            meshExtractionShader.SetBuffer(kernel, counterBufferName, counterBuffer);
            
            // Dispatch
            int threadGroups = Mathf.CeilToInt(TerrainWorldManager.CHUNK_SIZE / 8.0f);
            meshExtractionShader.Dispatch(kernel, threadGroups, threadGroups, threadGroups);
            
            // Wait a frame for GPU to finish
            yield return null;
            
            // Get vertex count
            int[] countData = new int[1];
            counterBuffer.GetData(countData);
            int vertexCount = countData[0];
            
            if (vertexCount > 0 && vertexCount < maxVerticesPerChunk)
            {
                // Read back data
                Vector3[] vertices = new Vector3[vertexCount];
                Vector3[] normals = new Vector3[vertexCount];
                int[] indices = new int[vertexCount];
                
                vertexBuffer.GetData(vertices, 0, 0, vertexCount);
                normalBuffer.GetData(normals, 0, 0, vertexCount);
                indexBuffer.GetData(indices, 0, 0, vertexCount);
                
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
                
                // Assign mesh
                meshFilter.mesh = mesh;
                
                // Store reference
                activeChunks[chunkCoord] = chunkObj;
                
                Debug.Log($"Built mesh for chunk {chunkCoord} with {vertexCount} vertices");
            }
            else if (vertexCount == 0)
            {
                // Empty chunk
                Debug.Log($"Chunk {chunkCoord} is empty");
            }
            else
            {
                Debug.LogWarning($"Chunk {chunkCoord} has too many vertices: {vertexCount}");
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
                renderer.material = chunkMaterial;
            }
            
            // Position chunk
            float chunkWorldSize = worldManager.VoxelSize * TerrainWorldManager.CHUNK_SIZE;
            chunkObj.transform.position = new Vector3(
                coord.x * chunkWorldSize,
                coord.y * chunkWorldSize,
                coord.z * chunkWorldSize
            );
            
            chunkObj.transform.parent = transform;
            
            return chunkObj;
        }
        
        public void ClearChunk(int3 coord)
        {
            if (activeChunks.TryGetValue(coord, out GameObject chunkObj))
            {
                chunkObj.SetActive(false);
                meshPool.Enqueue(chunkObj);
                activeChunks.Remove(coord);
            }
        }
        
        void OnDestroy()
        {
            vertexBuffer?.Release();
            normalBuffer?.Release();
            indexBuffer?.Release();
            counterBuffer?.Release();
        }
    }
}