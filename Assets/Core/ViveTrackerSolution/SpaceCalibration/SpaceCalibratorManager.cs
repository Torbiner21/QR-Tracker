using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ViveTrackerSolution.SpaceCalibration
{
    /// <summary>
    /// Solves the rigid-body transform T so that:
    ///
    ///   p_meta = T.rotation * p_steamvr + T.translation
    ///
    /// PRINCIPLE (same as OpenVR-SpaceCalibrator)
    /// ──────────────────────────────────────────────────────────────────────────
    /// Hold a Meta-tracked device (e.g. Touch controller) rigidly together with
    /// a SteamVR-tracked device (e.g. Vive Tracker 3.0).  Both devices report the
    /// same physical pose, just in their own coordinate systems.
    ///
    /// We collect N pose-pair samples while moving the devices around, then solve:
    ///
    ///   Rotation   — average of q_meta_i * Inverse(q_steam_i)  for every sample i
    ///   Translation — average of (p_meta_i − R * p_steam_i)    for every sample i
    ///
    /// As long as the Lighthouse base stations and the Quest guardian are NOT moved,
    /// this calibration stays valid indefinitely.  The result is persisted to
    /// CalibrationProfile.json inside Application.persistentDataPath.
    ///
    /// SETUP
    /// ──────────────────────────────────────────────────────────────────────────
    ///   metaDevice    → Transform that is driven by Meta/OVR tracking
    ///                   (e.g. the OVRCameraRig hand anchor, or any OVR-tracked Transform)
    ///   steamVrDevice → Transform driven by SteamVR tracking
    ///                   (e.g. the trackerTarget of your OpenVrTrackerReader)
    ///
    /// CALIBRATION PROCEDURE
    /// ──────────────────────────────────────────────────────────────────────────
    ///   1. Hold the two devices physically together (zip-tied is ideal).
    ///   2. Call StartCalibration() (or press calibrateKey).
    ///   3. Slowly move + rotate the pair through as many orientations as possible
    ///      (like calibrating a phone compass).
    ///   4. After <see cref="requiredSamples"/> are collected the solve runs automatically,
    ///      the profile is saved, and <see cref="OnCalibrationComplete"/> is fired.
    /// </summary>
    public class SpaceCalibratorManager : MonoBehaviour
    {
        // ── Inspector ───────────────────────────────────────────────────────────
        public AudioSource calibrationAudioSource;  // optional AudioSource for debug beeps (e.g. on calibration complete)
        public AudioClip calibrationCompleteClip;  // optional clip to play when calibration completes
        public AudioClip calibrationStartClip; 
        // ── Tracking source enums ────────────────────────────────────────────
        public enum TrackingSource { Controller, HandTracking }
        public enum HandSide { Right, Left }

        [Header("Meta Tracking Source")]
        [Tooltip("Use a Touch controller (held together with the tracker) or an open hand (tracker placed on palm).")]
        public TrackingSource trackingSource = TrackingSource.Controller;

        [Tooltip("Which hand / controller to use during calibration.")]
        public HandSide handSide = HandSide.Right;

        [Tooltip("The OVRCameraRig to read the anchor from.  Leave null — auto-found at runtime.")]
        public OVRCameraRig ovrCameraRig;

        [Tooltip("Transform driven by Meta / OVR tracking.  Leave null — auto-resolved from trackingSource + handSide + ovrCameraRig at runtime.")]
        public Transform metaDevice;

        [Header("SteamVR Tracker")]
        [Tooltip("Assign the OpenVrTrackerReader component.  The calibrator reads its rawPosition / rawRotationQ " +
                 "— the pose BEFORE any sync-origin, offset, or world-calibration processing.\n" +
                 "This is required for a correct solve; reading trackerTarget directly gives wrong results "+
                 "because all the offsets are already baked in.")]
        public OpenVrTrackerReader trackerReader;

        // ── Readiness indicators (read-only in Inspector) ─────────────────────
        [Header("Readiness  ——  both must be true to calibrate")]
        [Tooltip("True when the selected Meta/OVR controller is tracking.")]
        [SerializeField] private bool _metaControllerReady;

        [Tooltip("True when the OpenVrTrackerReader is assigned and its rawPosition is non-zero (tracker is being seen by base stations).")]
        [SerializeField] private bool _steamVrTrackerReady;

        [Tooltip("True when both devices are ready and calibration can start.")]
        [SerializeField] private bool _readyToCalibrate;

        [Header("Calibration Settings")]
        [Tooltip("Number of pose-pair samples to collect before solving.")]
        [Range(20, 1000)]
        public int requiredSamples = 200;

        [Tooltip("Seconds between each sample capture.")]
        [Range(0.02f, 0.5f)]
        public float sampleInterval = 0.05f;

        [Tooltip("Minimum metres the Meta device must move between samples (avoids duplicate poses).")]
        public float minMovementThreshold = 0.005f;

        [Tooltip("Minimum metres of movement required in EACH world axis before the solve is allowed.\n" +
                 "The yaw (horizontal room rotation) is constrained by left\u2011right (X) spread.\n" +
                 "If X spread is too low, yaw will be wrong and you\u2019ll see diagonal drift when walking.\n" +
                 "Raise this if you keep getting drift; lower it only in tight spaces.")]
        public float minSpreadPerAxis = 0.18f;   // 18 cm per axis

        [Tooltip("Key to start calibration (optional — call StartCalibration() from code too).  Default F8 — avoid C which is used for hand calibration.")]
        public KeyCode calibrateKey = KeyCode.F8;

        [Tooltip("Auto-load a saved profile on Start.")]
        public bool autoLoadOnStart = true;

        [Tooltip("Both Meta Quest and SteamVR define Y as 'up' using gravity/IMU.\n" +
                 "The calibration rotation between them should therefore be pure yaw (Y-axis only).\n" +
                 "Enabling this discards the noisy pitch+roll from the Kabsch solve, which is the " +
                 "cause of Z-movement producing Y drift.\n" +
                 "Disable only if your floor is significantly tilted relative to one of the systems.")]
        public bool constrainToYawOnly = true;

        // ── Events ──────────────────────────────────────────────────────────────

        /// <summary>Fired when calibration completes and the profile is saved.</summary>
        public event Action<CalibrationProfile> OnCalibrationComplete;

        /// <summary>Fired every time a new sample is added.  int = samples so far.</summary>
        public event Action<int, int> OnProgress;   // (current, required)

        /// <summary>Fired when calibration is cancelled before completion.</summary>
        public event Action OnCalibrationCancelled;

        // ── Public State ────────────────────────────────────────────────────────

        public enum State { Idle, Collecting, Solved }
        public State CurrentState { get; private set; } = State.Idle;

        /// <summary>Currently active (or most recently loaded) calibration profile.  Null if none.</summary>
        public CalibrationProfile ActiveProfile { get; private set; }

        public bool HasValidProfile => ActiveProfile != null;

        /// <summary>
        /// True when a tracking-origin-change event fired but the runtime could not supply
        /// the delta pose needed to compensate.  The calibration may be offset — consider
        /// recalibrating.  Cleared automatically when a successful compensation runs.
        /// </summary>
        public bool ProfileMayBeInvalid { get; private set; }

        /// <summary>How many samples have been collected in the current session.</summary>
        public int SamplesCollected => _samples.Count;

        // ── Private ─────────────────────────────────────────────────────────────

        private struct PosePair
        {
            public Vector3    metaPos;
            public Quaternion metaRot;
            public Vector3    steamPos;
            public Quaternion steamRot;
        }

        private readonly List<PosePair> _samples   = new List<PosePair>();
        private Vector3                 _lastMetaPos;
        private Coroutine               _collectCoroutine;

        // ── Unity ───────────────────────────────────────────────────────────────
        private void OnEnable()
        {
            OnCalibrationComplete                  += OnCalibrationCompleteAudio;
            OVRManager.TrackingOriginChangePending += OnTrackingOriginChangePending;
        }

        private void OnDisable()
        {
            OnCalibrationComplete                  -= OnCalibrationCompleteAudio;
            OVRManager.TrackingOriginChangePending -= OnTrackingOriginChangePending;
        }

        private void Start()
        {
            ResolveMetaDevice();
            if (autoLoadOnStart)
                TryLoadProfile();
        }

        private void Update()
        {
            // ── Refresh readiness every frame (drives inspector checkboxes) ────
            RefreshReadiness();

            if (Input.GetKeyDown(calibrateKey))
            {
                if (CurrentState == State.Collecting)
                    CancelCalibration();
                else
                    StartCalibration();
            }
        }

        private void OnCalibrationCompleteAudio(CalibrationProfile profile)
        {
            if (calibrationAudioSource != null && calibrationCompleteClip != null)
                calibrationAudioSource.PlayOneShot(calibrationCompleteClip);
        }

        // ── Recenter compensation ─────────────────────────────────────────────────

        /// <summary>
        /// Fired by OVRManager before the Meta tracking origin shifts (guardian reset,
        /// headset re-mount, user recentering, etc.).
        /// When <paramref name="poseInPreviousSpace"/> is available the profile and sample
        /// cloud are mathematically shifted into the new space — no re-gather needed.
        /// When it is null the runtime could not supply the delta; the profile is flagged.
        /// </summary>
        private void OnTrackingOriginChangePending(OVRManager.TrackingOrigin origin, OVRPose? poseInPreviousSpace)
        {
            if (ActiveProfile == null) return;

            if (!poseInPreviousSpace.HasValue)
            {
                ProfileMayBeInvalid = true;
                Debug.LogWarning("[SpaceCalibration] Tracking origin changed but no delta was provided. " +
                                 "Profile may be misaligned — recalibrate if drift is observed.", this);
                return;
            }

            StartCoroutine(CompensateForRecenter(poseInPreviousSpace.Value));
        }

        /// <summary>
        /// Waits one frame for the new tracking space to take effect, then applies the
        /// inverse of the origin-shift delta to both the profile transform and the
        /// persisted sample cloud.  Saves the updated profile to disk automatically.
        ///
        /// Math:  new-space point  =  Q⁻¹ × (old-space point − T)
        ///   where Q,T  =  poseInPreviousSpace (new origin expressed in old space)
        /// </summary>
        private IEnumerator CompensateForRecenter(OVRPose newOriginInOldSpace)
        {
            yield return null;   // wait one frame — OVR anchors have now updated

            if (ActiveProfile == null) yield break;

            var invRot = Quaternion.Inverse(newOriginInOldSpace.orientation);
            var offset = newOriginInOldSpace.position;

            // ── Shift the sample cloud into the new guardian space ────────────
            // SteamVR positions are Lighthouse-absolute and never change.
            // Only Meta positions need to be re-expressed in the new space.
            for (int i = 0; i < _samples.Count; i++)
            {
                var s     = _samples[i];
                s.metaPos = invRot * (s.metaPos - offset);
                s.metaRot = invRot * s.metaRot;
                _samples[i] = s;
            }

            // ── Shift the profile transform into the new guardian space ───────
            var newTrans = invRot * (ActiveProfile.Translation - offset);
            var newRot   = invRot * ActiveProfile.Rotation;

            ActiveProfile = CalibrationProfile.Create(newRot, newTrans, ActiveProfile.sampleCount);
            SaveSamplesToProfile(ActiveProfile, _samples);
            ActiveProfile.Save();
            trackerReader?.SetSpaceCalibration(ActiveProfile);
            ProfileMayBeInvalid = false;

            Debug.Log($"[SpaceCalibration] Recenter compensation applied — " +
                      $"new trans={newTrans:F3}  rot={newRot.eulerAngles:F1}", this);
        }

        // ── Device resolution ────────────────────────────────────────────────────

        /// <summary>
        /// If metaDevice is not manually assigned, find the correct controller
        /// anchor from the OVRCameraRig based on <see cref="trackingSource"/> and <see cref="handSide"/>.
        /// </summary>
        // Derive the OVRInput.Controller value from the two clean enums.
        private OVRInput.Controller ResolvedOvrController =>
            trackingSource == TrackingSource.HandTracking
                ? (handSide == HandSide.Left ? OVRInput.Controller.LHand  : OVRInput.Controller.RHand)
                : (handSide == HandSide.Left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch);

        private void ResolveMetaDevice()
        {
            if (metaDevice != null) return;   // already manually set

            if (ovrCameraRig == null)
                ovrCameraRig = FindObjectOfType<OVRCameraRig>();

            if (ovrCameraRig == null)
            {
                Debug.LogWarning("[SpaceCalibration] No OVRCameraRig found — assign metaDevice manually.");
                return;
            }

            metaDevice = handSide == HandSide.Left
                ? ovrCameraRig.leftHandAnchor
                : ovrCameraRig.rightHandAnchor;

            Debug.Log($"[SpaceCalibration] metaDevice auto-resolved to {metaDevice.name} " +
                      $"({trackingSource} / {handSide})");
        }

        /// <summary>
        /// Updates the three inspector readiness flags every frame.
        /// Meta controller readiness uses OVRInput position-tracking.
        /// SteamVR readiness checks the Transform is assigned and non-zero.
        /// </summary>
        private void RefreshReadiness()
        {
            // OVR side — OVRInput.GetControllerPositionTracked returns true when
            // the selected controller/hand has a valid tracked pose.
            _metaControllerReady = OVRInput.GetControllerPositionTracked(ResolvedOvrController);

            // SteamVR side — trackerReader assigned and rawPosition is non-zero
            // (OpenVR returns exactly (0,0,0) for untracked devices).
            _steamVrTrackerReady = trackerReader != null &&
                                   trackerReader.rawPosition != Vector3.zero;

            _readyToCalibrate = _metaControllerReady && _steamVrTrackerReady;
        }

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>Begin collecting samples.  Safe to call multiple times — cancels any in-progress session first.</summary>
        public void StartCalibration()
        {
            // Always re-resolve the Meta device on every calibration start.
            // OVRCameraRig may not exist at scene Start but is ready by the time
            // the user triggers calibration, so we never rely on the cached reference.
            metaDevice   = null;
            ovrCameraRig = null;
            ResolveMetaDevice();

            if (metaDevice == null || trackerReader == null)
            {
                Debug.LogError("[SpaceCalibration] metaDevice or trackerReader is not assigned.");
                return;
            }

            if (!_readyToCalibrate)
            {
                Debug.LogWarning($"[SpaceCalibration] Devices not ready — " +
                                 $"Meta controller tracked: {_metaControllerReady}, " +
                                 $"SteamVR tracker ready: {_steamVrTrackerReady}.  Aborting.");
                return;
            }

            if (_collectCoroutine != null)
                StopCoroutine(_collectCoroutine);

            // Clear any previously loaded/solved profile so preCalibratedPos reflects
            // the raw legacy pipeline during sample collection.
            if (trackerReader != null)
                trackerReader.SetSpaceCalibration(null);
            ActiveProfile = null;

            _samples.Clear();
            _lastMetaPos      = metaDevice.position;
            CurrentState      = State.Collecting;

            
            if (calibrationAudioSource != null && calibrationStartClip != null)
                calibrationAudioSource.PlayOneShot(calibrationStartClip);
            Debug.Log($"[SpaceCalibration] Calibration started — collecting {requiredSamples} samples.");
            _collectCoroutine = StartCoroutine(CollectSamplesCoroutine());
        }

        /// <summary>Abort a running calibration without saving.</summary>
        public void CancelCalibration()
        {
            if (_collectCoroutine != null)
            {
                StopCoroutine(_collectCoroutine);
                _collectCoroutine = null;
            }
            _samples.Clear();
            CurrentState = State.Idle;
            Debug.Log("[SpaceCalibration] Calibration cancelled.");
            OnCalibrationCancelled?.Invoke();
        }

        /// <summary>
        /// Attempt to load an existing calibration profile from disk.
        /// Returns true if a valid profile was found.
        /// </summary>
        public bool TryLoadProfile()
        {
            var profile = CalibrationProfile.Load();
            if (profile == null) return false;

            ActiveProfile = profile;
            CurrentState  = State.Solved;
            LoadSamplesFromProfile(profile);
            if (trackerReader != null)
                trackerReader.SetSpaceCalibration(ActiveProfile);
            else
                Debug.LogWarning("[SpaceCalibration] Profile loaded but trackerReader is not assigned — " +
                                 "SetSpaceCalibration() will be called when StartCalibration() is next invoked.");
            Debug.Log($"[SpaceCalibration] Loaded: {profile}");
            return true;
        }

        /// <summary>Convert a raw SteamVR world-space position to Meta world-space.</summary>
        public Vector3 ApplyToPosition(Vector3 steamVrWorldPos)
        {
            if (!HasValidProfile)
            {
                Debug.LogWarning("[SpaceCalibration] No valid profile — returning original position.");
                return steamVrWorldPos;
            }
            return ActiveProfile.TransformPoint(steamVrWorldPos);
        }

        /// <summary>Convert a raw SteamVR world-space rotation to Meta world-space rotation.</summary>
        public Quaternion ApplyToRotation(Quaternion steamVrWorldRot)
        {
            if (!HasValidProfile) return steamVrWorldRot;
            return ActiveProfile.TransformRotation(steamVrWorldRot);
        }

        // ── Sample collection ────────────────────────────────────────────────────

        private IEnumerator CollectSamplesCoroutine()
        {
            while (_samples.Count < requiredSamples)
            {
                yield return new WaitForSeconds(sampleInterval);

                // Skip if neither device is moving enough (avoids duplicate poses)
                float moved = Vector3.Distance(metaDevice.position, _lastMetaPos);
                if (moved < minMovementThreshold)
                    continue;

                _samples.Add(new PosePair
                {
                    metaPos  = metaDevice.position,
                    metaRot  = metaDevice.rotation,
                    // calibRawPosition = standard OpenVR→Unity (m3, m7, -m11), no axis remapping.
                    // Both this and metaDevice.position are Y-up left-handed, so
                    // Kabsch finds the correct rigid transform between the two spaces.
                    steamPos = trackerReader.calibRawPosition,
                    steamRot = trackerReader.rawRotationQ
                });
                _lastMetaPos = metaDevice.position;

                OnProgress?.Invoke(_samples.Count, requiredSamples);

                if (_samples.Count % 10 == 0)
                    Debug.Log($"[SpaceCalibration] {_samples.Count} / {requiredSamples} samples collected.");
            }

            Debug.Log("[SpaceCalibration] Sample collection complete — solving transform...");
            SolveAndSave();
        }

        /// <summary>
        /// Continues collecting samples beyond requiredSamples until spread is sufficient.
        /// Called automatically by SolveAndSave when spread is too low on any axis.
        /// </summary>
        private IEnumerator CollectExtraSamplesCoroutine()
        {
            int maxExtra = requiredSamples * 3;   // never loop forever
            while (_samples.Count < requiredSamples + maxExtra)
            {
                yield return new WaitForSeconds(sampleInterval);

                float moved = Vector3.Distance(metaDevice.position, _lastMetaPos);
                if (moved < minMovementThreshold) continue;

                _samples.Add(new PosePair
                {
                    metaPos  = metaDevice.position,
                    metaRot  = metaDevice.rotation,
                    steamPos = trackerReader.calibRawPosition,
                    steamRot = trackerReader.rawRotationQ
                });
                _lastMetaPos = metaDevice.position;
                OnProgress?.Invoke(_samples.Count, requiredSamples + maxExtra);

                // Re-check spread every 25 extra samples
                if (_samples.Count % 25 == 0)
                {
                    Vector3 sMin = _samples[0].steamPos, sMax = _samples[0].steamPos;
                    foreach (var sp in _samples) { sMin = Vector3.Min(sMin, sp.steamPos); sMax = Vector3.Max(sMax, sp.steamPos); }
                    Vector3 spread = sMax - sMin;
                    float minS2 = Mathf.Min(spread.x, Mathf.Min(spread.y, spread.z));
                    string weakAxis = spread.x < spread.y && spread.x < spread.z ? "X \u2192 move LEFT\u2011RIGHT"
                                    : spread.y < spread.z                         ? "Y \u2192 move UP\u2011DOWN"
                                    : "Z \u2192 move FORWARD\u2011BACK";
                    Debug.Log($"[SpaceCalibration] Extra sample {_samples.Count} " +
                              $"spread X={spread.x*100f:F0} Y={spread.y*100f:F0} Z={spread.z*100f:F0} cm  weakest: {weakAxis}");
                    if (minS2 >= minSpreadPerAxis)
                    {
                        Debug.Log("[SpaceCalibration] Spread sufficient \u2014 solving now.");
                        SolveAndSave();
                        yield break;
                    }
                }
            }
            Debug.LogWarning("[SpaceCalibration] Hit max extra-sample limit without reaching spread target. " +
                             "Solving with best available data. Move more side-to-side next time.");
            SolveAndSave();
        }

        private void SolveAndSave()
        {
            // ── Diagnostic: log raw first-sample coords ─────────────────────────
            // This lets you instantly see if the two coordinate spaces are even
            // in the same ballpark before the solve.
            var s0 = _samples[0];
            // Compute per-axis variance to confirm the SteamVR point cloud is 3-D
            Vector3 steamMin = s0.steamPos, steamMax = s0.steamPos;
            foreach (var s in _samples)
            {
                steamMin = Vector3.Min(steamMin, s.steamPos);
                steamMax = Vector3.Max(steamMax, s.steamPos);
            }
            Vector3 steamSpread = steamMax - steamMin;

            // ── Abort and request more movement if spread is too low ───────────
            // Low spread on an axis = that axis's rotation is poorly constrained.
            // Low X spread is the most dangerous: it leaves yaw (Y-rotation) ambiguous
            // and causes diagonal drift that grows linearly with walking distance.
            float minS = Mathf.Min(steamSpread.x, Mathf.Min(steamSpread.y, steamSpread.z));
            if (minS < minSpreadPerAxis)
            {
                string badAxis = steamSpread.x < steamSpread.y && steamSpread.x < steamSpread.z ? "X (left\u2011right)"
                               : steamSpread.y < steamSpread.z ? "Y (up\u2011down)"
                               : "Z (forward\u2011back)";
                Debug.LogWarning($"[SpaceCalibration] Spread too low on {badAxis}: " +
                                 $"{minS*100f:F1} cm < {minSpreadPerAxis*100f:F0} cm minimum.\n" +
                                 $"Keep moving and the solver will retry automatically once coverage is sufficient.");
                // Collect more samples until spread is met, up to 3\u00d7 requiredSamples
                _collectCoroutine = StartCoroutine(CollectExtraSamplesCoroutine());
                return;
            }

            Debug.Log($"[SpaceCalibration] Data quality:\n" +
                      $"  Meta    pos={s0.metaPos:F3}  rot={s0.metaRot.eulerAngles:F1}\n" +
                      $"  SteamVR pos={s0.steamPos:F3}  rot={s0.steamRot.eulerAngles:F1}\n" +
                      $"  Raw pos gap = {(s0.metaPos - s0.steamPos).magnitude * 100f:F1} cm\n" +
                      $"  SteamVR spread X={steamSpread.x*100f:F1} Y={steamSpread.y*100f:F1} Z={steamSpread.z*100f:F1} cm  samples={_samples.Count}");

            // ── Step 1: Rotation via delta-axis Kabsch ─────────────────────────
            //
            // For each pair of samples the two rigidly-attached devices rotate
            // identically, so their rotation-axis vectors must satisfy:
            //   R_cal * axis_steam = axis_meta
            // This is independent of position, so it works even when the point
            // cloud is flat/degenerate in one axis.
            //
            // Axis is extracted from the ROTATION MATRIX skew part (not from
            // quaternion xyz) to avoid the quaternion sign ambiguity that caused
            // flipped axes in earlier versions.
            //
            // ── Step 2: Translation via centroid ──────────────────────────────
            //   T = solved via least-squares that cancels the physical offset between
            //       tracking centres (same as OpenVR-SpaceCalibrator's CalibrateTranslation).
            //
            Debug.Log($"[SpaceCalibration] Running delta-axis Kabsch on {_samples.Count} samples.");
            Quaternion solvedRotation = DeltaAxisKabsch(_samples, out int validPairs);
            Debug.Log($"[SpaceCalibration] {validPairs} valid delta-axis pairs from {_samples.Count} samples.");

            // ── Step 1b: Constrain to yaw-only ─────────────────────────────────
            // Both Meta Quest and SteamVR align their Y axis with gravity via IMU.
            // The true calibration rotation is therefore pure yaw (rotation around Y only).
            // The Kabsch solve returns small but non-zero pitch and roll due to:
            //   • Under-constrained axes (coverage 30-45% with hand tracking)
            //   • Hand tracking noise coupling into the axis estimate
            // Any pitch/roll error directly causes Z-movement → Y drift (and X-movement → Z drift).
            // Extracting only the yaw eliminates this entirely.
            if (constrainToYawOnly)
            {
                Vector3 fwd = solvedRotation * Vector3.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 1e-6f)
                {
                    float yawDeg = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                    float pitchRollRemoved = Quaternion.Angle(solvedRotation,
                        Quaternion.AngleAxis(yawDeg, Vector3.up));
                    solvedRotation = Quaternion.AngleAxis(yawDeg, Vector3.up);
                    Debug.Log($"[SpaceCalibration] Yaw-only constraint applied: " +
                              $"yaw={yawDeg:F2}\u00b0  pitch+roll noise removed={pitchRollRemoved:F2}\u00b0");
                }
            }

            // ── Step 2b: Outlier-rejection on translation ──────────────────────
            // Hand tracking (and occasionally the Vive Tracker) can produce jumpy
            // pose samples. A single outlier sample contributes O(N) bad pair-diffs
            // to CalibrateTranslation's normal equations and can shift T by 5-15 cm.
            //
            // Strategy: compute a preliminary T, compute per-sample residual
            // |R*steam_i + T - meta_i|, reject samples beyond mean+2σ, re-solve.
            Vector3 solvedTranslation = CalibrateTranslation(_samples, solvedRotation);
            var cleanSamples = _samples;

            {
                var errs = new float[_samples.Count];
                float eSum = 0f;
                for (int i = 0; i < _samples.Count; i++)
                {
                    errs[i] = Vector3.Distance(solvedRotation * _samples[i].steamPos + solvedTranslation, _samples[i].metaPos);
                    eSum   += errs[i];
                }
                float eMean = eSum / _samples.Count;
                float eVar  = 0f;
                foreach (float e in errs) eVar += (e - eMean) * (e - eMean);
                float eSigma = Mathf.Sqrt(eVar / _samples.Count);
                float threshold = eMean + 2f * eSigma;

                cleanSamples = new List<PosePair>(_samples.Count);
                for (int i = 0; i < _samples.Count; i++)
                    if (errs[i] <= threshold) cleanSamples.Add(_samples[i]);

                int rejected = _samples.Count - cleanSamples.Count;
                if (rejected > 0)
                {
                    Debug.Log($"[SpaceCalibration] Outlier rejection: removed {rejected} samples " +
                              $"(threshold {threshold*100f:F1} cm, σ={eSigma*100f:F1} cm). " +
                              $"Re-solving translation on {cleanSamples.Count} clean samples.");
                    solvedTranslation = CalibrateTranslation(cleanSamples, solvedRotation);
                }
            }

            // ── Step 3: Residual error ──────────────────────────────────────────
            float totalErr = 0f, maxErr = 0f;
            foreach (var s in cleanSamples)
            {
                float e = Vector3.Distance(solvedRotation * s.steamPos + solvedTranslation, s.metaPos);
                totalErr += e;
                if (e > maxErr) maxErr = e;
            }
            float meanErr = totalErr / cleanSamples.Count;

            Debug.Log($"[SpaceCalibration] Solved:\n" +
                      $"  rotation    = {solvedRotation.eulerAngles:F2}\n" +
                      $"  translation = {solvedTranslation:F4} m\n" +
                      $"  mean error  = {meanErr * 100f:F2} cm   max = {maxErr * 100f:F2} cm\n" +
                      $"  samples used = {cleanSamples.Count} / {_samples.Count}");

            if (meanErr > 0.03f)
                Debug.LogWarning($"[SpaceCalibration] Mean error {meanErr * 100f:F1} cm is above 3 cm.\n" +
                                 "Residual error reflects the physical offset between tracker and hand / controller.\n" +
                                 "This is expected — 5-15 cm is normal. Position accuracy depends on rotation quality.");

            // Update in-memory sample list to the quality-filtered set used for the solve.
            // This is what gets persisted and used for future recenter compensation.
            _samples.Clear();
            _samples.AddRange(cleanSamples);

            // ── Step 4: Save + push into the tracker pipeline ────────────────
            ActiveProfile = CalibrationProfile.Create(solvedRotation, solvedTranslation, _samples.Count);
            SaveSamplesToProfile(ActiveProfile, _samples);
            ActiveProfile.Save();
            trackerReader.SetSpaceCalibration(ActiveProfile);
            CurrentState = State.Solved;
            OnCalibrationComplete?.Invoke(ActiveProfile);
        }

        // ── Sample cloud persistence ──────────────────────────────────────────────

        private void SaveSamplesToProfile(CalibrationProfile profile, List<PosePair> source)
        {
            profile.samples = new CalibrationProfile.SampleRecord[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                var s = source[i];
                profile.samples[i] = new CalibrationProfile.SampleRecord
                {
                    mPosX = s.metaPos.x,  mPosY = s.metaPos.y,  mPosZ = s.metaPos.z,
                    mRotX = s.metaRot.x,  mRotY = s.metaRot.y,  mRotZ = s.metaRot.z,  mRotW = s.metaRot.w,
                    sPosX = s.steamPos.x, sPosY = s.steamPos.y, sPosZ = s.steamPos.z,
                    sRotX = s.steamRot.x, sRotY = s.steamRot.y, sRotZ = s.steamRot.z, sRotW = s.steamRot.w
                };
            }
        }

        private void LoadSamplesFromProfile(CalibrationProfile profile)
        {
            _samples.Clear();
            if (profile.samples == null || profile.samples.Length == 0) return;
            foreach (var sr in profile.samples)
            {
                _samples.Add(new PosePair
                {
                    metaPos  = new Vector3(sr.mPosX, sr.mPosY, sr.mPosZ),
                    metaRot  = new Quaternion(sr.mRotX, sr.mRotY, sr.mRotZ, sr.mRotW),
                    steamPos = new Vector3(sr.sPosX, sr.sPosY, sr.sPosZ),
                    steamRot = new Quaternion(sr.sRotX, sr.sRotY, sr.sRotZ, sr.sRotW)
                });
            }
            Debug.Log($"[SpaceCalibration] Sample cloud restored: {_samples.Count} samples.", this);
        }

        // ── Kabsch helpers ────────────────────────────────────────────────────────

        /// <summary>
        /// Delta-axis Kabsch: finds R such that R * axis_steam ≈ axis_meta,
        /// across all valid sample-pair delta-rotations.
        ///
        /// KEY improvement over the earlier version: the rotation axis is extracted
        /// from the ROTATION MATRIX skew part — AxisFromMatrix(dRot) = (R32-R23, R13-R31, R21-R12)
        /// — which is sign-consistent and immune to the quaternion double-cover ambiguity
        /// that was flipping axes and corrupting the SVD.
        ///
        /// This matches exactly how OpenVR-SpaceCalibrator's CalibrateRotation works.
        /// </summary>
        private static Quaternion DeltaAxisKabsch(List<PosePair> samples, out int validPairs)
        {
            var H = new double[3, 3];
            validPairs = 0;

            for (int i = 0; i < samples.Count; i++)
            {
                for (int j = 0; j < i; j++)
                {
                    // Delta rotation in each space — compute as 3×3 matrix to safely extract axis
                    double[,] dMeta  = DeltaMatrix(samples[i].metaRot,  samples[j].metaRot);
                    double[,] dSteam = DeltaMatrix(samples[i].steamRot, samples[j].steamRot);

                    // Axis from skew-symmetric part: axis ~ (R32-R23, R13-R31, R21-R12)
                    // This is proportional to sin(angle)*axis — sign is always consistent.
                    double metaAngle  = AngleFromMatrix(dMeta);
                    double steamAngle = AngleFromMatrix(dSteam);

                    // Reject near-identity rotations — only accept pairs with large rotation angle.
                    // 0.7 rad = ~40°: forces solver to use only pairs with a clean axis estimate.
                    // Raise further (0.9 = ~52°) if yaw drift persists; lower (0.5 = ~29°) only if
                    // too few valid pairs are reported in the log.
                    if (metaAngle < 0.7 || steamAngle < 0.7) continue;

                    double[] metaAxis  = SkewAxis(dMeta);
                    double[] steamAxis = SkewAxis(dSteam);

                    double mn = Norm3(metaAxis), sn = Norm3(steamAxis);
                    if (mn < 0.01 || sn < 0.01) continue;

                    // Normalise
                    for (int k = 0; k < 3; k++) { metaAxis[k] /= mn; steamAxis[k] /= sn; }

                    // Weight by sin(angle) of both deltas:
                    // Large-angle pairs give a reliable axis estimate; near-threshold pairs are noisy.
                    double weight = Math.Sin(metaAngle) * Math.Sin(steamAngle);

                    // H += weight * meta * steam^T
                    for (int r = 0; r < 3; r++)
                        for (int c = 0; c < 3; c++)
                            H[r, c] += weight * metaAxis[r] * steamAxis[c];

                    validPairs++;
                }
            }

            if (validPairs < 6)
            {
                Debug.LogWarning("[SpaceCalibration] Too few valid delta pairs — need more rotation variety.");
                return Quaternion.identity;
            }

            // Kabsch: R = U * V^T from SVD(H)
            SVD3x3(H, out double[,] U, out double[,] V, out double[] sv);
            double[,] Vt  = Transpose3x3(V);
            double[,] Rot = Multiply3x3(U, Vt);
            if (Det3x3(Rot) < 0)
            {
                for (int r = 0; r < 3; r++) U[r, 2] *= -1;
                Rot = Multiply3x3(U, Vt);
            }

            // Axis coverage quality: ratio of smallest to largest singular value (0=degenerate, 1=perfect)
            double svMax = Math.Max(sv[0], Math.Max(sv[1], sv[2]));
            double svMin = Math.Min(sv[0], Math.Min(sv[1], sv[2]));
            double coverage = svMax > 1e-10 ? svMin / svMax : 0.0;
            string coverageMsg = coverage > 0.3 ? "GOOD" : coverage > 0.1 ? "FAIR — try more wrist roll" : "POOR — recalibrate with more rotation variety";
            Debug.Log($"[SpaceCalibration] Rotation axis coverage: {coverage * 100f:F0}% ({coverageMsg})  " +
                      $"sv=[{sv[0]:F2}, {sv[1]:F2}, {sv[2]:F2}]  pairs={validPairs}");
            if (coverage < 0.1)
                Debug.LogWarning("[SpaceCalibration] Rotation is poorly constrained on one axis — " +
                                 "position will drift with distance. Recalibrate and tilt/roll your hand more.");

            return MatrixToQuaternion(Rot);
        }

        // Compute rotation delta R_i * R_j^{-1} as a 3×3 double matrix
        private static double[,] DeltaMatrix(Quaternion qi, Quaternion qj)
        {
            // delta = qi * qj^{-1}
            Quaternion d = qi * Quaternion.Inverse(qj);
            return QuatToMatrix3x3(d);
        }

        private static double[,] QuatToMatrix3x3(Quaternion q)
        {
            double x = q.x, y = q.y, z = q.z, w = q.w;
            return new double[3, 3]
            {
                { 1 - 2*(y*y + z*z),     2*(x*y - z*w),     2*(x*z + y*w) },
                {     2*(x*y + z*w), 1 - 2*(x*x + z*z),     2*(y*z - x*w) },
                {     2*(x*z - y*w),     2*(y*z + x*w), 1 - 2*(x*x + y*y) }
            };
        }

        // Rotation angle from matrix: acos((trace-1)/2)
        private static double AngleFromMatrix(double[,] m) =>
            Math.Acos(Math.Max(-1.0, Math.Min(1.0, (m[0,0] + m[1,1] + m[2,2] - 1.0) * 0.5)));

        // Skew-symmetric axis (sign-consistent, proportional to sin(angle)*axis)
        private static double[] SkewAxis(double[,] m) =>
            new double[] { m[2,1] - m[1,2], m[0,2] - m[2,0], m[1,0] - m[0,1] };

        private static double Norm3(double[] v) =>
            Math.Sqrt(v[0]*v[0] + v[1]*v[1] + v[2]*v[2]);

        /// <summary>
        /// Solve for translation T given rotation R, eliminating the physical
        /// tracking-centre offset between the two devices.
        ///
        /// Model:  meta_i = R * steam_i + T + R_meta_i * c   (c = unknown fixed body offset)
        ///
        /// Rotating into meta-local frame and differencing sample pairs cancels c:
        ///   (R_meta_j^{-1} - R_meta_i^{-1}) * T
        ///       = R_meta_j^{-1}*(meta_j - R*steam_j) - R_meta_i^{-1}*(meta_i - R*steam_i)
        ///
        /// This overdetermined 3×3 linear system is solved with normal equations.
        /// Identical to OpenVR-SpaceCalibrator's CalibrateTranslation().
        /// </summary>
        private static Vector3 CalibrateTranslation(List<PosePair> samples, Quaternion R)
        {
            int N = samples.Count;

            // r_i = R_meta_i^{-1} * (meta_i - R * steam_i)  =  R_meta_i^{-1}*T + c
            var residuals = new Vector3[N];
            for (int i = 0; i < N; i++)
            {
                Vector3 corrected = samples[i].metaPos - (R * samples[i].steamPos);
                residuals[i]      = Quaternion.Inverse(samples[i].metaRot) * corrected;
            }

            // Normal equations:  (Σ dQ^T dQ) T = Σ dQ^T dc
            // where dQ = R_meta_j^{-1} - R_meta_i^{-1}  (3×3),  dc = r_j - r_i  (3-vec)
            var ATA = new double[3, 3];
            var ATb = new double[3];

            for (int i = 0; i < N; i++)
            {
                // NOTE: QuatToMatrix3x3(Quaternion.Inverse(q)) correctly gives R_meta^{-1}
                double[,] Mi = QuatToMatrix3x3(Quaternion.Inverse(samples[i].metaRot));
                for (int j = i + 1; j < N; j++)
                {
                    double[,] Mj = QuatToMatrix3x3(Quaternion.Inverse(samples[j].metaRot));
                    Vector3 dc   = residuals[j] - residuals[i];

                    // dQ[r,c] = Mj[r,c] - Mi[r,c]
                    // Accumulate ATA += dQ^T dQ,  ATb += dQ^T dc
                    for (int row = 0; row < 3; row++)
                    {
                        double dq0 = Mj[row, 0] - Mi[row, 0];
                        double dq1 = Mj[row, 1] - Mi[row, 1];
                        double dq2 = Mj[row, 2] - Mi[row, 2];
                        double dci = (double)dc[row];

                        ATA[0,0] += dq0*dq0;  ATA[0,1] += dq0*dq1;  ATA[0,2] += dq0*dq2;
                        ATA[1,0] += dq1*dq0;  ATA[1,1] += dq1*dq1;  ATA[1,2] += dq1*dq2;
                        ATA[2,0] += dq2*dq0;  ATA[2,1] += dq2*dq1;  ATA[2,2] += dq2*dq2;
                        ATb[0]   += dq0*dci;
                        ATb[1]   += dq1*dci;
                        ATb[2]   += dq2*dci;
                    }
                }
            }

            double[] T = Solve3x3(ATA, ATb);
            return new Vector3((float)T[0], (float)T[1], (float)T[2]);
        }

        /// <summary>
        /// Gradient-descent refinement of R.  Works in mean-centred coordinates so T
        /// drops out analytically, and the physical device-offset (c) has zero effect.
        ///
        ///   E(R) = Σ_i |R·s'_i - m'_i|²   (s' = steam - mean_steam, m' = meta - mean_meta)
        ///
        /// Gradient: g = 2 Σ_i  R·s'_i  ×  (R·s'_i - m'_i)
        ///            = -2 Σ_i  R·s'_i  ×  m'_i
        ///
        /// Each iteration computes the weighted-average residual rotation across all valid
        /// delta pairs, then applies a damped SO(3) correction.
        /// totalRotationDeg = total angle corrected (diagnostic).
        /// </summary>
        private static Quaternion RefineRotation(List<PosePair> samples, Quaternion R0, out float totalRotationDeg)
        {
            // IMPORTANT: uses ONLY rotation pair data.
            // The physical device-offset c cancels in delta rotations (dR_i * dR_j^{-1}),
            // exactly as it does in the initial delta-axis Kabsch step.
            // Using position data here would be WRONG because R_meta_i * c ≠ 0 (hand orientation
            // varies during calibration), causing the solver to fit offset noise instead of R.

            Quaternion R = R0;
            float damping = 0.5f;

            for (int iter = 0; iter < 60; iter++)
            {
                Vector3 avgOmega  = Vector3.zero;
                double  totalW    = 0.0;

                for (int i = 0; i < samples.Count; i++)
                {
                    for (int j = 0; j < i; j++)
                    {
                        float metaAngle  = Quaternion.Angle(samples[i].metaRot,  samples[j].metaRot)  * Mathf.Deg2Rad;
                        float steamAngle = Quaternion.Angle(samples[i].steamRot, samples[j].steamRot) * Mathf.Deg2Rad;
                        if (metaAngle < 0.4f || steamAngle < 0.4f) continue;

                        Quaternion dMeta        = samples[i].metaRot  * Quaternion.Inverse(samples[j].metaRot);
                        Quaternion dSteam       = samples[i].steamRot * Quaternion.Inverse(samples[j].steamRot);

                        // Predicted delta in meta space: R * dSteam * R^{-1}
                        Quaternion predicted    = R * dSteam * Quaternion.Inverse(R);

                        // Residual rotation: how far is the prediction from the measurement?
                        Quaternion residual     = dMeta * Quaternion.Inverse(predicted);

                        residual.ToAngleAxis(out float angleDeg, out Vector3 axis);
                        if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-10f) continue;

                        float angleRad = angleDeg * Mathf.Deg2Rad;
                        // Wrap to [−π, π] (ToAngleAxis always gives 0…360°)
                        if (angleRad > Mathf.PI) angleRad -= 2f * Mathf.PI;

                        // Same sin(angle) weighting as the Kabsch step
                        double w = Math.Sin(metaAngle) * Math.Sin(steamAngle);

                        avgOmega   += axis * (angleRad * (float)w);
                        totalW     += w;
                    }
                }

                if (totalW < 1e-10) break;
                avgOmega /= (float)totalW;

                float mag = avgOmega.magnitude;
                if (mag < 1e-6f) break;   // converged

                // Apply damped correction on SO(3)
                Quaternion correction = Quaternion.AngleAxis(mag * damping * Mathf.Rad2Deg, avgOmega / mag);
                R        = (correction * R).normalized;
                damping  *= 0.9f;
                if (damping < 0.005f) break;
            }

            totalRotationDeg = Quaternion.Angle(R0, R);
            return R;
        }

        /// <summary>Solve 3×3 linear system Ax=b via Gaussian elimination with partial pivoting.</summary>
        private static double[] Solve3x3(double[,] A, double[] b)
        {
            var M = new double[3, 4];
            for (int r = 0; r < 3; r++) { M[r,0]=A[r,0]; M[r,1]=A[r,1]; M[r,2]=A[r,2]; M[r,3]=b[r]; }
            for (int col = 0; col < 3; col++)
            {
                int pivot = col;
                for (int r = col+1; r < 3; r++)
                    if (Math.Abs(M[r,col]) > Math.Abs(M[pivot,col])) pivot = r;
                for (int c = 0; c < 4; c++) { double tmp=M[col,c]; M[col,c]=M[pivot,c]; M[pivot,c]=tmp; }
                if (Math.Abs(M[col,col]) < 1e-12) continue;
                double inv = 1.0 / M[col,col];
                for (int r = 0; r < 3; r++)
                {
                    if (r == col) continue;
                    double f = M[r,col] * inv;
                    for (int c = col; c < 4; c++) M[r,c] -= f * M[col,c];
                }
                for (int c = 0; c < 4; c++) M[col,c] *= inv;
            }
            return new double[] { M[0,3], M[1,3], M[2,3] };
        }

        // ── 3×3 SVD (via symmetric eigendecomposition of H^T·H) ──────────────────

        /// <summary>
        /// Computes the SVD of a general 3×3 matrix H = U·S·V^T.
        /// Uses: eigendecompose H^T·H → eigenvectors = V,  then U = H·V·S^-1.
        /// </summary>
        private static void SVD3x3(double[,] H, out double[,] U, out double[,] V, out double[] s)
        {
            // H^T * H  (symmetric PSD)
            var HtH = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    for (int k = 0; k < 3; k++)
                        HtH[i, j] += H[k, i] * H[k, j];

            // Eigendecompose H^T·H → columns of V are eigenvectors
            SymEigen3x3(HtH, out double[] d2, out V);
            SortEigenvectorsByDescending(d2, V);

            // Singular values s_i = sqrt(eigenvalue_i)
            s = new double[3];
            for (int i = 0; i < 3; i++) s[i] = d2[i] > 0 ? Math.Sqrt(d2[i]) : 0;

            // U columns: u_i = H · v_i / s_i
            U = new double[3, 3];
            for (int i = 0; i < 3; i++)
            {
                if (s[i] < 1e-10) { U[i, i] = 1; continue; }
                for (int r = 0; r < 3; r++)
                {
                    for (int k = 0; k < 3; k++) U[r, i] += H[r, k] * V[k, i];
                    U[r, i] /= s[i];
                }
            }
        }

        /// <summary>Jacobi eigendecomposition of a symmetric 3×3 matrix.</summary>
        private static void SymEigen3x3(double[,] a, out double[] d, out double[,] v)
        {
            var A = (double[,])a.Clone();
            v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            d = new double[3];

            for (int iter = 0; iter < 100; iter++)
            {
                // Find largest off-diagonal
                int p = 0, q = 1;
                double maxVal = Math.Abs(A[0, 1]);
                if (Math.Abs(A[0, 2]) > maxVal) { p = 0; q = 2; maxVal = Math.Abs(A[0, 2]); }
                if (Math.Abs(A[1, 2]) > maxVal) { p = 1; q = 2; maxVal = Math.Abs(A[1, 2]); }
                if (maxVal < 1e-10) break;

                double theta = 0.5 * (A[q, q] - A[p, p]) / A[p, q];
                double t     = (theta >= 0 ? 1 : -1) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                double c     = 1.0 / Math.Sqrt(t * t + 1);
                double s     = t * c;

                double app = A[p, p], aqq = A[q, q], apq = A[p, q];
                A[p, p] = app - t * apq;
                A[q, q] = aqq + t * apq;
                A[p, q] = A[q, p] = 0;

                for (int r = 0; r < 3; r++)
                {
                    if (r == p || r == q) continue;
                    double arp = A[r, p], arq = A[r, q];
                    A[r, p] = A[p, r] = c * arp - s * arq;
                    A[r, q] = A[q, r] = s * arp + c * arq;
                }
                for (int r = 0; r < 3; r++)
                {
                    double vrp = v[r, p], vrq = v[r, q];
                    v[r, p] = c * vrp - s * vrq;
                    v[r, q] = s * vrp + c * vrq;
                }
            }
            d[0] = A[0, 0]; d[1] = A[1, 1]; d[2] = A[2, 2];
        }

        private static void SortEigenvectorsByDescending(double[] d, double[,] v)
        {
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2 - i; j++)
                    if (d[j] < d[j + 1])
                    {
                        (d[j], d[j + 1]) = (d[j + 1], d[j]);
                        for (int r = 0; r < 3; r++) (v[r, j], v[r, j + 1]) = (v[r, j + 1], v[r, j]);
                    }
        }

        // ── Matrix helpers ────────────────────────────────────────────────────────

        private static double[,] Transpose3x3(double[,] m) =>
            new double[3, 3] {
                { m[0,0], m[1,0], m[2,0] },
                { m[0,1], m[1,1], m[2,1] },
                { m[0,2], m[1,2], m[2,2] }
            };

        private static double[,] Multiply3x3(double[,] a, double[,] b)
        {
            var c = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    for (int k = 0; k < 3; k++)
                        c[i, j] += a[i, k] * b[k, j];
            return c;
        }

        private static double Det3x3(double[,] m) =>
            m[0,0] * (m[1,1] * m[2,2] - m[1,2] * m[2,1])
          - m[0,1] * (m[1,0] * m[2,2] - m[1,2] * m[2,0])
          + m[0,2] * (m[1,0] * m[2,1] - m[1,1] * m[2,0]);

        private static Quaternion MatrixToQuaternion(double[,] m)
        {
            double trace = m[0,0] + m[1,1] + m[2,2];
            double qw, qx, qy, qz;
            if (trace > 0)
            {
                double s = 0.5 / Math.Sqrt(trace + 1.0);
                qw = 0.25 / s;
                qx = (m[2,1] - m[1,2]) * s;
                qy = (m[0,2] - m[2,0]) * s;
                qz = (m[1,0] - m[0,1]) * s;
            }
            else if (m[0,0] > m[1,1] && m[0,0] > m[2,2])
            {
                double s = 2.0 * Math.Sqrt(1.0 + m[0,0] - m[1,1] - m[2,2]);
                qw = (m[2,1] - m[1,2]) / s; qx = 0.25 * s;
                qy = (m[0,1] + m[1,0]) / s; qz = (m[0,2] + m[2,0]) / s;
            }
            else if (m[1,1] > m[2,2])
            {
                double s = 2.0 * Math.Sqrt(1.0 + m[1,1] - m[0,0] - m[2,2]);
                qw = (m[0,2] - m[2,0]) / s; qx = (m[0,1] + m[1,0]) / s;
                qy = 0.25 * s;              qz = (m[1,2] + m[2,1]) / s;
            }
            else
            {
                double s = 2.0 * Math.Sqrt(1.0 + m[2,2] - m[0,0] - m[1,1]);
                qw = (m[1,0] - m[0,1]) / s; qx = (m[0,2] + m[2,0]) / s;
                qy = (m[1,2] + m[2,1]) / s; qz = 0.25 * s;
            }
            return new Quaternion((float)qx, (float)qy, (float)qz, (float)qw);
        }

        private static Quaternion NormalizeQ(Quaternion q)
        {
            float mag = Mathf.Sqrt(q.x*q.x + q.y*q.y + q.z*q.z + q.w*q.w);
            if (mag < 1e-6f) return Quaternion.identity;
            return new Quaternion(q.x/mag, q.y/mag, q.z/mag, q.w/mag);
        }

        // ── Debug Gizmos ─────────────────────────────────────────────────────────

        private void OnDrawGizmosSelected()
        {
            if (!HasValidProfile || trackerReader == null) return;
            Vector3 corrected = ActiveProfile.TransformPoint(trackerReader.rawPosition);
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(corrected, 0.03f);
            if (metaDevice != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(corrected, metaDevice.position);
            }
        }
    }
}
