using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace ViveTrackerSolution.SpaceCalibration
{
    /// <summary>
    /// Optional UI binding for <see cref="SpaceCalibratorManager"/>.
    ///
    /// Wire up the buttons and text fields in the Inspector.
    /// Every field is optional — the script degrades gracefully if any UI element
    /// is left unassigned.
    ///
    /// Typical Canvas setup:
    ///   [Button] Start / Cancel
    ///   [Button] Reload from file
    ///   [Slider or ProgressBar image fill]
    ///   [TMP_Text] Status line
    ///   [TMP_Text] Profile details
    /// </summary>
    public class SpaceCalibrationUI : MonoBehaviour
    {
        // ── Inspector ───────────────────────────────────────────────────────────

        [Header("Manager")]
        public SpaceCalibratorManager calibratorManager;

        [Header("Buttons")]
        [Tooltip("Button that starts calibration (or cancels if one is running).")]
        public Button startCancelButton;

        [Tooltip("Button that reloads the saved profile from disk without re-calibrating.")]
        public Button reloadButton;

        [Header("Labels")]
        [Tooltip("Text label on the start/cancel button.")]
        public TMP_Text startCancelButtonLabel;

        [Tooltip("One-line status message.")]
        public TMP_Text statusText;

        [Tooltip("Multi-line details block (rotation, translation, sample count, timestamp).")]
        public TMP_Text profileDetailsText;

        [Header("Progress")]
        [Tooltip("Slider that fills from 0 → 1 during collection.  Optional.")]
        public Slider progressSlider;

        [Tooltip("Text showing '42 / 100' during collection.  Optional.")]
        public TMP_Text progressLabel;

        // ── Colours ─────────────────────────────────────────────────────────────
        [Header("Status Colors")]
        public Color colorIdle       = new Color(0.7f, 0.7f, 0.7f);
        public Color colorCollecting = new Color(1.0f, 0.8f, 0.1f);
        public Color colorSolved     = new Color(0.2f, 0.9f, 0.3f);
        public Color colorError      = new Color(1.0f, 0.3f, 0.3f);

        // ── Unity ───────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (calibratorManager == null)
            {
                Debug.LogError("[SpaceCalibrationUI] calibratorManager is not assigned.");
                enabled = false;
                return;
            }

            // Wire buttons
            if (startCancelButton != null)
                startCancelButton.onClick.AddListener(OnStartCancelClicked);
            if (reloadButton != null)
                reloadButton.onClick.AddListener(OnReloadClicked);

            // Wire manager events
            calibratorManager.OnProgress            += HandleProgress;
            calibratorManager.OnCalibrationComplete += HandleComplete;
            calibratorManager.OnCalibrationCancelled += HandleCancelled;
        }

        private void OnDestroy()
        {
            if (calibratorManager == null) return;
            calibratorManager.OnProgress            -= HandleProgress;
            calibratorManager.OnCalibrationComplete -= HandleComplete;
            calibratorManager.OnCalibrationCancelled -= HandleCancelled;
        }

        private void Start()
        {
            RefreshUI();
        }

        // ── Button handlers ──────────────────────────────────────────────────────

        private void OnStartCancelClicked()
        {
            if (calibratorManager.CurrentState == SpaceCalibratorManager.State.Collecting)
                calibratorManager.CancelCalibration();
            else
                calibratorManager.StartCalibration();

            RefreshUI();
        }

        private void OnReloadClicked()
        {
            bool ok = calibratorManager.TryLoadProfile();
            SetStatus(ok ? "Profile reloaded from disk." : "No saved profile found.", ok ? colorSolved : colorError);
            RefreshUI();
        }

        // ── Manager event handlers ────────────────────────────────────────────────

        private void HandleProgress(int current, int required)
        {
            SetStatus($"Collecting samples — move the devices around slowly...", colorCollecting);

            if (progressSlider != null)
                progressSlider.value = (float)current / required;

            if (progressLabel != null)
                progressLabel.text = $"{current} / {required}";
        }

        private void HandleComplete(CalibrationProfile profile)
        {
            SetStatus("Calibration complete! Profile saved.", colorSolved);
            RefreshUI();
        }

        private void HandleCancelled()
        {
            SetStatus("Calibration cancelled.", colorIdle);
            RefreshUI();
        }

        // ── UI refresh ────────────────────────────────────────────────────────────

        private void RefreshUI()
        {
            bool isCollecting = calibratorManager.CurrentState == SpaceCalibratorManager.State.Collecting;

            // Button label
            if (startCancelButtonLabel != null)
                startCancelButtonLabel.text = isCollecting ? "Cancel Calibration" : "Start Calibration";

            // Progress
            if (progressSlider != null)
                progressSlider.value = isCollecting ? (float)calibratorManager.SamplesCollected / calibratorManager.requiredSamples : 0f;

            if (progressLabel != null)
                progressLabel.text = isCollecting ? $"{calibratorManager.SamplesCollected} / {calibratorManager.requiredSamples}" : "";

            // Status line
            switch (calibratorManager.CurrentState)
            {
                case SpaceCalibratorManager.State.Idle:
                    SetStatus(calibratorManager.HasValidProfile ? "Idle — profile loaded." : "Idle — no profile.", colorIdle);
                    break;
                case SpaceCalibratorManager.State.Collecting:
                    SetStatus("Collecting samples — move the devices around slowly...", colorCollecting);
                    break;
                case SpaceCalibratorManager.State.Solved:
                    SetStatus("Calibration active.", colorSolved);
                    break;
            }

            // Profile details
            if (profileDetailsText != null)
            {
                if (calibratorManager.HasValidProfile)
                {
                    var p = calibratorManager.ActiveProfile;
                    profileDetailsText.text =
                        $"Rotation : {p.Rotation.eulerAngles:F2}\n" +
                        $"Translation : {p.Translation:F4} m\n" +
                        $"Samples : {p.sampleCount}\n" +
                        $"Recorded : {p.timestamp}";
                }
                else
                {
                    profileDetailsText.text = "(no calibration profile)";
                }
            }
        }

        private void SetStatus(string message, Color color)
        {
            if (statusText == null) return;
            statusText.text  = message;
            statusText.color = color;
        }
    }
}
