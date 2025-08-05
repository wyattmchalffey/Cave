// DensityFieldDebugger.cs - Debug visualization for density field
using UnityEngine;
using Unity.Mathematics;

namespace GPUTerrain
{
    public class DensityFieldDebugger : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ComputeShader debugShader;
        private TerrainWorldManager worldManager;
        
        [Header("Debug Settings")]
        [SerializeField] private bool showDensityValues = true;
        [SerializeField] private int3 debugChunkCoord = new int3(0, 0, 0);
        [SerializeField] private int debugSliceY = 16; // Which Y slice to show
        
        private ComputeBuffer densityReadBuffer;
        private float[] densityData;
        
        void Start()
        {
            worldManager = GetComponent<TerrainWorldManager>();
            if (worldManager == null)
            {
                enabled = false;
                return;
            }
            
            // Create buffer for reading density values
            int size = TerrainWorldManager.CHUNK_SIZE_PLUS_ONE;
            densityReadBuffer = new ComputeBuffer(size * size * size, sizeof(float));
            densityData = new float[size * size * size];
        }
        
        void Update()
        {
            if (Input.GetKeyDown(KeyCode.D) && Input.GetKey(KeyCode.LeftControl))
            {
                DebugDensityField();
            }
        }
        
        void DebugDensityField()
        {
            var worldTexture = worldManager.GetWorldDataTexture();
            if (worldTexture == null)
            {
                Debug.LogError("World data texture is null!");
                return;
            }
            
            // Read a slice of the density field
            if (debugShader != null)
            {
                int kernel = debugShader.FindKernel("ReadDensitySlice");
                debugShader.SetTexture(kernel, "WorldData", worldTexture);
                debugShader.SetBuffer(kernel, "DensityOutput", densityReadBuffer);
                debugShader.SetVector("ChunkCoord", new Vector4(debugChunkCoord.x, debugChunkCoord.y, debugChunkCoord.z, 0));
                debugShader.SetInt("ChunkSize", TerrainWorldManager.CHUNK_SIZE);
                debugShader.SetInt("SliceY", debugSliceY);
                
                debugShader.Dispatch(kernel, TerrainWorldManager.CHUNK_SIZE_PLUS_ONE / 8, 1, TerrainWorldManager.CHUNK_SIZE_PLUS_ONE / 8);
                
                densityReadBuffer.GetData(densityData);
                
                // Log some sample values
                Debug.Log($"=== Density Field Debug for Chunk {debugChunkCoord}, Y slice {debugSliceY} ===");
                
                // Sample center and corners
                int centerX = TerrainWorldManager.CHUNK_SIZE / 2;
                int centerZ = TerrainWorldManager.CHUNK_SIZE / 2;
                
                float centerDensity = GetDensityAt(centerX, debugSliceY, centerZ);
                Debug.Log($"Center ({centerX},{debugSliceY},{centerZ}): {centerDensity}");
                
                // Check for any transitions
                int transitionCount = 0;
                for (int z = 0; z < TerrainWorldManager.CHUNK_SIZE; z++)
                {
                    for (int x = 0; x < TerrainWorldManager.CHUNK_SIZE; x++)
                    {
                        float d = GetDensityAt(x, debugSliceY, z);
                        if (d > -0.1f && d < 0.1f) // Near the surface
                        {
                            transitionCount++;
                        }
                    }
                }
                
                Debug.Log($"Surface transitions found: {transitionCount}");
                
                // Find min/max values
                float minDensity = float.MaxValue;
                float maxDensity = float.MinValue;
                for (int i = 0; i < densityData.Length; i++)
                {
                    minDensity = Mathf.Min(minDensity, densityData[i]);
                    maxDensity = Mathf.Max(maxDensity, densityData[i]);
                }
                
                Debug.Log($"Density range: [{minDensity}, {maxDensity}]");
            }
            else
            {
                // Manual debug without compute shader
                Debug.Log("No debug shader assigned, showing basic info");
                Debug.Log($"World texture size: {worldTexture.width}x{worldTexture.height}x{worldTexture.volumeDepth}");
                Debug.Log($"Chunk at {debugChunkCoord} should be at texture offset: {debugChunkCoord * TerrainWorldManager.CHUNK_SIZE}");
            }
        }
        
        float GetDensityAt(int x, int y, int z)
        {
            int index = x + y * TerrainWorldManager.CHUNK_SIZE_PLUS_ONE + z * TerrainWorldManager.CHUNK_SIZE_PLUS_ONE * TerrainWorldManager.CHUNK_SIZE_PLUS_ONE;
            return index < densityData.Length ? densityData[index] : 0f;
        }
        
        void OnGUI()
        {
            if (!showDensityValues) return;
            
            GUI.Label(new Rect(10, 300, 400, 20), "=== Density Field Debug ===");
            GUI.Label(new Rect(10, 320, 400, 20), "Ctrl+D - Debug current chunk");
            GUI.Label(new Rect(10, 340, 400, 20), $"Debug Chunk: {debugChunkCoord}");
            GUI.Label(new Rect(10, 360, 400, 20), $"Y Slice: {debugSliceY}");
        }
        
        void OnDestroy()
        {
            densityReadBuffer?.Release();
        }
    }
}