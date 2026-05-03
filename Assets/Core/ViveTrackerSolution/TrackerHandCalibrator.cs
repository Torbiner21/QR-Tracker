using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

/// <summary>
/// Unified calibration that fixes BOTH the virtual model pose AND the tracker's
/// world-calibration in a single keypress.
///
/// WHY THIS EXISTS
/// ─────────────────────────────────────────────────────────────────────────────
/// Single-point T-key sync (AlignChildToViveTarget) can match position but leaves
/// any rotational miss-alignment intact.  Two-point hand alignment
/// (HandAlignmentCalibrator) correctly places the virtual model at calibration
/// time, but does NOT update the tracker's world calibration — so as soon as the
/// real object rotates the tracker drives the model with the old wrong rotation
/// and the two diverge again.
///
/// THIS SCRIPT solves both in one shot:
///   1. Reads two hand index-tip positions as physical ground-truth for two known
///      reference marks on the real object.
///   2. Applies two-point rigid-body alignment to <see cref="parentToAlign"/>, giving
///      the virtual model the correct world pose (full 6-DOF or yaw-only).
///   3. Reads the new world pose of <see cref="trackerSyncChild"/> — the sync point
///      on the virtual model that the tracker is supposed to follow — and back-solves
///      OpenVrTrackerReader's world calibration so tracking continues correctly
///      after the object moves or rotates.
///
/// SETUP
/// ─────────────────────────────────────────────────────────────────────────────
///   parentToAlign    → the virtual model root (same as parentA in AlignChildToViveTarget)
///   trackerSyncChild → the child Transform on the virtual model that should overlap
///                      the tracker target (same as childB in AlignChildToViveTarget)
///   virtualRefA/B    → two child Transforms at known reference marks on the virtual model
///                      (>5 cm apart, geometrically unambiguous)
///   trackerReader    → the OpenVrTrackerReader in the scene
///
/// CALIBRATION PROCEDURE
/// ─────────────────────────────────────────────────────────────────────────────
///   Simultaneous mode  — hold left index tip on mark A and right on mark B (or
///                        swapped, see refAHand), then press calibrateKey.
///   Sequential mode    — press captureAKey while touching mark A, press
///                        captureBKey while touching mark B, auto-applies.
/// </summary>
public class TrackerHandCalibrator : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────────────

    [Header("Required References")]
    [Tooltip("The virtual model root to move (same as parentA in AlignChildToViveTarget).")]
    public Transform parentToAlign;

    [Tooltip("Child Transform on the virtual model that should always overlap the tracker target.\n" +
             "This is the mount/sync point — same as childB in AlignChildToViveTarget.\n" +
             "After alignment its world pose is used to back-solve the tracker calibration.")]
    public Transform trackerSyncChild;

    [Tooltip("The OpenVrTrackerReader in the scene — its world calibration is updated after alignment.")]
    public OpenVrTrackerReader trackerReader;

    [Header("Virtual Reference Marks (children of the virtual model)")]
    [Tooltip("Child Transform on the virtual model at known reference mark A.")]
    public Transform virtualRefA;
    [Tooltip("Child Transform on the virtual model at known reference mark B.")]
    public Transform virtualRefB;

    [Header("Mode")]
    public CalibrationMode mode = CalibrationMode.Simultaneous;
    [Tooltip("Which hand reads reference mark A (the other hand reads B).")]
    public HandSide refAHand = HandSide.Left;

    [Header("Keys — Simultaneous")]
    [Tooltip("Hold both hands on their marks, press this key.")]
    public KeyCode calibrateKey = KeyCode.C;

    [Header("Keys — Sequential")]
    public KeyCode captureAKey    = KeyCode.Alpha1;
    public KeyCode captureBKey    = KeyCode.Alpha2;
    [Tooltip("Set to None to auto-apply once both points are captured.")]
    public KeyCode applyAfterBKey = KeyCode.None;

    [Header("Options")]
    [Tooltip("Only correct yaw (rotation around world Y). Recommended for floor-level objects.")]
    public bool yawOnlyRotation = true;

    [Tooltip("Log debug info to console.")]
    public bool debugLogging = true;

    // ── State ──────────────────────────────────────────────────────────────────

    private Transform _trackingSpace;

    [Header("Debug Read-Only")]
    [SerializeField] private bool    _pointACaptured;
    [SerializeField] private bool    _pointBCaptured;
    [SerializeField] private Vector3 _capturedPhysA;
    [SerializeField] private Vector3 _capturedPhysB;

    public enum CalibrationMode { Simultaneous, Sequential }
    public enum HandSide { Left, Right }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void Start() => DiscoverTrackingSpace();

    private void Update()
    {
        switch (mode)
        {
            case CalibrationMode.Simultaneous:
                if (Input.GetKeyDown(calibrateKey))
                    CalibrateSimultaneous();
                break;

            case CalibrationMode.Sequential:
                if (Input.GetKeyDown(captureAKey))  CapturePointA();
                if (Input.GetKeyDown(captureBKey))  CapturePointB();
                if (applyAfterBKey != KeyCode.None && Input.GetKeyDown(applyAfterBKey))
                    ApplyCapturedPoints();
                break;
        }
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    [ContextMenu("Calibrate — Simultaneous (both hands)")]
    public void CalibrateSimultaneous()
    {
        DiscoverTrackingSpace();
        if (!TryGetHandWorldPos(refAHand,           out Vector3 physA)) return;
        if (!TryGetHandWorldPos(Opposite(refAHand), out Vector3 physB)) return;
        ApplyFullCalibration(physA, physB);
    }

    [ContextMenu("Sequential — Capture Point A")]
    public void CapturePointA()
    {
        DiscoverTrackingSpace();
        if (!TryGetHandWorldPos(refAHand, out _capturedPhysA)) return;
        _pointACaptured = true;
        if (debugLogging) Debug.Log($"[TrackerHandCalibrator] Point A captured: {_capturedPhysA}", this);
        if (_pointACaptured && _pointBCaptured && applyAfterBKey == KeyCode.None)
            ApplyCapturedPoints();
    }

    [ContextMenu("Sequential — Capture Point B")]
    public void CapturePointB()
    {
        DiscoverTrackingSpace();
        if (!TryGetHandWorldPos(Opposite(refAHand), out _capturedPhysB)) return;
        _pointBCaptured = true;
        if (debugLogging) Debug.Log($"[TrackerHandCalibrator] Point B captured: {_capturedPhysB}", this);
        if (_pointACaptured && _pointBCaptured && applyAfterBKey == KeyCode.None)
            ApplyCapturedPoints();
    }

    [ContextMenu("Sequential — Apply Captured Points")]
    public void ApplyCapturedPoints()
    {
        if (!_pointACaptured || !_pointBCaptured)
        {
            Debug.LogWarning("[TrackerHandCalibrator] Cannot apply — capture both points first.", this);
            return;
        }
        ApplyFullCalibration(_capturedPhysA, _capturedPhysB);
        _pointACaptured = _pointBCaptured = false;
    }

    [ContextMenu("Reset Captures")]
    public void ResetCaptures()
    {
        _pointACaptured = _pointBCaptured = false;
        Debug.Log("[TrackerHandCalibrator] Captures reset.", this);
    }

    // ── Core ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Full two-step calibration:
    ///   Step 1 — Two-point rigid-body alignment of the virtual model.
    ///   Step 2 — Back-solve the tracker world calibration from the new sync-child pose.
    /// </summary>
    private void ApplyFullCalibration(Vector3 physA, Vector3 physB)
    {
        if (parentToAlign == null || virtualRefA == null || virtualRefB == null)
        {
            Debug.LogWarning("[TrackerHandCalibrator] Assign parentToAlign, virtualRefA, and virtualRefB.", this);
            return;
        }

        // ── Guard: hand positions must be distinct ─────────────────────────────
        if (Vector3.Distance(physA, physB) < 0.01f)
        {
            Debug.LogError("[TrackerHandCalibrator] physA and physB are identical (< 1 cm apart).\n" +
                           "Hand positions were not read correctly — verify hand tracking is running.", this);
            return;
        }

        // ── Step 1: Two-point virtual model alignment ──────────────────────────
        // Capture local offsets BEFORE moving the parent.
        Vector3 localA = parentToAlign.InverseTransformPoint(virtualRefA.position);
        Vector3 localB = parentToAlign.InverseTransformPoint(virtualRefB.position);

        Vector3 localDir = localB - localA;
        Vector3 physDir  = physB  - physA;

        if (localDir.magnitude < 0.01f)
        {
            Debug.LogWarning("[TrackerHandCalibrator] virtualRefA and virtualRefB are less than 1 cm apart " +
                             "in the virtual model — move them further apart for stable calibration.", this);
            return;
        }
        if (physDir.magnitude < 0.01f)
        {
            Debug.LogWarning("[TrackerHandCalibrator] Measured hand positions are too close together.", this);
            return;
        }

        Vector3 localDirWorld = parentToAlign.rotation * localDir;

        Quaternion rotDelta;
        if (yawOnlyRotation)
        {
            Vector3 fromDir = Vector3.ProjectOnPlane(localDirWorld, Vector3.up);
            Vector3 toDir   = Vector3.ProjectOnPlane(physDir,       Vector3.up);

            if (fromDir.magnitude < 0.001f || toDir.magnitude < 0.001f)
            {
                Debug.LogWarning("[TrackerHandCalibrator] Direction vectors are nearly vertical — cannot solve yaw. " +
                                 "Place reference marks further apart horizontally.", this);
                return;
            }

            float yaw = Vector3.SignedAngle(fromDir, toDir, Vector3.up);
            rotDelta  = Quaternion.Euler(0f, yaw, 0f);
        }
        else
        {
            rotDelta = Quaternion.FromToRotation(localDirWorld.normalized, physDir.normalized);
        }

        Quaternion newParentRot = rotDelta * parentToAlign.rotation;
        Vector3    physMid  = (physA + physB) * 0.5f;
        Vector3    localMid = (localA + localB) * 0.5f;
        Vector3    newParentPos = physMid - newParentRot * localMid;

        parentToAlign.SetPositionAndRotation(newParentPos, newParentRot);

        if (debugLogging)
        {
            Debug.Log($"[TrackerHandCalibrator] Step 1 — Virtual model aligned.\n" +
                      $"  physA={physA:F3}  physB={physB:F3}\n" +
                      $"  rotDelta={rotDelta.eulerAngles:F2}\n" +
                      $"  parentPos={newParentPos:F3}  parentRot={newParentRot.eulerAngles:F2}", this);
        }

        // ── Step 2: Back-solve tracker world calibration ───────────────────────
        // After step 1, trackerSyncChild is now at the correct world pose.
        // Feed that to the tracker reader so it updates its world calib accordingly.
        if (trackerReader != null && trackerSyncChild != null)
        {
            Vector3    desiredTrackerPos = trackerSyncChild.position;
            Quaternion desiredTrackerRot = trackerSyncChild.rotation;

            trackerReader.ApplyTrackerHandCalibration(desiredTrackerPos, desiredTrackerRot);

            if (debugLogging)
            {
                Debug.Log($"[TrackerHandCalibrator] Step 2 — Tracker world calibration updated.\n" +
                          $"  syncChild pos={desiredTrackerPos:F3}  rot={desiredTrackerRot.eulerAngles:F2}", this);
            }
        }
        else
        {
            if (trackerReader == null)
                Debug.LogWarning("[TrackerHandCalibrator] trackerReader is null — tracker calibration skipped.\n" +
                                 "Assign OpenVrTrackerReader to also fix tracker orientation.", this);
            if (trackerSyncChild == null)
                Debug.LogWarning("[TrackerHandCalibrator] trackerSyncChild is null — tracker calibration skipped.\n" +
                                 "Assign the child Transform that corresponds to the tracker mount point.", this);
        }
    }

    // ── Tracking-space discovery ───────────────────────────────────────────────

    private void DiscoverTrackingSpace()
    {
        if (_trackingSpace != null) return;
        var rig = FindObjectOfType<OVRCameraRig>();
        if (rig != null)
        {
            _trackingSpace = rig.trackingSpace;
            if (debugLogging)
                Debug.Log($"[TrackerHandCalibrator] Discovered OVRCameraRig trackingSpace='{_trackingSpace.name}'", this);
        }
        else if (debugLogging)
        {
            Debug.LogWarning("[TrackerHandCalibrator] OVRCameraRig not found yet — will retry on next calibration.", this);
        }
    }

    // ── XR helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the world-space position of the INDEX FINGER TIP for the given hand.
    ///
    /// Path 1: XRHandSubsystem (Meta hand tracking) — IndexTip joint in tracking space,
    ///         converted to world space via OVRCameraRig.trackingSpace.
    /// Path 2: XR InputDevices fallback — wrist/controller position (no finger data).
    /// </summary>
    private bool TryGetHandWorldPos(HandSide hand, out Vector3 worldPos)
    {
        // ── Path 1: XRHandSubsystem ────────────────────────────────────────────
        var subsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);

        if (subsystems.Count > 0 && subsystems[0].running)
        {
            XRHand xrHand = hand == HandSide.Left ? subsystems[0].leftHand : subsystems[0].rightHand;

            if (xrHand.isTracked)
            {
                var joint = xrHand.GetJoint(XRHandJointID.IndexTip);
                if (joint.TryGetPose(out Pose tipPose))
                {
                    worldPos = _trackingSpace != null
                        ? _trackingSpace.TransformPoint(tipPose.position)
                        : tipPose.position;

                    if (debugLogging)
                        Debug.Log($"[TrackerHandCalibrator] {hand} IndexTip (XRHandSubsystem): {worldPos:F3}", this);
                    return true;
                }
            }

            Debug.LogWarning($"[TrackerHandCalibrator] {hand} hand not tracked — hold it in view of the headset.", this);
            worldPos = Vector3.zero;
            return false;
        }

        // ── Path 2: InputDevices fallback (wrist, no finger data) ─────────────
        Debug.LogWarning("[TrackerHandCalibrator] XRHandSubsystem not running — falling back to wrist position.", this);

        if (Camera.main == null) { worldPos = Vector3.zero; return false; }

        var headDevices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.Head, headDevices);
        if (headDevices.Count == 0 ||
            !headDevices[0].TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 rawHeadPos) ||
            !headDevices[0].TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rawHeadRot))
        {
            worldPos = Vector3.zero;
            return false;
        }

        Quaternion rigRot  = Camera.main.transform.rotation * Quaternion.Inverse(rawHeadRot);
        XRNode     node    = hand == HandSide.Left ? XRNode.LeftHand : XRNode.RightHand;

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
            Debug.Log($"[TrackerHandCalibrator] {hand} wrist (InputDevices fallback): {worldPos:F3}", this);
        return true;
    }

    private static HandSide Opposite(HandSide h) => h == HandSide.Left ? HandSide.Right : HandSide.Left;
}
