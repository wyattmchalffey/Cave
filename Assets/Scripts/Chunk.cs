using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

public class Chunk : MonoBehaviour
{
    public const int CHUNK_SIZE = 32;
    public const float VOXEL_SIZE = 0.25f;
    
    private float[,,] voxelData;
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;
    
    public Vector3Int Coordinate { get; private set; }
    public int LODLevel { get; private set; }
    
    // Mesh generation
    private List<Vector3> vertices = new List<Vector3>();
    private List<int> triangles = new List<int>();
    private List<Vector3> normals = new List<Vector3>();
    private List<Vector2> uvs = new List<Vector2>();
    
    // Marching cubes lookup tables
    private static readonly int[] edgeTable = new int[256];
    private static readonly int[,] triTable = new int[256, 16];
    
    void Awake()
    {
        voxelData = new float[CHUNK_SIZE + 1, CHUNK_SIZE + 1, CHUNK_SIZE + 1];
        
        meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null)
            meshFilter = gameObject.AddComponent<MeshFilter>();
            
        meshRenderer = GetComponent<MeshRenderer>();
        if (meshRenderer == null)
            meshRenderer = gameObject.AddComponent<MeshRenderer>();
            
        meshCollider = GetComponent<MeshCollider>();
        if (meshCollider == null)
            meshCollider = gameObject.AddComponent<MeshCollider>();
            
