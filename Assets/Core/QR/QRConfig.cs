using UnityEngine;

[CreateAssetMenu(fileName = "QR_Config", menuName = "Config/QR", order = 100)]
public class QRScannerConfig : ScriptableObject
{
    [Header("Core Behavior")]
    [Tooltip("Start automatically on Start().")]
    public bool autoStart = true;

    [Tooltip("Stop scanning after final placement.")]
    public bool stopAfterFinalize = true;

    [Tooltip("Filter by payload substring (case-insensitive). Leave empty to accept the first QR seen.")]
    public string payloadContainsFilter = "";

    [Header("Accuracy / Stability Thresholds")]
    [Tooltip("How many accepted bursts are required before finalizing.")]
    [Min(1)] public int iterationAmount = 10;

    [Tooltip("RMS position threshold (mm) within a burst for it to be accepted.")]
    public float burstPosRmsThresholdMm = 1f;

    [Tooltip("RMS rotation threshold (deg) within a burst for it to be accepted.")]
    public float burstAngleRmsThresholdDeg = 5f;

    [Header("Sampling / Timing")]
    [Tooltip("Frames per burst (more = smoother, slower).")]
    [Range(3, 20)] public int samplesPerBurst = 7;

    [Tooltip("Minimum delay (s) between accepted bursts for the same QR.")]
    public float interScanIntervalSeconds = 1.5f;

    [Tooltip("Wait time after (re)acquiring a QR before accepting new bursts.")]
    public float reacquireCooldownSeconds = 0.4f;

    [Header("View Gating (Camera ↔ QR)")]
    [Tooltip("Reject if view deviates from face-on by more than this (deg). 0 disables.")]
    [Range(0f, 90f)] public float maxViewAngleFromNormalDeg = 15f;

    [Tooltip("Require different viewpoints between bursts (false = accept from same angle).")]
    public bool requireViewpointDiversity = false;

    [Tooltip("Minimum angular difference (deg) between camera view directions of accepted bursts.")]
    [Range(0f, 90f)] public float minAngleBetweenBurstsDeg = 10f;

    [Tooltip("Minimum camera distance change (m) between accepted bursts to count as diverse.")]
    [Min(0f)] public float minDistanceDeltaM = 0.05f;

    [Header("Robust Final Averaging")]
    [Tooltip("Number of recent accepted bursts used for final computation (0 = all).")]
    [Min(0)] public int finalWindow = 20;

    [Tooltip("Maximum positional difference (mm) allowed as inlier in final averaging.")]
    public float inlierMaxMm = 3f;

    [Tooltip("Maximum angular difference (deg) allowed as inlier in final averaging.")]
    public float inlierMaxDeg = 3f;

    [Tooltip("IRLS iterations for robust averaging.")]
    [Range(1, 10)] public int irlsIterations = 4;

    [Tooltip("Huber threshold (mm) for translation residuals in IRLS.")]
    public float huberPosK_mm = 3f;

    [Tooltip("Huber threshold (deg) for rotation residuals in IRLS.")]
    public float huberRotK_deg = 3f;

    [Header("Utility / Reset")]
    [Tooltip("Keyboard key to restart calibration while testing in Editor.")]
    public KeyCode restartKey = KeyCode.Y;

    [Tooltip("Restore target to original pose when restarting.")]
    public bool resetTargetOnRestart = false;

    [Tooltip("Invoke Additional events.")]
    public bool invokeAdditionalEvents = false;
}
