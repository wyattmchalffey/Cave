// TerrainTestController.cs - Simple controller to test the terrain system
using UnityEngine;
using GPUTerrain;

public class TerrainTestController : MonoBehaviour
{
    [Header("References")]
    public TerrainWorldManager terrainManager;
    
    [Header("Player Movement")]
    public float moveSpeed = 10f;
    public float lookSpeed = 2f;
    public float flySpeed = 20f;
    
    private Camera playerCamera;
    private float rotationX = 0f;
    
    void Start()
    {
        playerCamera = GetComponent<Camera>();
        if (playerCamera == null)
            playerCamera = GetComponentInChildren<Camera>();
        
        // Lock cursor for FPS controls
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        
        // Create terrain manager if not assigned
        if (terrainManager == null)
        {
            GameObject terrainGO = new GameObject("Terrain World Manager");
            terrainManager = terrainGO.AddComponent<TerrainWorldManager>();
            Debug.Log("Created Terrain World Manager");
        }
    }
    
    void Update()
    {
        HandleMovement();
        HandleMouseLook();
        HandleDebugInputs();
    }
    
    void HandleMovement()
    {
        float horizontal = Input.GetAxis("Horizontal");
        float vertical = Input.GetAxis("Vertical");
        float upDown = 0f;
        
        if (Input.GetKey(KeyCode.Space))
            upDown = 1f;
        else if (Input.GetKey(KeyCode.LeftShift))
            upDown = -1f;
        
        float currentSpeed = Input.GetKey(KeyCode.LeftControl) ? flySpeed : moveSpeed;
        
        Vector3 movement = new Vector3(horizontal, upDown, vertical);
        movement = transform.TransformDirection(movement);
        movement *= currentSpeed * Time.deltaTime;
        
        transform.position += movement;
    }
    
    void HandleMouseLook()
    {
        float mouseX = Input.GetAxis("Mouse X") * lookSpeed;
        float mouseY = Input.GetAxis("Mouse Y") * lookSpeed;
        
        rotationX -= mouseY;
        rotationX = Mathf.Clamp(rotationX, -90f, 90f);
        
        playerCamera.transform.localRotation = Quaternion.Euler(rotationX, 0f, 0f);
        transform.Rotate(Vector3.up * mouseX);
    }
    
    void HandleDebugInputs()
    {
        // Toggle cursor lock
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = Cursor.lockState == CursorLockMode.Locked 
                ? CursorLockMode.None 
                : CursorLockMode.Locked;
            Cursor.visible = !Cursor.visible;
        }
        
        // Reload shaders (for development)
        if (Input.GetKeyDown(KeyCode.R) && Input.GetKey(KeyCode.LeftControl))
        {
            Debug.Log("Reloading shaders...");
            // Force shader reload by disabling/enabling terrain manager
            if (terrainManager != null)
            {
                terrainManager.enabled = false;
                terrainManager.enabled = true;
            }
        }
    }
    
    void OnGUI()
    {
        // Debug information
        int yOffset = 10;
        GUI.Label(new Rect(10, yOffset, 300, 20), $"Position: {transform.position}");
        yOffset += 20;
        
        GUI.Label(new Rect(10, yOffset, 300, 20), "Controls:");
        yOffset += 20;
        GUI.Label(new Rect(10, yOffset, 300, 20), "WASD - Move");
        yOffset += 20;
        GUI.Label(new Rect(10, yOffset, 300, 20), "Space/Shift - Fly Up/Down");
        yOffset += 20;
        GUI.Label(new Rect(10, yOffset, 300, 20), "Ctrl - Fast Movement");
        yOffset += 20;
        GUI.Label(new Rect(10, yOffset, 300, 20), "ESC - Toggle Cursor");
    }
}

// Setup instructions for the project
public static class TerrainSetupInstructions
{
    /*
     * GPU TERRAIN SETUP INSTRUCTIONS
     * ==============================
     * 
     * 1. Create Compute Shaders:
     *    - Create a folder: Assets/Shaders/Compute/
     *    - Create new Compute Shader: TerrainGeneration.compute
     *    - Create new Compute Shader: MeshExtraction.compute
     *    - Create new Compute Shader: WorldUpdate.compute
     *    - Create new Compute Shader: FrustumCulling.compute
     *    - Copy the provided compute shader code into each file
     * 
     * 2. Create Materials:
     *    - Create a folder: Assets/Materials/
     *    - Create new Material: TerrainMaterial
     *    - Set shader to: GPUTerrain/TerrainRenderer
     *    - Assign texture arrays for different material types
     * 
     * 3. Create Test Scene:
     *    - Create new Scene
     *    - Create Empty GameObject: "Player"
     *    - Add Camera as child of Player
     *    - Add TerrainTestController script to Player
     *    - Create Empty GameObject: "Terrain"
     *    - Add TerrainWorldManager script to Terrain
     * 
     * 4. Configure Terrain World Manager:
     *    - Assign compute shaders to their slots
     *    - Assign terrain material
     *    - Configure world size (start with 16x8x16 chunks)
     *    - Set voxel size to 0.25
     * 
     * 5. Marching Cubes Tables:
     *    - Create MarchingCubesTables.cs with the standard tables
     *    - These can be found in any marching cubes implementation
     * 
     * 6. Performance Settings:
     *    - Enable GPU Instancing on terrain material
     *    - Set project to use Forward rendering (for now)
     *    - Enable Multi-threaded rendering in Player Settings
     * 
     * 7. Testing:
     *    - Enter Play mode
     *    - Terrain should generate around the player
     *    - Use WASD to move and explore caves
     * 
     * TROUBLESHOOTING:
     * ================
     * 
     * If terrain doesn't appear:
     * - Check compute shader compilation errors
     * - Verify all shaders are assigned
     * - Check console for errors
     * - Try reducing world size
     * 
     * If performance is poor:
     * - Reduce chunk view distance
     * - Increase voxel size (0.5 instead of 0.25)
     * - Reduce world height
     * - Profile with GPU Profiler
     * 
     * Next Steps:
     * - Add texture arrays for different materials
     * - Implement LOD system
     * - Add terrain modification
     * - Integrate ecosystem data layers
     */
}