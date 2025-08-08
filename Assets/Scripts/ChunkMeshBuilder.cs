// ChunkMeshBuilder.cs - Updated for Unity 6.1 with GraphicsBuffer
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

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
        
        // Buffers for mesh extraction - Updated to use GraphicsBuffer
        private GraphicsBuffer vertexBuffer;
        private GraphicsBuffer normalBuffer;
        private GraphicsBuffer indexBuffer;
        private GraphicsBuffer counterBuffer;
        
        // Temporary buffers for extraction process
        private GraphicsBuffer vertexCountBuffer;
        private GraphicsBuffer vertexOffsetBuffer;
        private GraphicsBuffer totalCountBuffer;
        
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
            // Create GraphicsBuffers instead of ComputeBuffers for Unity 6.1
            vertexBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                maxVerticesPerChunk,
                sizeof(float) * 3
            );
            
            normalBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                maxVerticesPerChunk,
                sizeof(float) * 3
            );
            
            indexBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                maxVerticesPerChunk,
                sizeof(uint)
            );
            
            // Counter buffer
            counterBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                1,
                sizeof(uint)
            );
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

            // Find kernels
            int countKernel = -1;
            int extractKernel = -1;

            try
            {
                countKernel = meshExtractionShader.FindKernel("CountVertices");
                extractKernel = meshExtractionShader.FindKernel("ExtractMesh");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to find kernels: {e.Message}");
                yield break;
            }

            const int VOXELS_PER_CHUNK = 32 * 32 * 32;

            // Create temporary GraphicsBuffers for counting
            if (vertexCountBuffer != null) vertexCountBuffer.Dispose();
            if (vertexOffsetBuffer != null) vertexOffsetBuffer.Dispose();
            if (totalCountBuffer != null) totalCountBuffer.Dispose();
            
            vertexCountBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                VOXELS_PER_CHUNK,
                sizeof(uint)
            );
            
            vertexOffsetBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                VOXELS_PER_CHUNK,
                sizeof(uint)
            );
            
            totalCountBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                1,
                sizeof(uint)
            );

            // Clear buffers
            uint[] clearData = new uint[VOXELS_PER_CHUNK];
            vertexCountBuffer.SetData(clearData);
            vertexOffsetBuffer.SetData(clearData);
            totalCountBuffer.SetData(new uint[] { 0 });

            // PASS 1: Count vertices needed per voxel
            meshExtractionShader.SetTexture(countKernel, "WorldData", worldDataTexture);
            meshExtractionShader.SetInts("ChunkCoord", chunkCoord.x, chunkCoord.y, chunkCoord.z);
            meshExtractionShader.SetFloat("VoxelSize", worldManager.VoxelSize);
            meshExtractionShader.SetInt("ChunkSize", TerrainWorldManager.CHUNK_SIZE);

            meshExtractionShader.SetBuffer(countKernel, "VertexCountPerVoxel", vertexCountBuffer);
            meshExtractionShader.SetBuffer(countKernel, "TotalVertexCounter", totalCountBuffer);

            // Dispatch with proper thread groups (32/8 = 4 groups per dimension)
            int threadGroups = Mathf.CeilToInt(TerrainWorldManager.CHUNK_SIZE / 8.0f);
            meshExtractionShader.Dispatch(countKernel, threadGroups, threadGroups, threadGroups);

            // Read back the total vertex count
            var totalRequest = AsyncGPUReadback.Request(totalCountBuffer);
            yield return new WaitUntil(() => totalRequest.done);

            if (totalRequest.hasError)
            {
                Debug.LogError($"Failed to read vertex count for chunk {chunkCoord}");
                CleanupTemporaryBuffers();
                yield break;
            }

            uint totalVertices = totalRequest.GetData<uint>()[0];
            Debug.Log($"Chunk {chunkCoord}: Will generate {totalVertices} vertices");

            if (totalVertices == 0)
            {
                Debug.Log($"Chunk {chunkCoord} is empty");
                worldManager.MarkChunkHasMesh(chunkCoord);
                CleanupTemporaryBuffers();
                yield break;
            }

            if (totalVertices >= maxVerticesPerChunk)
            {
                Debug.LogWarning($"Chunk {chunkCoord} exceeds vertex limit: {totalVertices}");
                CleanupTemporaryBuffers();
                yield break;
            }

            // Calculate prefix sum for vertex offsets (on CPU for simplicity)
            var vertexCountRequest = AsyncGPUReadback.Request(vertexCountBuffer);
            yield return new WaitUntil(() => vertexCountRequest.done);

            if (!vertexCountRequest.hasError)
            {
                uint[] counts = vertexCountRequest.GetData<uint>().ToArray();
                uint[] offsets = new uint[VOXELS_PER_CHUNK];
                uint runningOffset = 0;

                for (int i = 0; i < VOXELS_PER_CHUNK; i++)
                {
                    offsets[i] = runningOffset;
                    runningOffset += counts[i];
                }

                vertexOffsetBuffer.SetData(offsets);
            }

            // Clear main buffers
            Vector3[] clearVerts = new Vector3[totalVertices];
            vertexBuffer.SetData(clearVerts);
            normalBuffer.SetData(clearVerts);
            uint[] clearIndices = new uint[totalVertices];
            indexBuffer.SetData(clearIndices);

            // PASS 2: Extract actual mesh data
            meshExtractionShader.SetTexture(extractKernel, "WorldData", worldDataTexture);
            meshExtractionShader.SetInts("ChunkCoord", chunkCoord.x, chunkCoord.y, chunkCoord.z);
            meshExtractionShader.SetFloat("VoxelSize", worldManager.VoxelSize);
            meshExtractionShader.SetInt("ChunkSize", TerrainWorldManager.CHUNK_SIZE);

            meshExtractionShader.SetBuffer(extractKernel, "VertexBuffer", vertexBuffer);
            meshExtractionShader.SetBuffer(extractKernel, "NormalBuffer", normalBuffer);
            meshExtractionShader.SetBuffer(extractKernel, "IndexBuffer", indexBuffer);
            meshExtractionShader.SetBuffer(extractKernel, "VertexCountPerVoxel", vertexCountBuffer);
            meshExtractionShader.SetBuffer(extractKernel, "VertexOffsetPerVoxel", vertexOffsetBuffer);

            // Dispatch extraction
            meshExtractionShader.Dispatch(extractKernel, threadGroups, threadGroups, threadGroups);

            // Wait for GPU to complete
            yield return new WaitForEndOfFrame();

            // Read back mesh data
            int vertexCount = (int)totalVertices;
            Vector3[] vertices = new Vector3[vertexCount];
            Vector3[] normals = new Vector3[vertexCount];
            int[] indices = new int[vertexCount];

            var vertexRequest = AsyncGPUReadback.Request(vertexBuffer, vertexCount * sizeof(float) * 3, 0);
            var normalRequest = AsyncGPUReadback.Request(normalBuffer, vertexCount * sizeof(float) * 3, 0);
            var indexRequest = AsyncGPUReadback.Request(indexBuffer, vertexCount * sizeof(uint), 0);

            yield return new WaitUntil(() => vertexRequest.done && normalRequest.done && indexRequest.done);

            if (vertexRequest.hasError || normalRequest.hasError || indexRequest.hasError)
            {
                Debug.LogError($"GPU Readback error for chunk {chunkCoord}");
                CleanupTemporaryBuffers();
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

            // Clean up temporary buffers
            CleanupTemporaryBuffers();

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

            if (smoothNormals)
            {
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
            }

            mesh.RecalculateBounds();
            mesh.Optimize();
            mesh.UploadMeshData(true);

            meshFilter.mesh = mesh;

            MeshRenderer renderer = chunkObj.GetComponent<MeshRenderer>();
            if (renderer != null && chunkMaterial != null)
            {
                renderer.material = chunkMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }

            activeChunks[chunkCoord] = chunkObj;
            worldManager.MarkChunkHasMesh(chunkCoord);

            Debug.Log($"Successfully created mesh for chunk {chunkCoord} with {vertexCount} vertices");
        }

        void CleanupTemporaryBuffers()
        {
            vertexCountBuffer?.Dispose();
            vertexCountBuffer = null;
            vertexOffsetBuffer?.Dispose();
            vertexOffsetBuffer = null;
            totalCountBuffer?.Dispose();
            totalCountBuffer = null;
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
            
            // Dispose GraphicsBuffers instead of Release
            vertexBuffer?.Dispose();
            normalBuffer?.Dispose();
            indexBuffer?.Dispose();
            counterBuffer?.Dispose();
            
            // Clean up temporary buffers
            CleanupTemporaryBuffers();
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