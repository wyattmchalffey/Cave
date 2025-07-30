using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

public class Chunk : MonoBehaviour
{
    public const int CHUNK_SIZE = 16;
    
    private float[,,] voxelData;
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;
    
    public Vector3Int Coordinate { get; private set; }
    
    // Mesh generation optimization
    private List<Vector3> vertices = new List<Vector3>();
    private List<int> triangles = new List<int>();
    private List<Vector3> normals = new List<Vector3>();
    
    // Mesh generation mode
    public enum MeshingMode { Simple, Greedy }
    public MeshingMode meshingMode = MeshingMode.Greedy;
    
    void Awake()
    {
        voxelData = new float[CHUNK_SIZE, CHUNK_SIZE, CHUNK_SIZE];
        
        meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null)
            meshFilter = gameObject.AddComponent<MeshFilter>();
            
        meshRenderer = GetComponent<MeshRenderer>();
        if (meshRenderer == null)
            meshRenderer = gameObject.AddComponent<MeshRenderer>();
            
        meshCollider = GetComponent<MeshCollider>();
        if (meshCollider == null)
            meshCollider = gameObject.AddComponent<MeshCollider>();
    }
    
    public void Initialize(Vector3Int coordinate, Material material)
    {
        Coordinate = coordinate;
        transform.position = new Vector3(
            coordinate.x * CHUNK_SIZE,
            coordinate.y * CHUNK_SIZE,
            coordinate.z * CHUNK_SIZE
        );
        
        if (material != null)
            meshRenderer.material = material;
    }
    
    public void SetVoxelDataFromJob(NativeArray<float> jobData)
    {
        int index = 0;
        for (int x = 0; x < CHUNK_SIZE; x++)
        {
            for (int y = 0; y < CHUNK_SIZE; y++)
            {
                for (int z = 0; z < CHUNK_SIZE; z++)
                {
                    voxelData[x, y, z] = jobData[index++];
                }
            }
        }
    }
    
    public void GenerateVoxelData(CaveSettings settings)
    {
        // Fallback for non-job generation
        Vector3 chunkWorldPos = transform.position;
        
        for (int x = 0; x < CHUNK_SIZE; x++)
        {
            for (int y = 0; y < CHUNK_SIZE; y++)
            {
                for (int z = 0; z < CHUNK_SIZE; z++)
                {
                    Vector3 worldPos = chunkWorldPos + new Vector3(x, y, z);
                    voxelData[x, y, z] = GenerateDensity(worldPos, settings);
                }
            }
        }
    }
    
    float GenerateDensity(Vector3 pos, CaveSettings settings)
    {
        // Simple noise-based generation for fallback
        float noise = Mathf.PerlinNoise(pos.x * settings.baseFrequency, pos.z * settings.baseFrequency);
        noise += Mathf.PerlinNoise(pos.x * settings.baseFrequency * 2f, pos.z * settings.baseFrequency * 2f) * 0.5f;
        noise /= 1.5f;
        
        return noise > settings.caveThreshold ? 1f : 0f;
    }
    
    public void GenerateMesh()
    {
        vertices.Clear();
        triangles.Clear();
        normals.Clear();
        
        switch (meshingMode)
        {
            case MeshingMode.Simple:
                GenerateSimpleMesh();
                break;
            case MeshingMode.Greedy:
                GenerateGreedyMesh();
                break;
        }
        
        // Create mesh
        Mesh mesh = new Mesh();
        mesh.name = "Chunk Mesh";
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        
        if (normals.Count > 0)
        {
            mesh.normals = normals.ToArray();
        }
        else
        {
            mesh.RecalculateNormals();
        }
        
        mesh.RecalculateBounds();
        mesh.Optimize();
        
        meshFilter.mesh = mesh;
        
        // Only update collider if needed
        if (meshCollider.enabled)
        {
            meshCollider.sharedMesh = mesh;
        }
    }
    
    void GenerateSimpleMesh()
    {
        // Simple method that generates all faces correctly
        for (int x = 0; x < CHUNK_SIZE; x++)
        {
            for (int y = 0; y < CHUNK_SIZE; y++)
            {
                for (int z = 0; z < CHUNK_SIZE; z++)
                {
                    if (voxelData[x, y, z] > 0.5f)
                    {
                        // Check each face
                        if (!IsVoxelSolid(x - 1, y, z)) // Left face
                            AddFace(x, y, z, Vector3.left);
                        if (!IsVoxelSolid(x + 1, y, z)) // Right face
                            AddFace(x, y, z, Vector3.right);
                        if (!IsVoxelSolid(x, y - 1, z)) // Bottom face
                            AddFace(x, y, z, Vector3.down);
                        if (!IsVoxelSolid(x, y + 1, z)) // Top face
                            AddFace(x, y, z, Vector3.up);
                        if (!IsVoxelSolid(x, y, z - 1)) // Back face
                            AddFace(x, y, z, Vector3.back);
                        if (!IsVoxelSolid(x, y, z + 1)) // Front face
                            AddFace(x, y, z, Vector3.forward);
                    }
                }
            }
        }
    }
    
    void GenerateGreedyMesh()
    {
        // Process each axis separately (X, Y, Z)
        for (int axis = 0; axis < 3; axis++)
        {
            int axis1 = (axis + 1) % 3;
            int axis2 = (axis + 2) % 3;
            
            int[] dims = { CHUNK_SIZE, CHUNK_SIZE, CHUNK_SIZE };
            int[] pos = new int[3];
            
            bool[,] mask = new bool[dims[axis1], dims[axis2]];
            bool[,] maskFlip = new bool[dims[axis1], dims[axis2]];
            
            // Process each layer along this axis
            for (pos[axis] = -1; pos[axis] < dims[axis];)
            {
                // Create mask for this layer
                for (pos[axis1] = 0; pos[axis1] < dims[axis1]; pos[axis1]++)
                {
                    for (pos[axis2] = 0; pos[axis2] < dims[axis2]; pos[axis2]++)
                    {
                        // Get voxel states
                        bool voxel1 = IsVoxelSolid(pos[0], pos[1], pos[2]);
                        bool voxel2 = IsVoxelSolid(
                            pos[0] + (axis == 0 ? 1 : 0),
                            pos[1] + (axis == 1 ? 1 : 0),
                            pos[2] + (axis == 2 ? 1 : 0)
                        );
                        
                        // Create face if there's a solid/air boundary
                        mask[pos[axis1], pos[axis2]] = voxel1 != voxel2;
                        
                        // Track which direction the face should point
                        // Face points AWAY from solid, INTO air
                        maskFlip[pos[axis1], pos[axis2]] = voxel1 && !voxel2;
                    }
                }
                
                pos[axis]++;
                
                // Build mesh from mask
                for (int j = 0; j < dims[axis1]; j++)
                {
                    for (int i = 0; i < dims[axis2];)
                    {
                        if (!mask[j, i])
                        {
                            i++;
                            continue;
                        }
                        
                        bool flip = maskFlip[j, i];
                        
                        // Compute width
                        int width = 1;
                        while (i + width < dims[axis2] && mask[j, i + width] && maskFlip[j, i + width] == flip)
                        {
                            width++;
                        }
                        
                        // Compute height
                        int height = 1;
                        bool done = false;
                        
                        while (j + height < dims[axis1] && !done)
                        {
                            for (int k = 0; k < width; k++)
                            {
                                if (!mask[j + height, i + k] || maskFlip[j + height, i + k] != flip)
                                {
                                    done = true;
                                    break;
                                }
                            }
                            if (!done) height++;
                        }
                        
                        // Add quad
                        int[] x = new int[3];
                        x[axis] = pos[axis];
                        x[axis1] = j;
                        x[axis2] = i;
                        
                        int[] du = new int[3];
                        du[axis1] = height;
                        
                        int[] dv = new int[3];
                        dv[axis2] = width;
                        
                        CreateQuad(x, du, dv, axis, flip);
                        
                        // Clear mask area
                        for (int l = 0; l < height; l++)
                        {
                            for (int k = 0; k < width; k++)
                            {
                                mask[j + l, i + k] = false;
                            }
                        }
                        
                        i += width;
                    }
                }
            }
        }
    }
    
    void CreateQuad(int[] pos, int[] du, int[] dv, int axis, bool flip)
    {
        int vertexIndex = vertices.Count;
        
        // Create vertices
        Vector3 v1 = new Vector3(pos[0], pos[1], pos[2]);
        Vector3 v2 = v1 + new Vector3(du[0], du[1], du[2]);
        Vector3 v3 = v1 + new Vector3(dv[0], dv[1], dv[2]);
        Vector3 v4 = v2 + new Vector3(dv[0], dv[1], dv[2]);
        
        vertices.Add(v1);
        vertices.Add(v2);
        vertices.Add(v3);
        vertices.Add(v4);
        
        // Calculate normal - points INTO the air (away from solid)
        Vector3 normal = Vector3.zero;
        normal[axis] = flip ? 1 : -1;
        
        for (int i = 0; i < 4; i++)
        {
            normals.Add(normal);
        }
        
        // Add triangles with correct winding
        if (flip)
        {
            // Standard winding
            triangles.Add(vertexIndex);
            triangles.Add(vertexIndex + 1);
            triangles.Add(vertexIndex + 2);
            triangles.Add(vertexIndex + 1);
            triangles.Add(vertexIndex + 3);
            triangles.Add(vertexIndex + 2);
        }
        else
        {
            // Flipped winding
            triangles.Add(vertexIndex);
            triangles.Add(vertexIndex + 2);
            triangles.Add(vertexIndex + 1);
            triangles.Add(vertexIndex + 1);
            triangles.Add(vertexIndex + 2);
            triangles.Add(vertexIndex + 3);
        }
    }
    
    void AddFace(int x, int y, int z, Vector3 direction)
    {
        int vertexIndex = vertices.Count;
        Vector3 basePos = new Vector3(x, y, z);
        
        if (direction == Vector3.left)
        {
            vertices.Add(basePos + new Vector3(0, 0, 0));
            vertices.Add(basePos + new Vector3(0, 1, 0));
            vertices.Add(basePos + new Vector3(0, 1, 1));
            vertices.Add(basePos + new Vector3(0, 0, 1));
        }
        else if (direction == Vector3.right)
        {
            vertices.Add(basePos + new Vector3(1, 0, 1));
            vertices.Add(basePos + new Vector3(1, 1, 1));
            vertices.Add(basePos + new Vector3(1, 1, 0));
            vertices.Add(basePos + new Vector3(1, 0, 0));
        }
        else if (direction == Vector3.down)
        {
            vertices.Add(basePos + new Vector3(0, 0, 0));
            vertices.Add(basePos + new Vector3(0, 0, 1));
            vertices.Add(basePos + new Vector3(1, 0, 1));
            vertices.Add(basePos + new Vector3(1, 0, 0));
        }
        else if (direction == Vector3.up)
        {
            vertices.Add(basePos + new Vector3(0, 1, 1));
            vertices.Add(basePos + new Vector3(0, 1, 0));
            vertices.Add(basePos + new Vector3(1, 1, 0));
            vertices.Add(basePos + new Vector3(1, 1, 1));
        }
        else if (direction == Vector3.back)
        {
            vertices.Add(basePos + new Vector3(1, 0, 0));
            vertices.Add(basePos + new Vector3(1, 1, 0));
            vertices.Add(basePos + new Vector3(0, 1, 0));
            vertices.Add(basePos + new Vector3(0, 0, 0));
        }
        else if (direction == Vector3.forward)
        {
            vertices.Add(basePos + new Vector3(0, 0, 1));
            vertices.Add(basePos + new Vector3(0, 1, 1));
            vertices.Add(basePos + new Vector3(1, 1, 1));
            vertices.Add(basePos + new Vector3(1, 0, 1));
        }
        
        // Add triangles
        triangles.Add(vertexIndex);
        triangles.Add(vertexIndex + 1);
        triangles.Add(vertexIndex + 2);
        triangles.Add(vertexIndex);
        triangles.Add(vertexIndex + 2);
        triangles.Add(vertexIndex + 3);
        
        // Add normals
        for (int i = 0; i < 4; i++)
        {
            normals.Add(direction);
        }
    }
    
    bool IsVoxelSolid(int x, int y, int z)
    {
        if (x < 0 || x >= CHUNK_SIZE || y < 0 || y >= CHUNK_SIZE || z < 0 || z >= CHUNK_SIZE)
            return false;
            
        return voxelData[x, y, z] > 0.5f;
    }
    
    public float GetVoxel(int x, int y, int z)
    {
        if (x < 0 || x >= CHUNK_SIZE || y < 0 || y >= CHUNK_SIZE || z < 0 || z >= CHUNK_SIZE)
            return 0f;
            
        return voxelData[x, y, z];
    }
}