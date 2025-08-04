// ChunkMetadata.cs - Shared structures between CPU and GPU
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace GPUTerrain
{
    // Must match the struct in shaders exactly
    [StructLayout(LayoutKind.Sequential)]
    public struct ChunkMetadata
    {
        public float3 position;      // World position of chunk origin
        public uint vertexOffset;    // Offset into global vertex pool
        public uint vertexCount;     // Number of vertices in this chunk
        public uint indexOffset;     // Offset into global index pool
        public uint lodLevel;        // Level of detail (0 = highest)
        public uint flags;           // Bit flags (visible, dirty, etc.)
        public float3 _padding;      // Padding to 64 bytes for alignment
        
        public static int Size => sizeof(float) * 16; // 64 bytes
        
        // Flag constants
        public const uint FLAG_VISIBLE = 1 << 0;
        public const uint FLAG_DIRTY = 1 << 1;
        public const uint FLAG_GENERATING = 1 << 2;
        public const uint FLAG_HAS_MESH = 1 << 3;
        public const uint FLAG_EMPTY = 1 << 4;
        
        public bool IsVisible => (flags & FLAG_VISIBLE) != 0;
        public bool IsDirty => (flags & FLAG_DIRTY) != 0;
        public bool IsGenerating => (flags & FLAG_GENERATING) != 0;
        public bool HasMesh => (flags & FLAG_HAS_MESH) != 0;
        public bool IsEmpty => (flags & FLAG_EMPTY) != 0;
    }
    
    // Vertex structure for the pool
    [StructLayout(LayoutKind.Sequential)]
    public struct TerrainVertex
    {
        public float3 position;      // World position
        public float materialID;     // Material/texture ID
        public float3 normal;        // Surface normal
        public float _padding;       // Padding to 32 bytes
        
        public static int Size => sizeof(float) * 8; // 32 bytes
    }
    
    // World update command for GPU
    [StructLayout(LayoutKind.Sequential)]
    public struct WorldUpdateCommand
    {
        public uint type;            // 0=Generate, 1=Modify, 2=Delete
        public float3 position;      // Position or center of modification
        public float radius;         // Radius for modifications
        public float strength;       // Modification strength
        public uint2 _padding;       // Padding to 32 bytes
        
        public static int Size => sizeof(float) * 8; // 32 bytes
    }
    
    // Utility class for managing chunk coordinates
    public static class ChunkCoordinate
    {
        public static int3 WorldToChunk(float3 worldPos, float voxelSize, int chunkSize)
        {
            float chunkWorldSize = voxelSize * chunkSize;
            return new int3(
                (int)math.floor(worldPos.x / chunkWorldSize),
                (int)math.floor(worldPos.y / chunkWorldSize),
                (int)math.floor(worldPos.z / chunkWorldSize)
            );
        }
        
        public static float3 ChunkToWorld(int3 chunkCoord, float voxelSize, int chunkSize)
        {
            float chunkWorldSize = voxelSize * chunkSize;
            return new float3(chunkCoord) * chunkWorldSize;
        }
        
        public static uint ChunkHash(int3 coord)
        {
            uint x = (uint)coord.x;
            uint y = (uint)coord.y;
            uint z = (uint)coord.z;
            
            const uint p1 = 73856093;
            const uint p2 = 19349663;
            const uint p3 = 83492791;
            
            return (x * p1) ^ (y * p2) ^ (z * p3);
        }
    }
}