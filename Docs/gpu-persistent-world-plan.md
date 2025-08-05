# GPU-Persistent World Implementation Plan

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                    CPU Side                              │
├─────────────────────────────────────────────────────────┤
│  WorldManager                                           │
│  ├── Player Position Tracking                          │
│  ├── Chunk Request Queue                               │
│  └── Update Command Buffer                             │
└────────────────┬───────────────────────────────────────┘
                 │ Commands Only
                 ▼
┌─────────────────────────────────────────────────────────┐
│                    GPU Side                              │
├─────────────────────────────────────────────────────────┤
│  Persistent Resources:                                   │
│  ├── World Data 3D Texture (RWTexture3D)               │
│  ├── Chunk Metadata Buffer                             │
│  ├── Vertex Pool Buffer                                │
│  ├── Index Pool Buffer                                 │
│  └── Indirect Draw Args Buffer                         │
│                                                         │
│  Compute Kernels:                                      │
│  ├── UpdateWorldRegion                                 │
│  ├── GenerateChunkData                                 │
│  ├── ExtractMeshFromRegion                            │
│  └── CompactVertexData                                │
└─────────────────────────────────────────────────────────┘
```

## Phase 1: Core Infrastructure (Week 1)

### 1.1 Persistent GPU Resources

```csharp
public class GPUWorldResources
{
    // World data stored as 3D texture
    // R: Density, G: Material ID, B: Temperature, A: Moisture
    public RenderTexture WorldDataTexture; // e.g., 512x256x512
    
    // Chunk metadata (position, LOD, flags)
    public ComputeBuffer ChunkMetadataBuffer;
    
    // Vertex/Index pools for all chunks
    public ComputeBuffer VertexPoolBuffer;    // 10M vertices
    public ComputeBuffer IndexPoolBuffer;     // 30M indices
    
    // Per-chunk draw arguments
    public ComputeBuffer IndirectArgsBuffer;
    
    // Allocation tracking
    public ComputeBuffer AllocationTable;
}
```

### 1.2 World Coordinate System

```hlsl
// Convert world position to texture coordinates
uint3 WorldToTextureCoord(float3 worldPos, float3 worldOrigin, float voxelSize)
{
    float3 relativePos = worldPos - worldOrigin;
    return uint3(relativePos / voxelSize);
}

// Wrap coordinates for infinite world
uint3 WrapTextureCoord(int3 coord, uint3 textureSize)
{
    return uint3(
        coord.x & (textureSize.x - 1),
        coord.y & (textureSize.y - 1),
        coord.z & (textureSize.z - 1)
    );
}
```

## Phase 2: GPU-Side World Management (Week 2)

### 2.1 Spatial Hashing

```hlsl
// ChunkMetadata.compute
struct ChunkMetadata
{
    float3 worldPosition;
    uint lodLevel;
    uint vertexOffset;
    uint vertexCount;
    uint indexOffset;
    uint indexCount;
    uint lastAccessFrame;
    uint flags; // dirty, generating, empty, etc.
};

// Hash function for chunk lookup
uint HashChunkPosition(int3 chunkCoord)
{
    const uint p1 = 73856093;
    const uint p2 = 19349663;
    const uint p3 = 83492791;
    return ((chunkCoord.x * p1) ^ (chunkCoord.y * p2) ^ (chunkCoord.z * p3)) % HASH_TABLE_SIZE;
}
```

### 2.2 Streaming Update System

```csharp
public class WorldStreamingSystem
{
    struct UpdateCommand
    {
        public enum Type { Generate, Modify, Delete }
        public Type type;
        public Vector3Int chunkCoord;
        public int lodLevel;
        public Vector3 modificationCenter;
        public float modificationRadius;
    }
    
    private ComputeBuffer commandBuffer;
    private Queue<UpdateCommand> pendingCommands;
    
    public void StreamUpdates()
    {
        // Batch commands for GPU
        var commands = pendingCommands.Take(MAX_COMMANDS_PER_FRAME);
        commandBuffer.SetData(commands);
        
        // Dispatch update kernel
        worldUpdateShader.SetBuffer(kernel, "Commands", commandBuffer);
        worldUpdateShader.Dispatch(kernel, commands.Length, 1, 1);
    }
}
```

## Phase 3: GPU Generation Pipeline (Week 3)

### 3.1 Async Generation Kernel

```hlsl
// WorldGeneration.compute
[numthreads(8, 8, 8)]
void GenerateWorldRegion(uint3 id : SV_DispatchThreadID)
{
    // Get chunk from command
    ChunkMetadata chunk = ChunkQueue[id.x];
    
    // Calculate texture region
    uint3 texBase = WorldToTextureCoord(chunk.worldPosition);
    uint3 texCoord = texBase + id.yzx * chunk.lodLevel;
    
    // Generate density
    float3 worldPos = TextureToWorldPos(texCoord);
    float density = GenerateCaveDensity(worldPos);
    
    // Write to persistent texture
    WorldDataTexture[WrapTextureCoord(texCoord)] = float4(density, materialID, temp, moisture);
    
    // Mark chunk as dirty for mesh extraction
    chunk.flags |= CHUNK_DIRTY;
}
```

### 3.2 Mesh Extraction Pipeline

```hlsl
// MeshExtraction.compute
RWStructuredBuffer<Vertex> VertexPool;
RWStructuredBuffer<uint> IndexPool;
RWStructuredBuffer<uint> VertexAllocator;

