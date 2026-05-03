//using SurgicalScience.Logger;
//using SurgicalScience.ReflectedEvents;
//using SurgicalScience.TraumaVR.Configuration;
//using SurgicalScience.TraumaVR.Configuration.SurgicalScience.TraumaVR.Configuration;
//using SurgicalScience.TraumaVR.Events;
//using SurgicalScience.TraumaVR.Hardware.Calibration;
using UnityEngine;
using UnityEngine.Events;

namespace SurgicalScience.TraumaVR.Simulation
{
    /// <summary>
    /// Owns the start/stop lifecycle for QR calibration.
    ///
    /// Two ways to trigger the first calibration run:
    ///   1. <see cref="autoStart"/> = true, no <see cref="initilizingEvent"/> assigned
    ///      → calibration begins immediately in Awake.
    ///   2. <see cref="autoStart"/> = true, <see cref="initilizingEvent"/> assigned
    ///      → calibration is deferred until the event fires.
    /// </summary>
    public class QRCalibrationManager : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private SimpleQRCalibrator qrCalibrator;
        //[SerializeField] private QuestControllersSpaceAlignmentManager controllersCalibrator;
        //[SerializeField] private GameObjectScriptableReference trackingSpace;
        //[SerializeField] private BoolToScriptableObject isQRReady;

        //[Tooltip("Config applied to the calibrator at the start of each calibration run.")]
        [SerializeField] private QRScannerConfig calibrationConfig;

        //[Tooltip("When assigned, initialization is deferred until this event fires. " +
        //         "Leave empty to initialize immediately in Awake.")]
        //[SerializeField] private ReflectedEventTypeReference initilizingEvent;

        [Header("Behavior")]
        [Tooltip("Automatically calls StartCalibration() once the manager initializes.")]
        [SerializeField] private bool autoStart = true;

        [Header("Events")]
        public UnityEvent OnManagerAwake;
        public UnityEvent OnPhase1Started;
        public UnityEvent OnPhase1Finished;
        public UnityEvent OnStopped;
        public UnityEvent OnSceneChanged;

        //private static readonly LogCategory LogCat = LogCatalogCalibration.QRManager;

        private Pose _lastCalibratedPose;
        private bool _isCalibrating;
        private bool _isActive;
        private bool _isInitialized;
        private bool _externalCalibrationReceived;

        public bool IsQRReady => isQRReady;

        bool isQRReady;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            // Suppress the calibrator's own autoStart before its Start() fires,
            // regardless of which init path we take below.
            if (qrCalibrator != null)
                qrCalibrator.SetManagedByManager(true);

            //SimulationEvents.VRCalibratedCameraTransform.Subscribe((pos, rot) =>
            //{
            //    Log.Info(LogCat, "External calibration received — stopping QR calibration.", this);
            //    _externalCalibrationReceived = true;
            //    StopCalibration();
            //});

            //if (initilizingEvent != null)
            //    initilizingEvent.Subscribe(() => { if (!_isInitialized) Init(); });
            //else
                Init();
        }

        private void OnDestroy()
        {
            if (qrCalibrator == null) return;
            qrCalibrator.OnFinalized.RemoveListener(OnCalibratorFinalized);
            qrCalibrator.SetManagedByManager(false);
        }

        private void Update()
        {
            // Right-index-trigger aborts QR calibration and falls back to controller calibration.
            //if (controllersCalibrator != null && _isCalibrating && OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger))
            //{
            //    StopCalibration();

            //    if (trackingSpace != null)
            //        trackingSpace.RuntimeObject.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            //    controllersCalibrator.enabled = true;
            //}
        }

        // ── Initialization ────────────────────────────────────────────────────

        private void Init()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            //RuntimeEvents.LoadingScreenOff.Subscribe(() =>
            //{
            //    if (_isActive) OnSceneChanged?.Invoke();
            //});

            if (qrCalibrator == null)
                qrCalibrator = GetComponent<SimpleQRCalibrator>();

            OnManagerAwake?.Invoke();

            if (qrCalibrator == null)
            {
                Debug.LogWarning("No SimpleQRCalibrator found — calibration unavailable.");
                //Log.Warn(LogCat, "No SimpleQRCalibrator found — calibration unavailable.", this);
                return;
            }

            // Remove before add — prevents duplicate listeners if Init() is ever called twice.
            qrCalibrator.OnFinalized.RemoveListener(OnCalibratorFinalized);
            qrCalibrator.OnFinalized.AddListener(OnCalibratorFinalized);
            qrCalibrator.SetManagedByManager(true);

            Debug.Log($"Calibration manager initialized. AutoStart={autoStart}");
            //Log.Info(LogCat, $"Calibration manager initialized. AutoStart={autoStart}", this);

            if (autoStart && !_externalCalibrationReceived)
                StartCalibration();
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void StartCalibration()
        {
            _isActive = true;
            _externalCalibrationReceived = false;

            if (qrCalibrator == null || _isCalibrating) return;

            if (isQRReady == true) isQRReady = false;

            qrCalibrator.SetManagedByManager(true);

            if (calibrationConfig != null)
                qrCalibrator.ApplyConfig(calibrationConfig);

            Debug.Log("QR calibration started.");
            //Log.Info(LogCat, "QR calibration started.", this);
            OnPhase1Started?.Invoke();
            _isCalibrating = true;
            qrCalibrator.Begin();
        }

        public void StopCalibration()
        {
            _isActive = false;

            if (qrCalibrator == null || !_isCalibrating) return;

            _isCalibrating = false;
            qrCalibrator.SetActive(false);
            OnStopped?.Invoke();

            Debug.Log("QR calibration stopped.");
            //Log.Info(LogCat, "QR calibration stopped.", this);
        }

        public void RestartCalibration()
        {
            Debug.Log("QR calibration restarting.");
            //Log.Info(LogCat, "QR calibration restarting.", this);

            if (qrCalibrator == null) return;

            _isActive = true;
            _externalCalibrationReceived = false;
            _isCalibrating = true;

            if (isQRReady == true) isQRReady = false;

            qrCalibrator.SetManagedByManager(true);

            if (calibrationConfig != null)
                qrCalibrator.ApplyConfig(calibrationConfig);

            OnPhase1Started?.Invoke();
            qrCalibrator.RestartCalibration();
        }

        public Pose GetLastCalibratedPose() => _lastCalibratedPose;

        [ContextMenu("Start Calibration")]
        private void Context_StartCalibration() => StartCalibration();

        [ContextMenu("Restart Calibration")]
        private void Context_RestartCalibration() => RestartCalibration();

        // ── Callbacks ─────────────────────────────────────────────────────────

        private void OnCalibratorFinalized(Transform t)
        {
            if (!_isCalibrating || t == null) return;

            _isCalibrating = false;
            _lastCalibratedPose = new Pose(t.position, t.rotation);

            Debug.Log($"QR finalized. pos={t.position} rot={t.rotation.eulerAngles}");
            //Log.Info(LogCat, $"QR finalized. pos={t.position} rot={t.rotation.eulerAngles}", this);

            OnPhase1Finished?.Invoke();

            if (isQRReady == false) isQRReady = true;
        }
    }
}