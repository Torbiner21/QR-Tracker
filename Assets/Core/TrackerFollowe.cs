using UnityEngine;
using UnityEngine.InputSystem;

public class TrackerFollowe : MonoBehaviour
{
    public InputActionReference trackerPositionAction;

    void OnEnable() 
    { 
        trackerPositionAction.action.Enable(); 
    }

    void OnDisable() 
    { 
        trackerPositionAction.action.Disable(); 
    }

    void LateUpdate()
    {
        Vector3 rawPos = trackerPositionAction.action.ReadValue<Vector3>();
        this.transform.position = rawPos;
    }
}