[numthreads(32, 1, 1)]
void ExtractMeshFromChunk(uint3 id : SV_DispatchThreadID)
{
    uint chunkIndex = id.x;
    ChunkMetadata chunk = ChunkMetadata[chunkIndex];
    
    // Allocate vertex space atomically
    uint vertexOffset = 0;
    InterlockedAdd(VertexAllocator[0], estimatedVertexCount, vertexOffset);
    
    // Extract mesh using marching cubes
    uint vertexCount = 0;
    for (uint z = 0; z < CHUNK_SIZE; z++)
    {
        for (uint y = 0; y < CHUNK_SIZE; y++)
        {
            for (uint x = 0; x < CHUNK_SIZE; x++)
            {
                // Read from persistent world texture
                float density[8];
                ReadCubeDensities(chunk.worldPosition, x, y, z, density);
                
                // Generate vertices
                GenerateMarchingCubesVertices(
                    density, 
                    VertexPool, 
                    vertexOffset + vertexCount,
                    vertexCount
                );
            }
        }
    }
    
    // Update metadata
    chunk.vertexOffset = vertexOffset;
    chunk.vertexCount = vertexCount;
}
```

## Phase 4: Rendering Integration (Week 4)

### 4.1 GPU-Driven Rendering

```csharp
public class GPUWorldRenderer
{
    private Material worldMaterial;
    private ComputeBuffer visibleChunksBuffer;
    private ComputeBuffer drawArgsBuffer;
    
    public void Render(Camera camera)
    {
        // GPU frustum culling
        cullShader.SetMatrix("ViewProjection", camera.projectionMatrix * camera.worldToCameraMatrix);
        cullShader.SetBuffer(0, "AllChunks", chunkMetadataBuffer);
        cullShader.SetBuffer(0, "VisibleChunks", visibleChunksBuffer);
        cullShader.Dispatch(0, (totalChunks + 63) / 64, 1, 1);
        
        // Draw all visible chunks in one call
        worldMaterial.SetBuffer("VertexPool", vertexPoolBuffer);
        worldMaterial.SetBuffer("ChunkMetadata", visibleChunksBuffer);
        
        Graphics.DrawProceduralIndirect(
            worldMaterial,
            new Bounds(Vector3.zero, Vector3.one * 10000),
            MeshTopology.Triangles,
            drawArgsBuffer
        );
    }
}
```

### 4.2 Vertex Shader for Pool Rendering

```hlsl
// WorldVertex.shader
StructuredBuffer<Vertex> VertexPool;
StructuredBuffer<ChunkMetadata> ChunkMetadata;

struct v2f
{
    float4 pos : SV_POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
};

v2f vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    ChunkMetadata chunk = ChunkMetadata[instanceID];
    Vertex v = VertexPool[chunk.vertexOffset + vertexID];
    
    v2f o;
    o.pos = UnityWorldToClipPos(v.position);
    o.normal = v.normal;
    o.uv = v.uv;
    
    return o;
}
```

## Phase 5: Memory Management (Week 5)

### 5.1 Ring Buffer Allocation

```hlsl
// MemoryManagement.compute
RWStructuredBuffer<uint> AllocationRing;
RWStructuredBuffer<uint> AllocationHead;

uint AllocateChunkMemory(uint requiredSize)
{
    uint offset;
    InterlockedAdd(AllocationHead[0], requiredSize, offset);
    
    // Wrap around ring buffer
    return offset % TOTAL_BUFFER_SIZE;
}

void FreeChunkMemory(uint offset, uint size)
{
    // Mark region as free in allocation table
    for (uint i = 0; i < size; i++)
    {
        AllocationTable[offset + i] = 0;
    }
}
```

### 5.2 Chunk Eviction

```csharp
public class ChunkEvictionSystem
{
    public void EvictDistantChunks(Vector3 playerPos)
    {
        // Sort chunks by distance and last access time
        var evictionKernel = evictionShader.FindKernel("SortChunksByPriority");
        evictionShader.SetVector("PlayerPosition", playerPos);
        evictionShader.SetInt("CurrentFrame", Time.frameCount);
        evictionShader.Dispatch(evictionKernel, 1, 1, 1);
        
        // Free memory for distant chunks
        var freeKernel = evictionShader.FindKernel("FreeDistantChunks");
        evictionShader.SetInt("MaxChunks", maxActiveChunks);
        evictionShader.Dispatch(freeKernel, 1, 1, 1);
    }
}
```

## Implementation Timeline

### Week 1: Foundation
- Set up persistent GPU resources
- Implement basic world texture system
- Create command buffer infrastructure

### Week 2: World Management
- Implement spatial hashing on GPU
- Create streaming update system
- Add chunk metadata management

### Week 3: Generation
- Port cave generation to work with persistent texture
- Implement GPU-side mesh extraction
- Add LOD support

### Week 4: Rendering
- Implement GPU-driven rendering
- Create indirect drawing system
- Add frustum culling

### Week 5: Optimization
- Implement memory management
- Add chunk eviction
- Performance profiling and tuning

## Key Benefits

1. **Zero CPU-GPU Transfer**: Once initialized, no mesh data moves between CPU and GPU
2. **Instant Chunk Loading**: Chunks already on GPU appear instantly
3. **Massive Scale**: Can handle 10x-100x more chunks than traditional systems
4. **Dynamic Updates**: Terrain modifications happen entirely on GPU
5. **Perfect for VR**: Consistent frame times with no loading hitches

## Challenges to Consider

1. **GPU Memory**: Requires significant VRAM (2-4GB for large worlds)
2. **Debugging**: Harder to debug GPU-side issues
3. **Platform Support**: Requires modern GPU features
4. **Initial Complexity**: More complex than traditional chunk systems

## Performance Targets

- 1000+ active chunks at 90 FPS
- < 0.1ms chunk activation time
- Support for real-time terrain modification
- No frame drops during chunk loading