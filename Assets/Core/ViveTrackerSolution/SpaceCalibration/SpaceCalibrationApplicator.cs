using UnityEngine;

namespace ViveTrackerSolution.SpaceCalibration
{
    /// <summary>
    /// Applies the solved space calibration to the raw SteamVR tracker pose every frame,
    /// producing a corrected output Transform in Meta (OVR) world space.
    ///
    /// IMPORTANT: The calibration was solved against OpenVrTrackerReader.rawPosition /
    /// rawRotationQ (the pose BEFORE any sync-origin, offset, or world-calibration
    /// processing).  This component reads the same raw values so the transform is
    /// applied consistently.
    ///
    /// SETUP
    /// ──────────────────────────────────────────────────────────────────────────
    ///   manager         → the SpaceCalibratorManager in the scene
    ///   trackerReader   → the OpenVrTrackerReader whose rawPosition/rawRotationQ
    ///                     will be corrected each frame
    ///   correctedOutput → Transform that receives the calibrated Meta-space pose
    ///
    /// If no valid profile is loaded the output mirrors the raw tracker pose as-is.
    /// </summary>
    public class SpaceCalibrationApplicator : MonoBehaviour
    {
        // ── Inspector ───────────────────────────────────────────────────────────

        [Header("References")]
        public SpaceCalibratorManager manager;

        [Tooltip("The OpenVrTrackerReader to read raw pose from.\n" +
                 "Must be the same reader referenced in SpaceCalibratorManager.")]
        public OpenVrTrackerReader trackerReader;

        [Tooltip("Transform that receives the corrected Meta-space pose each frame.")]
        public Transform correctedOutput;

        [Header("Options")]
        [Tooltip("Apply correction to position.")]
        public bool applyPosition = true;

        [Tooltip("Apply correction to rotation.")]
        public bool applyRotation = true;

        [Tooltip("Show a debug line between the raw and corrected position in the Scene view.")]
        public bool showDebugLine = true;

        // ── Unity ───────────────────────────────────────────────────────────────

        private void LateUpdate()
        {
            if (trackerReader == null || correctedOutput == null) return;

            Vector3    rawPos = trackerReader.preCalibratedPos;
            Quaternion rawRot = trackerReader.preCalibratedRot;

            if (manager == null || !manager.HasValidProfile)
            {
                // No calibration — pass through raw pose unchanged
                if (applyPosition) correctedOutput.position = rawPos;
                if (applyRotation) correctedOutput.rotation = rawRot;
                return;
            }

            CalibrationProfile profile = manager.ActiveProfile;

            if (applyPosition)
                correctedOutput.position = profile.TransformPoint(rawPos);

            if (applyRotation)
                correctedOutput.rotation = profile.TransformRotation(rawRot);
        }

        // ── Gizmos ───────────────────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            if (!showDebugLine) return;
            if (trackerReader == null || correctedOutput == null) return;
            if (manager == null || !manager.HasValidProfile) return;

            Vector3 raw       = trackerReader.rawPosition;
            Vector3 corrected = manager.ActiveProfile.TransformPoint(raw);

            Gizmos.color = new Color(1f, 0.4f, 0f, 0.8f);
            Gizmos.DrawSphere(raw, 0.015f);
            Gizmos.color = Color.cyan;
            Gizmos.DrawSphere(corrected, 0.015f);
            Gizmos.color = Color.white;
            Gizmos.DrawLine(raw, corrected);
        }
    }
}
