using UnityEngine;

public class ObjectFollowCamera : MonoBehaviour
{
    [Header("Follow Settings")]
    public float followDistance = 2.0f;     // Distance behind the camera
    public float heightOffset = 0.5f;       // Vertical offset 
    public float lateralOffset = 0.0f;      // Side offset (left/right)
    public float followSpeed = 5.0f;        // How quickly it follows
    public float rotationSpeed = 10.0f;     // How quickly it aligns rotation
    public bool matchCameraRotation = true; // Whether to copy camera rotation
    public bool lookAtCamera = false;       // Option to face the camera instead
    Transform cam;

    void Start()
    {
        cam = Camera.main?.transform;
    }

    void LateUpdate()
    {
        if (cam == null)
            return;

        // Desired position: behind and slightly offset relative to camera
        Vector3 targetPosition = cam.position 
                               - cam.forward * followDistance 
                               + cam.up * heightOffset 
                               + cam.right * lateralOffset;

        // Smooth position
        transform.position = Vector3.Lerp(transform.position, targetPosition, followSpeed * Time.deltaTime);

        // Smooth rotation
        if (matchCameraRotation)
        {
            Quaternion targetRotation = Quaternion.Lerp(transform.rotation, cam.rotation, rotationSpeed * Time.deltaTime);
            transform.rotation = targetRotation;
        }
        else if (lookAtCamera)
        {
            Quaternion lookRot = Quaternion.LookRotation(cam.position - transform.position);
            transform.rotation = Quaternion.Lerp(transform.rotation, lookRot, rotationSpeed * Time.deltaTime);
        }
    }
}
