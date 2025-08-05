// ChunkMetadata.cs - Shared structures between CPU and GPU
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace GPUTerrain
{
    // Must match the struct in shaders exactly
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct ChunkMetadata
    {
        [FieldOffset(0)] public float3 position;      // World position of chunk origin (12 bytes)
        [FieldOffset(12)] public uint vertexOffset;   // Offset into global vertex pool (4 bytes)
        [FieldOffset(16)] public uint vertexCount;    // Number of vertices in this chunk (4 bytes)
        [FieldOffset(20)] public uint indexOffset;    // Offset into global index pool (4 bytes)
        [FieldOffset(24)] public uint lodLevel;       // Level of detail (0 = highest) (4 bytes)
        [FieldOffset(28)] public uint flags;          // Bit flags (visible, dirty, etc.) (4 bytes)
        // Total used: 32 bytes, struct size forced to 64 bytes
        
        public static int Size => 64;
        
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
        
        public static int ChunkIndex(int3 coord, int worldSizeX, int worldSizeY, int worldSizeZ)
        {
            if (coord.x < 0 || coord.x >= worldSizeX ||
                coord.y < 0 || coord.y >= worldSizeY ||
                coord.z < 0 || coord.z >= worldSizeZ)
            {
                return -1; // Out of bounds
            }
            
            return coord.x + coord.y * worldSizeX + coord.z * worldSizeX * worldSizeY;
        }
        
        public static int3 IndexToChunk(int index, int worldSizeX, int worldSizeY, int worldSizeZ)
        {
            int z = index / (worldSizeX * worldSizeY);
            int y = (index % (worldSizeX * worldSizeY)) / worldSizeX;
            int x = index % worldSizeX;
            
            return new int3(x, y, z);
        }
        
        public static float DistanceToChunk(float3 worldPos, int3 chunkCoord, float voxelSize, int chunkSize)
        {
            float3 chunkCenter = ChunkToWorld(chunkCoord, voxelSize, chunkSize) + (voxelSize * chunkSize * 0.5f);
            return math.distance(worldPos, chunkCenter);
        }
        
        public static bool IsChunkInRange(float3 worldPos, int3 chunkCoord, float range, float voxelSize, int chunkSize)
        {
            return DistanceToChunk(worldPos, chunkCoord, voxelSize, chunkSize) <= range;
        }
    }
    
    // Debug utilities
    public static class ChunkDebugInfo
    {
        public static string GetChunkInfo(ChunkMetadata chunk)
        {
            return $"Chunk at {chunk.position}: " +
                   $"Vertices={chunk.vertexCount} (offset={chunk.vertexOffset}), " +
                   $"LOD={chunk.lodLevel}, " +
                   $"Flags={GetFlagsString(chunk.flags)}";
        }
        
        public static string GetFlagsString(uint flags)
        {
            string result = "";
            if ((flags & ChunkMetadata.FLAG_VISIBLE) != 0) result += "Visible ";
            if ((flags & ChunkMetadata.FLAG_DIRTY) != 0) result += "Dirty ";
            if ((flags & ChunkMetadata.FLAG_GENERATING) != 0) result += "Generating ";
            if ((flags & ChunkMetadata.FLAG_HAS_MESH) != 0) result += "HasMesh ";
            if ((flags & ChunkMetadata.FLAG_EMPTY) != 0) result += "Empty ";
            return result.Trim();
        }
    }
}