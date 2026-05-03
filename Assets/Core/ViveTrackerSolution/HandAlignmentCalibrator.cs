using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

/// <summary>
/// Two-point hand calibration for the Ambu tracker system.
///
/// PROBLEM this solves:
///   Single-point T-key sync can nail position but leaves orientation slightly off.
///   A small yaw error compounds badly during rotation because rotation errors grow
///   with distance from the pivot.
///
/// HOW IT WORKS:
///   You place two small physical markers at known locations on the real Ambu
///   (e.g. a front edge and a side edge — anything geometrically unambiguous).
///   You create matching child Transforms on the virtual model at the same local offsets.
///   When calibrating you touch those two real points with your hands and press the key.
///   The script solves for the full rigid-body transform (yaw + 3-D translation) and
///   snaps parentToAlign so the virtual model matches the real one exactly.
///
/// TWO MODES:
///   Simultaneous  — hold both hands at their points, press Calibrate key once.
///   Sequential    — press CapturePointA key while touching point A, then press
///                   CapturePointB key while touching point B, then a final
///                   Calibrate key (or auto-fires after both points captured).
///
/// INTEGRATION:
///   - parentToAlign  → same as parentA in AlignChildToViveTarget (the Ambu_Container)
///   - virtualRefA/B  → child Transforms on the virtual model at the two reference marks
///   - handSide       → which hand touches which reference (configurable per setup)
/// </summary>
public class HandAlignmentCalibrator : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────────────

    [Header("Target")]
    [Tooltip("The root object to move — same as parentA in AlignChildToViveTarget.")]
    public Transform parentToAlign;

    [Header("Virtual Reference Points (children of the virtual model)")]
    [Tooltip("Child Transform on the virtual model at known reference mark A.")]
    public Transform virtualRefA;
    [Tooltip("Child Transform on the virtual model at known reference mark B.")]
    public Transform virtualRefB;
    [Header("Mode")]
    [Tooltip("Simultaneous: both hands sampled at the same moment (one key press).\n" +
             "Sequential: capture each point separately, then auto-applies.")]
    public CalibrationMode mode = CalibrationMode.Simultaneous;

    [Tooltip("Which controller/hand maps to virtual ref A (left hand recommended for point A).")]
    public HandSide refAHand = HandSide.Left;

    [Header("Keys — Simultaneous mode")]
    [Tooltip("Press while holding both hands at the two reference marks.")]
    public KeyCode calibrateKey = KeyCode.C;

    [Header("Keys — Sequential mode")]
    [Tooltip("Press while touching reference mark A with the designated hand.")]
    public KeyCode captureAKey    = KeyCode.Alpha1;
    [Tooltip("Press while touching reference mark B with the designated hand.")]
    public KeyCode captureBKey    = KeyCode.Alpha2;
    [Tooltip("Press to apply after both points captured (leave as None for auto-apply).")]
    public KeyCode applyAfterBKey = KeyCode.None;

    [Header("Options")]
    [Tooltip("Only correct yaw (rotation around world Y). Recommended — avoids tilting a floor-level object.")]
    public bool yawOnlyRotation = true;

    [Tooltip("Log sampling debug info to console.")]
    public bool debugLogging = true;

    // ── State ──────────────────────────────────────────────────────────────────

    // ── Auto-discovered at runtime (no Inspector assignment needed) ───────────
    private Transform _trackingSpace;   // OVRCameraRig.trackingSpace — converts tracking→world

    [Header("Debug Read-Only")]
    [SerializeField] private bool _pointACaptured;
    [SerializeField] private bool _pointBCaptured;
    [SerializeField] private Vector3 _capturedPhysA;
    [SerializeField] private Vector3 _capturedPhysB;

    public enum CalibrationMode { Simultaneous, Sequential }
    public enum HandSide { Left, Right }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void Start()
    {
        DiscoverHandAnchors();
    }

    /// <summary>
    /// Finds the OVRCameraRig in the scene and caches its trackingSpace Transform.
    /// Called on Start and before each calibration trigger so dynamic rigs are handled.
    /// </summary>
    private void DiscoverHandAnchors()
    {
        if (_trackingSpace != null) return;

        var rig = FindObjectOfType<OVRCameraRig>();
        if (rig != null)
        {
            _trackingSpace = rig.trackingSpace;
            if (debugLogging)
                Debug.Log($"[HandCalibrator] Auto-discovered OVRCameraRig — trackingSpace='{_trackingSpace.name}'", this);
        }
        else if (debugLogging)
        {
            Debug.LogWarning("[HandCalibrator] OVRCameraRig not found yet — will retry on next calibration trigger.", this);
        }
    }

    private void Update()
    {
        switch (mode)
        {
            case CalibrationMode.Simultaneous:
                if (Input.GetKeyDown(calibrateKey))
                    CalibrateSimultaneous();
                break;

            case CalibrationMode.Sequential:
                if (Input.GetKeyDown(captureAKey))
                    CapturePointA();
                if (Input.GetKeyDown(captureBKey))
                    CapturePointB();
                if (applyAfterBKey != KeyCode.None && Input.GetKeyDown(applyAfterBKey))
                    ApplyCapturedPoints();
                break;
        }
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Simultaneous mode: sample both hands right now and apply alignment.</summary>
    [ContextMenu("Calibrate (Simultaneous — both hands)")]
    public void CalibrateSimultaneous()
    {
        DiscoverHandAnchors();
        if (!TryGetHandWorldPos(refAHand,           out Vector3 physA)) return;
        if (!TryGetHandWorldPos(Opposite(refAHand), out Vector3 physB)) return;

        ApplyTwoPointAlignment(physA, physB);
    }

    /// <summary>Sequential mode: capture position of reference mark A now.</summary>
    [ContextMenu("Sequential — Capture Point A")]
    public void CapturePointA()
    {
        DiscoverHandAnchors();
        if (!TryGetHandWorldPos(refAHand, out _capturedPhysA)) return;
        _pointACaptured = true;

        if (debugLogging)
            Debug.Log($"[HandCalibrator] Point A captured: {_capturedPhysA}", this);

        if (_pointACaptured && _pointBCaptured && applyAfterBKey == KeyCode.None)
            ApplyCapturedPoints();
    }

    /// <summary>Sequential mode: capture position of reference mark B now.</summary>
    [ContextMenu("Sequential — Capture Point B")]
    public void CapturePointB()
    {
        DiscoverHandAnchors();
        if (!TryGetHandWorldPos(Opposite(refAHand), out _capturedPhysB)) return;
        _pointBCaptured = true;

        if (debugLogging)
            Debug.Log($"[HandCalibrator] Point B captured: {_capturedPhysB}", this);

        if (_pointACaptured && _pointBCaptured && applyAfterBKey == KeyCode.None)
            ApplyCapturedPoints();
    }

    /// <summary>Sequential mode: apply alignment from the two previously captured points.</summary>
    [ContextMenu("Sequential — Apply Captured Points")]
    public void ApplyCapturedPoints()
    {
        if (!_pointACaptured || !_pointBCaptured)
        {
            Debug.LogWarning("[HandCalibrator] Cannot apply — not both points captured yet.", this);
            return;
        }
        ApplyTwoPointAlignment(_capturedPhysA, _capturedPhysB);
        _pointACaptured = _pointBCaptured = false;
    }

    /// <summary>Reset sequential captures without applying.</summary>
    [ContextMenu("Reset Captures")]
    public void ResetCaptures()
    {
        _pointACaptured = _pointBCaptured = false;
        Debug.Log("[HandCalibrator] Captures reset.", this);
    }

    // ── Core math ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Solves for and applies the rigid-body transform that moves parentToAlign so
    /// virtualRefA lands on physA and virtualRefB lands on physB.
    ///
    /// Math (yaw-only variant):
    ///   localA = parentToAlign.InverseTransformPoint(virtualRefA.position)  — local offset, constant
    ///   localB = parentToAlign.InverseTransformPoint(virtualRefB.position)
    ///
    ///   We want:  R * localA + t = physA
    ///             R * localB + t = physB
    ///
    ///   Subtracting:   R * (localB − localA) = physB − physA
    ///   Project to XZ: yaw-only FromToRotation gives R.
    ///   Translation:   t = physMid − R * localMid
    ///
    ///   When yawOnlyRotation is false, a full 3-D rotation is solved from the
    ///   same two vectors (loses one degree of freedom — roll around the AB axis).
    /// </summary>
    private void ApplyTwoPointAlignment(Vector3 physA, Vector3 physB)
    {
        if (parentToAlign == null || virtualRefA == null || virtualRefB == null)
        {
            Debug.LogWarning("[HandCalibrator] Assign parentToAlign, virtualRefA, and virtualRefB.", this);
            return;
        }

        // Sanity check: if both hands returned the same position the XR fallback is broken.
        // This happens on Meta/OVR when leftHandAnchor/rightHandAnchor are not assigned.
        if (Vector3.Distance(physA, physB) < 0.01f)
        {
            Debug.LogError("[HandCalibrator] physA and physB are identical (or < 1 cm apart).\n" +
                           "This means hand positions were NOT read correctly.\n" +
                           "► OVRCameraRig may not be active yet, or hand tracking is not running.", this);
            return;
        }

        // Local offsets of the two reference marks relative to parentToAlign.
        // Captured BEFORE we move the parent — order matters.
        Vector3 localA = parentToAlign.InverseTransformPoint(virtualRefA.position);
        Vector3 localB = parentToAlign.InverseTransformPoint(virtualRefB.position);

        // Direction vectors between the two points.
        Vector3 localDir = localB - localA;   // in parent local space, magnitude = real distance between marks
        Vector3 physDir  = physB  - physA;    // in world space

        if (localDir.magnitude < 0.01f)
        {
            Debug.LogWarning("[HandCalibrator] virtualRefA and virtualRefB are too close together — place them further apart for stable calibration.", this);
            return;
        }
        if (physDir.magnitude < 0.01f)
        {
            Debug.LogWarning("[HandCalibrator] Measured hand positions are too close together — the two reference marks must be at least ~5 cm apart in the real world.", this);
            return;
        }

        // ── Rotation ──────────────────────────────────────────────────────────
        // Transform localDir into world space using the *current* parent rotation.
        Vector3 localDirWorld = parentToAlign.rotation * localDir;

        Quaternion rotDelta;
        if (yawOnlyRotation)
        {
            // Project both direction vectors onto XZ and solve yaw-only.
            Vector3 fromDir = Vector3.ProjectOnPlane(localDirWorld, Vector3.up);
            Vector3 toDir   = Vector3.ProjectOnPlane(physDir,       Vector3.up);

            if (fromDir.magnitude < 0.001f || toDir.magnitude < 0.001f)
            {
                Debug.LogWarning("[HandCalibrator] Direction vectors are nearly vertical — cannot solve yaw. Try placing reference marks further apart horizontally.", this);
                return;
            }

            float yaw = Vector3.SignedAngle(fromDir, toDir, Vector3.up);
            rotDelta   = Quaternion.Euler(0f, yaw, 0f);
        }
        else
        {
            // Full 3-D rotation from the direction pair.
            rotDelta = Quaternion.FromToRotation(localDirWorld.normalized, physDir.normalized);
        }

        Quaternion newRot = rotDelta * parentToAlign.rotation;

        // ── Translation ───────────────────────────────────────────────────────
        // Use midpoint for numerical stability (averages error from both measurements).
        Vector3 physMid  = (physA + physB) * 0.5f;
        Vector3 localMid = (localA + localB) * 0.5f;
        Vector3 newPos   = physMid - newRot * localMid;

        // ── Apply ─────────────────────────────────────────────────────────────
        parentToAlign.SetPositionAndRotation(newPos, newRot);

        if (debugLogging)
        {
            Debug.Log($"[HandCalibrator] Alignment applied.\n" +
                      $"  physA={physA:F3}  physB={physB:F3}\n" +
                      $"  rotDelta={rotDelta.eulerAngles:F2}\n" +
                      $"  newPos={newPos:F3}  newRot={newRot.eulerAngles:F2}", this);
        }
    }

    // ── XR helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the world-space position of the INDEX FINGER TIP for the specified hand.
    ///
    /// Priority 1: XRHandSubsystem (com.unity.xr.hands) — reads the IndexTip joint pose
    ///             (tracking-space) and converts to world space via OVRCameraRig.trackingSpace.
    ///             This is the correct, precise path for Meta hand tracking.
    ///
    /// Priority 2: UnityEngine.XR.InputDevices fallback — wrist/controller anchor only,
    ///             no finger data. Used for non-Meta / OpenXR controller setups.
    /// </summary>
    private bool TryGetHandWorldPos(HandSide hand, out Vector3 worldPos)
    {
        // ── Path 1: XRHandSubsystem → IndexTip joint ──────────────────────────
        var handSubsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(handSubsystems);

        if (handSubsystems.Count > 0 && handSubsystems[0].running)
        {
            XRHand xrHand = hand == HandSide.Left
                ? handSubsystems[0].leftHand
                : handSubsystems[0].rightHand;

            if (xrHand.isTracked)
            {
                var joint = xrHand.GetJoint(XRHandJointID.IndexTip);
                if (joint.TryGetPose(out Pose tipPose))
                {
                    // tipPose.position is in XR tracking space.
                    // OVRCameraRig.trackingSpace converts it to Unity world space.
                    if (_trackingSpace != null)
                        worldPos = _trackingSpace.TransformPoint(tipPose.position);
                    else
                        worldPos = tipPose.position;   // rig at origin — still valid

                    if (debugLogging)
                        Debug.Log($"[HandCalibrator] {hand} IndexTip via XRHandSubsystem: {worldPos:F3}", this);
                    return true;
                }
            }

            Debug.LogWarning($"[HandCalibrator] {hand} hand not tracked by XRHandSubsystem — " +
                             "hold your hand in view of the headset.", this);
            worldPos = Vector3.zero;
            return false;
        }

        // ── Path 2: UnityEngine.XR.InputDevices fallback (wrist, no finger data) ──
        Debug.LogWarning("[HandCalibrator] XRHandSubsystem not running — falling back to wrist/controller position. " +
                         "Finger tip offset will NOT be applied.", this);

        if (Camera.main == null)
        {
            worldPos = Vector3.zero;
            return false;
        }

        var headDevices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.Head, headDevices);
        if (headDevices.Count == 0 ||
            !headDevices[0].TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 rawHeadPos) ||
            !headDevices[0].TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rawHeadRot))
        {
            worldPos = Vector3.zero;
            return false;
        }

        Quaternion rigRot = Camera.main.transform.rotation * Quaternion.Inverse(rawHeadRot);

        XRNode node = hand == HandSide.Left ? XRNode.LeftHand : XRNode.RightHand;
        var handDevices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, handDevices);
        if (handDevices.Count == 0 ||
            !handDevices[0].TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 rawHandPos))
        {
            worldPos = Vector3.zero;
            return false;
        }

        worldPos = Camera.main.transform.position + rigRot * (rawHandPos - rawHeadPos);
        if (debugLogging)
            Debug.Log($"[HandCalibrator] {hand} wrist via XR InputDevices (fallback): {worldPos:F3}", this);
        return true;
    }

    private static HandSide Opposite(HandSide h) =>
        h == HandSide.Left ? HandSide.Right : HandSide.Left;
}
