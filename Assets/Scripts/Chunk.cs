using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class Chunk : MonoBehaviour
{
    // Chunk properties
    public Vector3Int coordinate { get; private set; }
    public int chunkSize { get; private set; }
    public float voxelSize { get; private set; }
    public float[,,] voxelData { get; set; }
    
    // Mesh components
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;
    private Mesh mesh;
    
    // Generation data
    public JobHandle? generationHandle;
    public NativeArray<float> densityField;
    
    // Mesh data
    private List<Vector3> vertices = new List<Vector3>();
    private List<int> triangles = new List<int>();
    private List<Vector3> normals = new List<Vector3>();
    private List<Vector2> uvs = new List<Vector2>();
    
    // Constants
    private const float ISO_LEVEL = 0.5f;
    
    void Awake()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        meshCollider = GetComponent<MeshCollider>();
        
        mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32; // Support for more vertices
        meshFilter.mesh = mesh;
    }
    
    public void Initialize(Vector3Int coord, int size, float voxelScale)
    {
        coordinate = coord;
        chunkSize = size;
        voxelSize = voxelScale;
        name = $"Chunk_{coord.x}_{coord.y}_{coord.z}";
        
        // Initialize voxel data array
        voxelData = new float[chunkSize + 1, chunkSize + 1, chunkSize + 1];
    }
    
    public void Initialize(Vector3Int coord, Material material, int size, float voxelScale)
    {
        Initialize(coord, size, voxelScale);
        
        if (meshRenderer != null && material != null)
        {
            meshRenderer.material = material;
        }
    }
    
    public void GenerateMesh()
    {
        vertices.Clear();
        triangles.Clear();
        normals.Clear();
        uvs.Clear();
        
        // Generate mesh using marching cubes
        for (int x = 0; x < chunkSize; x++)
        {
            for (int y = 0; y < chunkSize; y++)
            {
                for (int z = 0; z < chunkSize; z++)
                {
                    ProcessVoxel(x, y, z);
                }
            }
        }
        
        UpdateMesh();
    }
    
    void ProcessVoxel(int x, int y, int z)
    {
        // Sample density at 8 corners of the voxel
        float[] cornerDensities = new float[8];
        cornerDensities[0] = voxelData[x, y, z];
        cornerDensities[1] = voxelData[x + 1, y, z];
        cornerDensities[2] = voxelData[x + 1, y + 1, z];
        cornerDensities[3] = voxelData[x, y + 1, z];
        cornerDensities[4] = voxelData[x, y, z + 1];
        cornerDensities[5] = voxelData[x + 1, y, z + 1];
        cornerDensities[6] = voxelData[x + 1, y + 1, z + 1];
        cornerDensities[7] = voxelData[x, y + 1, z + 1];
        
        // Calculate cube index
        int cubeIndex = 0;
        for (int i = 0; i < 8; i++)
        {
            if (cornerDensities[i] < ISO_LEVEL)
                cubeIndex |= (1 << i);
        }
        
        // Skip if cube is entirely inside or outside
        if (cubeIndex == 0 || cubeIndex == 255)
            return;
        
        // Use edge table to find intersected edges
        int edgeFlags = MarchingCubesTables.EdgeTable[cubeIndex];
        if (edgeFlags == 0)
            return;
        
        // Calculate vertex positions on edges
        Vector3[] edgeVertices = new Vector3[12];
        Vector3 basePos = new Vector3(x, y, z) * voxelSize;
        
        if ((edgeFlags & 1) != 0)
            edgeVertices[0] = VertexInterp(basePos, basePos + Vector3.right * voxelSize, cornerDensities[0], cornerDensities[1]);
        if ((edgeFlags & 2) != 0)
            edgeVertices[1] = VertexInterp(basePos + Vector3.right * voxelSize, basePos + new Vector3(1, 1, 0) * voxelSize, cornerDensities[1], cornerDensities[2]);
        if ((edgeFlags & 4) != 0)
            edgeVertices[2] = VertexInterp(basePos + new Vector3(1, 1, 0) * voxelSize, basePos + Vector3.up * voxelSize, cornerDensities[2], cornerDensities[3]);
        if ((edgeFlags & 8) != 0)
            edgeVertices[3] = VertexInterp(basePos + Vector3.up * voxelSize, basePos, cornerDensities[3], cornerDensities[0]);
        if ((edgeFlags & 16) != 0)
            edgeVertices[4] = VertexInterp(basePos + Vector3.forward * voxelSize, basePos + new Vector3(1, 0, 1) * voxelSize, cornerDensities[4], cornerDensities[5]);
        if ((edgeFlags & 32) != 0)
            edgeVertices[5] = VertexInterp(basePos + new Vector3(1, 0, 1) * voxelSize, basePos + new Vector3(1, 1, 1) * voxelSize, cornerDensities[5], cornerDensities[6]);
        if ((edgeFlags & 64) != 0)
            edgeVertices[6] = VertexInterp(basePos + new Vector3(1, 1, 1) * voxelSize, basePos + new Vector3(0, 1, 1) * voxelSize, cornerDensities[6], cornerDensities[7]);
        if ((edgeFlags & 128) != 0)
            edgeVertices[7] = VertexInterp(basePos + new Vector3(0, 1, 1) * voxelSize, basePos + Vector3.forward * voxelSize, cornerDensities[7], cornerDensities[4]);
        if ((edgeFlags & 256) != 0)
            edgeVertices[8] = VertexInterp(basePos, basePos + Vector3.forward * voxelSize, cornerDensities[0], cornerDensities[4]);
        if ((edgeFlags & 512) != 0)
            edgeVertices[9] = VertexInterp(basePos + Vector3.right * voxelSize, basePos + new Vector3(1, 0, 1) * voxelSize, cornerDensities[1], cornerDensities[5]);
        if ((edgeFlags & 1024) != 0)
            edgeVertices[10] = VertexInterp(basePos + new Vector3(1, 1, 0) * voxelSize, basePos + new Vector3(1, 1, 1) * voxelSize, cornerDensities[2], cornerDensities[6]);
        if ((edgeFlags & 2048) != 0)
            edgeVertices[11] = VertexInterp(basePos + Vector3.up * voxelSize, basePos + new Vector3(0, 1, 1) * voxelSize, cornerDensities[3], cornerDensities[7]);
        
        // Create triangles
        for (int i = 0; MarchingCubesTables.TriTable[cubeIndex * 16 + i] != -1; i += 3)
        {
            int vertexIndex = vertices.Count;
            
            Vector3 v1 = edgeVertices[MarchingCubesTables.TriTable[cubeIndex * 16 + i]];
            Vector3 v2 = edgeVertices[MarchingCubesTables.TriTable[cubeIndex * 16 + i + 1]];
            Vector3 v3 = edgeVertices[MarchingCubesTables.TriTable[cubeIndex * 16 + i + 2]];
            
            vertices.Add(v1);
            vertices.Add(v2);
            vertices.Add(v3);
            
            // Calculate normal
            Vector3 normal = Vector3.Cross(v2 - v1, v3 - v1).normalized;
            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);
            
            // Simple UV mapping
            uvs.Add(new Vector2(v1.x * 0.1f, v1.z * 0.1f));
            uvs.Add(new Vector2(v2.x * 0.1f, v2.z * 0.1f));
            uvs.Add(new Vector2(v3.x * 0.1f, v3.z * 0.1f));
            
            triangles.Add(vertexIndex);
            triangles.Add(vertexIndex + 1);
            triangles.Add(vertexIndex + 2);
        }
    }
    
    Vector3 VertexInterp(Vector3 p1, Vector3 p2, float v1, float v2)
    {
        if (Mathf.Abs(ISO_LEVEL - v1) < 0.00001f)
            return p1;
        if (Mathf.Abs(ISO_LEVEL - v2) < 0.00001f)
            return p2;
        if (Mathf.Abs(v1 - v2) < 0.00001f)
            return p1;
        
        float mu = (ISO_LEVEL - v1) / (v2 - v1);
        return Vector3.Lerp(p1, p2, mu);
    }
    
    void UpdateMesh()
    {
        if (mesh == null) return;
        
        mesh.Clear();
        
        if (vertices.Count > 0)
        {
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            
            mesh.RecalculateBounds();
            
            // Update collision mesh
            if (meshCollider != null)
            {
                meshCollider.sharedMesh = mesh;
            }
        }
    }
    
    public void SetMeshData(Vector3[] verts, int[] tris, Vector3[] norms, Vector2[] uvCoords)
    {
        vertices.Clear();
        triangles.Clear();
        normals.Clear();
        uvs.Clear();
        
        vertices.AddRange(verts);
        triangles.AddRange(tris);
        normals.AddRange(norms);
        uvs.AddRange(uvCoords);
        
        UpdateMesh();
    }
    
    public int GetVertexCount()
    {
        return vertices.Count;
    }
    
    public void SetDensityFromNativeArray(NativeArray<float> densityData)
    {
        if (!densityData.IsCreated) return;
        
        int index = 0;
        for (int z = 0; z <= chunkSize; z++)
        {
            for (int y = 0; y <= chunkSize; y++)
            {
                for (int x = 0; x <= chunkSize; x++)
                {
                    voxelData[x, y, z] = densityData[index++];
                }
            }
        }
    }
    
    void OnDestroy()
    {
        if (densityField.IsCreated)
        {
            densityField.Dispose();
        }
        
        if (mesh != null)
        {
            if (Application.isPlaying)
                Destroy(mesh);
            else
                DestroyImmediate(mesh);
        }
    }
}