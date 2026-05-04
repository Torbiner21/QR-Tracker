using UnityEngine;
using Valve.VR;

public class HybridTracker : MonoBehaviour
{
    [Header("Calibration Settings")]
    [Tooltip("Leave empty. It will automatically find the Meta Quest Headset (Main Camera)")]
    public Transform questHeadset;
    [Tooltip("Press this key while physically holding the tracker against the headset")]
    public KeyCode calibrateKey = KeyCode.C;

    [Header("Hardware Selection")]
    [Tooltip("Leave empty to use the first tracker found, or enter a serial (e.g., LHR-1234)")]
    public string targetSerial = "";

    // The mathematically isolated offsets between Meta Space and SteamVR Space
    private Vector3 spaceOffsetPos = Vector3.zero;
    private Quaternion spaceOffsetRot = Quaternion.identity;

    private bool isTracking = false;

    void Start()
    {
        // Auto-assign the headset if not set manually
        if (questHeadset == null && Camera.main != null)
        {
            questHeadset = Camera.main.transform;
        }

        // Boot OpenVR purely in the background to avoid fighting Oculus XR
        var err = EVRInitError.None;
        OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);

        if (err == EVRInitError.None)
        {
            isTracking = true;
            Debug.Log("[Hybrid Tracker] OpenVR Background Link Established.");
        }
        else
        {
            Debug.LogError($"[Hybrid Tracker] Failed to init SteamVR: {err}");
        }
    }

    void LateUpdate()
    {
        if (!isTracking) return;

        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, poses);

        for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
        {
            if (!poses[i].bPoseIsValid || !poses[i].bDeviceIsConnected) continue;
            if (OpenVR.System.GetTrackedDeviceClass(i) != ETrackedDeviceClass.GenericTracker) continue;

            if (!string.IsNullOrEmpty(targetSerial))
            {
                var sb = new System.Text.StringBuilder(64);
                var err = ETrackedPropertyError.TrackedProp_Success;
                OpenVR.System.GetStringTrackedDeviceProperty(i, ETrackedDeviceProperty.Prop_SerialNumber_String, sb, 64, ref err);
                if (!sb.ToString().Contains(targetSerial)) continue;
            }

            // Extract RAW SteamVR Pose to Unity Left-Handed Coordinates
            HmdMatrix34_t m = poses[i].mDeviceToAbsoluteTracking;
            Vector3 rawPos = new Vector3(m.m3, m.m7, -m.m11);
            Vector3 forward = new Vector3(-m.m2, -m.m6, m.m10);
            Vector3 up = new Vector3(m.m1, m.m5, -m.m9);

            if (forward == Vector3.zero || up == Vector3.zero) continue;
            Quaternion rawRot = Quaternion.LookRotation(forward, up);

            // Calibrate when 'C' is pressed
            if (Input.GetKeyDown(calibrateKey) && questHeadset != null)
            {
                CalibrateSpace(rawPos, rawRot);
            }

            // Apply Space Offset
            transform.position = spaceOffsetPos + (spaceOffsetRot * rawPos);
            transform.rotation = spaceOffsetRot * rawRot;

            break;
        }
    }

    private void CalibrateSpace(Vector3 rawTrackerPos, Quaternion rawTrackerRot)
    {
        // Calculate the rotation difference between the Headset and the Vive Tracker
        Quaternion rotationDiff = questHeadset.rotation * Quaternion.Inverse(rawTrackerRot);

        // Lock to YAW (Y-axis) only to prevent distance drift
        spaceOffsetRot = Quaternion.Euler(0, rotationDiff.eulerAngles.y, 0);

        // Calculate the position difference
        spaceOffsetPos = questHeadset.position - (spaceOffsetRot * rawTrackerPos);

        Debug.Log($"[Hybrid Tracker] Calibrated to Headset! Yaw Offset: {spaceOffsetRot.eulerAngles.y:F1}°, Pos Offset: {spaceOffsetPos}");
    }

    void OnDestroy()
    {
        if (isTracking) OpenVR.Shutdown();
    }
}
