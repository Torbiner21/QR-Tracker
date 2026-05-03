using System;
using System.IO;
using UnityEngine;

namespace ViveTrackerSolution.SpaceCalibration
{
    /// <summary>
    /// Stores the solved rigid-body transform that maps SteamVR world space → Meta (OVR) world space.
    ///
    ///   p_meta = Rotation * p_steamvr + Translation
    ///
    /// Saved as JSON next to Application.persistentDataPath so it persists across
    /// play-mode sessions and builds.  As long as the Lighthouse base stations and
    /// the Quest guardian are not moved, one calibration is permanently valid.
    /// </summary>
    [Serializable]
    public class CalibrationProfile
    {
        // ── Solved transform ────────────────────────────────────────────────────
        public float rotX, rotY, rotZ, rotW;   // quaternion (Meta ← SteamVR)
        public float transX, transY, transZ;   // translation in metres

        /// <summary>Number of samples that were averaged to produce this result.</summary>
        public int sampleCount;

        /// <summary>UTC timestamp when this calibration was recorded.</summary>
        public string timestamp;

        /// <summary>
        /// Serialized sample cloud — SteamVR positions are Lighthouse-absolute (stable across
        /// sessions); Meta positions are in the guardian space at calibration time and are
        /// automatically shifted by SpaceCalibratorManager whenever a recenter is detected.
        /// Stored so Kabsch can be re-run instantly after a guardian shift without re-gathering.
        /// </summary>
        public SampleRecord[] samples;

        /// <summary>One pose-pair sample, flattened to floats for JsonUtility compatibility.</summary>
        [Serializable]
        public class SampleRecord
        {
            public float mPosX, mPosY, mPosZ;           // Meta position
            public float mRotX, mRotY, mRotZ, mRotW;    // Meta rotation (quaternion)
            public float sPosX, sPosY, sPosZ;           // SteamVR position
            public float sRotX, sRotY, sRotZ, sRotW;    // SteamVR rotation (quaternion)
        }

        // ── Convenience accessors ───────────────────────────────────────────────
        [NonSerialized] private Quaternion _rotation;
        [NonSerialized] private Vector3    _translation;
        [NonSerialized] private bool       _cached;

        public Quaternion Rotation
        {
            get
            {
                if (!_cached) Cache();
                return _rotation;
            }
        }

        public Vector3 Translation
        {
            get
            {
                if (!_cached) Cache();
                return _translation;
            }
        }

        private void Cache()
        {
            _rotation    = new Quaternion(rotX, rotY, rotZ, rotW);
            _translation = new Vector3(transX, transY, transZ);
            _cached      = true;
        }

        /// <summary>Apply the calibration: converts a SteamVR world-space position to Meta world-space.</summary>
        public Vector3 TransformPoint(Vector3 steamVrPos)
        {
            return Rotation * steamVrPos + Translation;
        }

        /// <summary>Apply only the rotational part (for direction vectors / orientations).</summary>
        public Vector3 TransformDirection(Vector3 steamVrDir)
        {
            return Rotation * steamVrDir;
        }

        /// <summary>Convert a SteamVR rotation to Meta rotation.</summary>
        public Quaternion TransformRotation(Quaternion steamVrRot)
        {
            return Rotation * steamVrRot;
        }

        // ── Factory ─────────────────────────────────────────────────────────────
        public static CalibrationProfile Create(Quaternion rotation, Vector3 translation, int samples)
        {
            return new CalibrationProfile
            {
                rotX        = rotation.x,
                rotY        = rotation.y,
                rotZ        = rotation.z,
                rotW        = rotation.w,
                transX      = translation.x,
                transY      = translation.y,
                transZ      = translation.z,
                sampleCount = samples,
                timestamp   = DateTime.UtcNow.ToString("o")
            };
        }

        // ── Persistence ─────────────────────────────────────────────────────────
        private const string FileName = "SpaceCalibration.json";

        public static string FilePath =>
            // TODO: switch to TraumaVRData.PersistentDataPath once that project is merged in
            Path.Combine(Application.persistentDataPath, FileName);

        public void Save()
        {
            try
            {
                File.WriteAllText(FilePath, JsonUtility.ToJson(this, prettyPrint: true));
                Debug.Log($"[SpaceCalibration] Profile saved → {FilePath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SpaceCalibration] Failed to save profile: {ex.Message}");
            }
        }

        /// <summary>Returns null if no file exists or it cannot be parsed.</summary>
        public static CalibrationProfile Load()
        {
            if (!File.Exists(FilePath))
            {
                Debug.Log($"[SpaceCalibration] No saved profile found at {FilePath}");
                return null;
            }

            try
            {
                var json    = File.ReadAllText(FilePath);
                var profile = JsonUtility.FromJson<CalibrationProfile>(json);
                Debug.Log($"[SpaceCalibration] Profile loaded — {profile.sampleCount} samples, recorded {profile.timestamp}");
                return profile;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SpaceCalibration] Failed to load profile: {ex.Message}");
                return null;
            }
        }

        public override string ToString() =>
            $"rot={Rotation.eulerAngles}  trans={Translation}  samples={sampleCount}  @{timestamp}";
    }
}