        InitializeMarchingCubesTables();
    }
    
    public void Initialize(Vector3Int coordinate, Material material, int lodLevel = 0)
    {
        Coordinate = coordinate;
        LODLevel = lodLevel;
        transform.position = new Vector3(
            coordinate.x * CHUNK_SIZE * VOXEL_SIZE,
            coordinate.y * CHUNK_SIZE * VOXEL_SIZE,
            coordinate.z * CHUNK_SIZE * VOXEL_SIZE
        );
        
        if (material != null)
            meshRenderer.material = material;
    }
    
    public void SetVoxelDataFromJob(NativeArray<float> jobData)
    {
        int index = 0;
        for (int x = 0; x <= CHUNK_SIZE; x++)
        {
            for (int y = 0; y <= CHUNK_SIZE; y++)
            {
                for (int z = 0; z <= CHUNK_SIZE; z++)
                {
                    if (index < jobData.Length)
                        voxelData[x, y, z] = jobData[index++];
                }
            }
        }
    }
    
    public void GenerateSmoothMesh()
    {
        vertices.Clear();
        triangles.Clear();
        normals.Clear();
        uvs.Clear();
        
        // Debug: Check density values
        if (LODLevel == 0 && Coordinate == Vector3Int.zero)
        {
            int solidCount = 0;
            int airCount = 0;
            for (int x = 0; x <= CHUNK_SIZE; x++)
            {
                for (int y = 0; y <= CHUNK_SIZE; y++)
                {
                    for (int z = 0; z <= CHUNK_SIZE; z++)
                    {
                        if (voxelData[x, y, z] > 0.5f) solidCount++;
                        else airCount++;
                    }
                }
            }
            Debug.Log($"Chunk {Coordinate}: Solid voxels: {solidCount}, Air voxels: {airCount}");
        }
        
        // Use simple voxel mesh for testing
        // GenerateMarchingCubesMesh();
        GenerateSimpleVoxelMesh();
        
        if (vertices.Count == 0)
        {
            // No geometry to generate
            if (meshFilter.mesh != null)
            {
                meshFilter.mesh.Clear();
            }
            return;
        }
        
        // Create mesh
        Mesh mesh = new Mesh();
        mesh.name = "Cave Chunk Mesh";
        
        // Use 32-bit indices for meshes with many vertices
        if (vertices.Count > 65535)
        {
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }
        
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        
        // Calculate normals if not already done
        if (normals.Count == vertices.Count)
        {
            mesh.normals = normals.ToArray();
        }
        else
        {
            mesh.RecalculateNormals();
        }
        
        mesh.uv = uvs.ToArray();
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mesh.Optimize();
        
        meshFilter.mesh = mesh;
        
        // Update collider with simplified mesh if needed
        if (meshCollider.enabled && LODLevel == 0)
        {
            meshCollider.sharedMesh = mesh;
        }
    }
    
    void GenerateMarchingCubesMesh()
    {
        float isoLevel = 0.5f; // Surface level
        int stepSize = 1 << LODLevel; // Skip voxels for LOD
        
        for (int x = 0; x < CHUNK_SIZE; x += stepSize)
        {
            for (int y = 0; y < CHUNK_SIZE; y += stepSize)
            {
                for (int z = 0; z < CHUNK_SIZE; z += stepSize)
                {
                    ProcessMarchingCubesVoxel(x, y, z, isoLevel, stepSize);
                }
            }
        }
    }
    
    void ProcessMarchingCubesVoxel(int x, int y, int z, float isoLevel, int stepSize)
    {
        // Get the 8 corner values
        float[] cubeCorners = new float[8];
        cubeCorners[0] = GetVoxelValue(x, y, z);
        cubeCorners[1] = GetVoxelValue(x + stepSize, y, z);
        cubeCorners[2] = GetVoxelValue(x + stepSize, y, z + stepSize);
        cubeCorners[3] = GetVoxelValue(x, y, z + stepSize);
        cubeCorners[4] = GetVoxelValue(x, y + stepSize, z);
        cubeCorners[5] = GetVoxelValue(x + stepSize, y + stepSize, z);
        cubeCorners[6] = GetVoxelValue(x + stepSize, y + stepSize, z + stepSize);
        cubeCorners[7] = GetVoxelValue(x, y + stepSize, z + stepSize);
        
        // Calculate the cube index
        int cubeIndex = 0;
        for (int i = 0; i < 8; i++)
        {
            if (cubeCorners[i] < isoLevel)
                cubeIndex |= (1 << i);
        }
        
        // Skip if cube is entirely inside or outside
        if (cubeIndex == 0 || cubeIndex == 255)
            return;
        
        // Get vertices positions
        Vector3[] vertexPositions = new Vector3[8];
        vertexPositions[0] = new Vector3(x, y, z) * VOXEL_SIZE;
        vertexPositions[1] = new Vector3(x + stepSize, y, z) * VOXEL_SIZE;
        vertexPositions[2] = new Vector3(x + stepSize, y, z + stepSize) * VOXEL_SIZE;
        vertexPositions[3] = new Vector3(x, y, z + stepSize) * VOXEL_SIZE;
        vertexPositions[4] = new Vector3(x, y + stepSize, z) * VOXEL_SIZE;
        vertexPositions[5] = new Vector3(x + stepSize, y + stepSize, z) * VOXEL_SIZE;
        vertexPositions[6] = new Vector3(x + stepSize, y + stepSize, z + stepSize) * VOXEL_SIZE;
        vertexPositions[7] = new Vector3(x, y + stepSize, z + stepSize) * VOXEL_SIZE;
        
        // Find the vertices where the surface intersects the cube
        Vector3[] vertexList = new Vector3[12];
        
        if ((edgeTable[cubeIndex] & 1) != 0)
            vertexList[0] = VertexInterp(isoLevel, vertexPositions[0], vertexPositions[1], cubeCorners[0], cubeCorners[1]);
        if ((edgeTable[cubeIndex] & 2) != 0)
            vertexList[1] = VertexInterp(isoLevel, vertexPositions[1], vertexPositions[2], cubeCorners[1], cubeCorners[2]);
        if ((edgeTable[cubeIndex] & 4) != 0)
            vertexList[2] = VertexInterp(isoLevel, vertexPositions[2], vertexPositions[3], cubeCorners[2], cubeCorners[3]);
        if ((edgeTable[cubeIndex] & 8) != 0)
            vertexList[3] = VertexInterp(isoLevel, vertexPositions[3], vertexPositions[0], cubeCorners[3], cubeCorners[0]);
        if ((edgeTable[cubeIndex] & 16) != 0)
            vertexList[4] = VertexInterp(isoLevel, vertexPositions[4], vertexPositions[5], cubeCorners[4], cubeCorners[5]);
        if ((edgeTable[cubeIndex] & 32) != 0)
            vertexList[5] = VertexInterp(isoLevel, vertexPositions[5], vertexPositions[6], cubeCorners[5], cubeCorners[6]);
        if ((edgeTable[cubeIndex] & 64) != 0)
            vertexList[6] = VertexInterp(isoLevel, vertexPositions[6], vertexPositions[7], cubeCorners[6], cubeCorners[7]);
        if ((edgeTable[cubeIndex] & 128) != 0)
            vertexList[7] = VertexInterp(isoLevel, vertexPositions[7], vertexPositions[4], cubeCorners[7], cubeCorners[4]);
        if ((edgeTable[cubeIndex] & 256) != 0)
            vertexList[8] = VertexInterp(isoLevel, vertexPositions[0], vertexPositions[4], cubeCorners[0], cubeCorners[4]);
        if ((edgeTable[cubeIndex] & 512) != 0)
            vertexList[9] = VertexInterp(isoLevel, vertexPositions[1], vertexPositions[5], cubeCorners[1], cubeCorners[5]);
        if ((edgeTable[cubeIndex] & 1024) != 0)
            vertexList[10] = VertexInterp(isoLevel, vertexPositions[2], vertexPositions[6], cubeCorners[2], cubeCorners[6]);
        if ((edgeTable[cubeIndex] & 2048) != 0)
            vertexList[11] = VertexInterp(isoLevel, vertexPositions[3], vertexPositions[7], cubeCorners[3], cubeCorners[7]);
        
        // Create triangles
        for (int i = 0; triTable[cubeIndex, i] != -1; i += 3)
        {
            int vertexIndex = vertices.Count;
            
            Vector3 v1 = vertexList[triTable[cubeIndex, i]];
            Vector3 v2 = vertexList[triTable[cubeIndex, i + 1]];
            Vector3 v3 = vertexList[triTable[cubeIndex, i + 2]];
            
            vertices.Add(v1);
            vertices.Add(v2);
            vertices.Add(v3);
            
            // Calculate normal
            Vector3 normal = Vector3.Cross(v2 - v1, v3 - v1).normalized;
            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);
            
            // UVs based on world position
            uvs.Add(new Vector2(v1.x * 0.1f, v1.z * 0.1f));
            uvs.Add(new Vector2(v2.x * 0.1f, v2.z * 0.1f));
            uvs.Add(new Vector2(v3.x * 0.1f, v3.z * 0.1f));
            
            triangles.Add(vertexIndex);
            triangles.Add(vertexIndex + 1);
            triangles.Add(vertexIndex + 2);
        }
    }
    
    Vector3 VertexInterp(float isolevel, Vector3 p1, Vector3 p2, float valp1, float valp2)
    {
        if (Mathf.Abs(isolevel - valp1) < 0.00001f)
            return p1;
        if (Mathf.Abs(isolevel - valp2) < 0.00001f)
            return p2;
        if (Mathf.Abs(valp1 - valp2) < 0.00001f)
            return p1;
        
        float mu = (isolevel - valp1) / (valp2 - valp1);
        return Vector3.Lerp(p1, p2, mu);
    }
    
    float GetVoxelValue(int x, int y, int z)
    {
        x = Mathf.Clamp(x, 0, CHUNK_SIZE);
        y = Mathf.Clamp(y, 0, CHUNK_SIZE);
        z = Mathf.Clamp(z, 0, CHUNK_SIZE);
        return voxelData[x, y, z];
    }
    
    void InitializeMarchingCubesTables()
    {
        // For testing, use simple voxel rendering instead
        // In production, implement full marching cubes tables
    }
    
    // Alternative simple voxel mesh generation for testing
    public void GenerateSimpleVoxelMesh()
    {
        vertices.Clear();
        triangles.Clear();
        normals.Clear();
        uvs.Clear();
        
        float threshold = 0.5f;
        
        for (int x = 0; x < CHUNK_SIZE; x++)
        {
            for (int y = 0; y < CHUNK_SIZE; y++)
            {
                for (int z = 0; z < CHUNK_SIZE; z++)
                {
                    if (voxelData[x, y, z] > threshold)
                    {
                        // Check each face
                        Vector3 pos = new Vector3(x, y, z) * VOXEL_SIZE;
                        
                        // Left face (X-)
                        if (x == 0 || voxelData[x - 1, y, z] <= threshold)
                            AddQuadFace(pos, Vector3.up * VOXEL_SIZE, Vector3.forward * VOXEL_SIZE, Vector3.left);
                        
                        // Right face (X+)
                        if (x == CHUNK_SIZE - 1 || voxelData[x + 1, y, z] <= threshold)
                            AddQuadFace(pos + Vector3.right * VOXEL_SIZE, Vector3.forward * VOXEL_SIZE, Vector3.up * VOXEL_SIZE, Vector3.right);
                        
                        // Bottom face (Y-)
                        if (y == 0 || voxelData[x, y - 1, z] <= threshold)
                            AddQuadFace(pos, Vector3.forward * VOXEL_SIZE, Vector3.right * VOXEL_SIZE, Vector3.down);
                        
                        // Top face (Y+)
                        if (y == CHUNK_SIZE - 1 || voxelData[x, y + 1, z] <= threshold)
                            AddQuadFace(pos + Vector3.up * VOXEL_SIZE, Vector3.right * VOXEL_SIZE, Vector3.forward * VOXEL_SIZE, Vector3.up);
                        
                        // Back face (Z-)
                        if (z == 0 || voxelData[x, y, z - 1] <= threshold)
                            AddQuadFace(pos, Vector3.right * VOXEL_SIZE, Vector3.up * VOXEL_SIZE, Vector3.back);
                        
                        // Front face (Z+)
                        if (z == CHUNK_SIZE - 1 || voxelData[x, y, z + 1] <= threshold)
                            AddQuadFace(pos + Vector3.forward * VOXEL_SIZE, Vector3.up * VOXEL_SIZE, Vector3.right * VOXEL_SIZE, Vector3.forward);
                    }
                }
            }
        }
        
        if (vertices.Count == 0) return;
        
        // Create mesh
        Mesh mesh = new Mesh();
        mesh.name = "Simple Voxel Mesh";
        
        if (vertices.Count > 65535)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.normals = normals.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.RecalculateBounds();
        mesh.Optimize();
        
        meshFilter.mesh = mesh;
        
        if (meshCollider.enabled && LODLevel == 0)
            meshCollider.sharedMesh = mesh;
    }
    
    void AddQuadFace(Vector3 corner, Vector3 up, Vector3 right, Vector3 normal)
    {
        int vertexIndex = vertices.Count;
        
        // Add vertices
        vertices.Add(corner);
        vertices.Add(corner + up);
        vertices.Add(corner + up + right);
        vertices.Add(corner + right);
        
        // Add UVs
        uvs.Add(new Vector2(0, 0));
        uvs.Add(new Vector2(0, 1));
        uvs.Add(new Vector2(1, 1));
        uvs.Add(new Vector2(1, 0));
        
        // Add normals
        for (int i = 0; i < 4; i++)
            normals.Add(normal);
        
        // Add triangles
        triangles.Add(vertexIndex);
        triangles.Add(vertexIndex + 1);
        triangles.Add(vertexIndex + 2);
        
        triangles.Add(vertexIndex);
        triangles.Add(vertexIndex + 2);
        triangles.Add(vertexIndex + 3);
    }
}