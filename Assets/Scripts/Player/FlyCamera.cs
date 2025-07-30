using UnityEngine;

public class FlyCamera : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 10f;
    public float fastSpeed = 30f;
    public float sensitivity = 2f;
    
    private float rotationX = 0f;
    private float rotationY = 0f;
    
    void Start()
    {
        // Lock cursor for better control
        Cursor.lockState = CursorLockMode.Locked;
        
        // Get initial rotation
        Vector3 rot = transform.eulerAngles;
        rotationX = rot.x;
        rotationY = rot.y;
    }
    
    void Update()
    {
        // Mouse look
        if (Cursor.lockState == CursorLockMode.Locked)
        {
            rotationX -= Input.GetAxis("Mouse Y") * sensitivity;
            rotationY += Input.GetAxis("Mouse X") * sensitivity;
            
            rotationX = Mathf.Clamp(rotationX, -90f, 90f);
            
            transform.rotation = Quaternion.Euler(rotationX, rotationY, 0);
        }
        
        // Toggle cursor lock with Escape
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = Cursor.lockState == CursorLockMode.Locked ? 
                CursorLockMode.None : CursorLockMode.Locked;
        }
        
        // Movement
        float speed = Input.GetKey(KeyCode.LeftShift) ? fastSpeed : moveSpeed;
        
        Vector3 move = Vector3.zero;
        move += transform.forward * Input.GetAxis("Vertical");
        move += transform.right * Input.GetAxis("Horizontal");
        
        // Up/down movement
        if (Input.GetKey(KeyCode.E)) move += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) move += Vector3.down;
        
        transform.position += move * speed * Time.deltaTime;
    }
}