using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Meta.XR.MRUtilityKit;
//using SurgicalScience.Logger;
//using SurgicalScience.TraumaVR.Configuration;
//using SurgicalScience.TraumaVR.Configuration.SurgicalScience.TraumaVR.Configuration;
using UnityEngine;
using UnityEngine.Events;

public class SimpleQRCalibrator : MonoBehaviour/*, IConfigurable*/
{
    [Header("References")]
    public MRUK mruk;
    public QRScannerConfig defaultConfig;
    public Transform targetToPlace;
    [SerializeField] private float targetProgressReference;

    [Header("Core Behavior")]
    public bool autoStart = true;
    public bool stopAfterFinalize = true;
    public string payloadContainsFilter = "";

    [Header("Accuracy / Stability Thresholds")]
    [Min(1)] public int iterationAmount = 10;
    public float burstPosRmsThresholdMm = 1f;
    public float burstAngleRmsThresholdDeg = 5f;

    [Header("Sampling / Timing")]
    [Range(3, 20)] public int samplesPerBurst = 7;
    public float interScanIntervalSeconds = 1.5f;
    public float reacquireCooldownSeconds = 0.4f;
    [Min(0f)] public float trackingLostGraceSeconds = 0.35f;

    [Header("View Gating (Camera <-> QR)")]
    [Range(0f, 90f)] public float maxViewAngleFromNormalDeg = 15f;
    public bool requireViewpointDiversity = false;
    public float minAngleBetweenBurstsDeg = 10f;
    public float minDistanceDeltaM = 0.05f;

    [Header("Robust Final Averaging")]
    [Min(0)] public int finalWindow = 20;
    public float inlierMaxMm = 3f;
    public float inlierMaxDeg = 3f;
    [Range(1, 10)] public int irlsIterations = 4;
    public float huberPosK_mm = 3f;
    public float huberRotK_deg = 3f;

    [Header("Safety")]
    public bool allowButtonActions = false;

    [Header("Utility / Reset")]
    public KeyCode restartKey = KeyCode.Y;
    public bool resetTargetOnRestart = false;
    public bool invokeAdditionalEvents = false;

    [Header("Events")]
    public UnityEvent<int> OnValidScan;
    public UnityEvent<Transform> OnFinalized;
    public UnityEvent<Transform> OnFinalizedAdditional;
    public UnityEvent<bool> OnSystemToggled;
    public UnityEvent<Vector3, Quaternion> OnQrScanFinished;

    [Header("Runtime Toggle / Debug")]
    [SerializeField] private KeyCode toggleKey = KeyCode.F8;
    [SerializeField] private Transform cameraOverride;

    public float Progress => _running ? targetProgressReference : 0f;

    //private static readonly LogCategory LogCat = LogCatalogCalibration.QRScanner;
    private const float PostResetSeedDelaySeconds = 0.15f;

    class Series
    {
        public string Key;
        public Guid Uuid;
        public string Payload;
        public Transform Anchor;
        public MRUKTrackable Trackable;
        public float LastTrackedTime;
        public readonly List<Pose> Poses = new();
        public readonly List<Vector3> ViewDirs = new();
        public readonly List<float> CamDists = new();
        public int AcceptedBursts;
        public bool Finalized;
        public float NextEligibleTime;
    }

    Series _series;
    bool _running;
    bool _subscribed;
    bool _managedByManager;

    Pose _originalTargetPose;
    bool _haveOriginalTargetPose;
    float _nextSeedAttemptTime;

    Coroutine _scanLoopCo;
    Coroutine _startupCo;

    bool _hasPendingQrEnable;
    bool _pendingQrEnableValue;
    bool _pendingNeedsResetCycle;

    Transform _cam;
    readonly List<MRUKTrackable> _qrList = new();
    readonly List<Pose> _burst = new(32);

    public bool IsActive => _running;

    public void SetManagedByManager(bool managed) => _managedByManager = managed;

    public void SetActive(bool active)
    {
        if (active) Begin();
        else
        {
            StopScanning();
            ClearSeriesForReseed(false);
            EnsureQrTrackingEnabled(false);
        }

        OnSystemToggled?.Invoke(active);
    }

    public void Begin()
    {
        //targetProgressReference.ResetValue();
        targetProgressReference = 0f;

        StopInternalCoroutines();

        _running = false;
        _hasPendingQrEnable = false;
        _pendingNeedsResetCycle = true;
        _nextSeedAttemptTime = 0f;

        ClearSeriesForReseed(false);
        _qrList.Clear();

        _startupCo = StartCoroutine(RobustEnableTracking(() =>
        {
            _startupCo = StartCoroutine(WaitAndSubscribe());
        }));
    }

    public void StopScanning()
    {
        _running = false;
        StopInternalCoroutines();
    }

    public void ForceFinalize(Transform target)
    {
        StopScanning();
        EnsureQrTrackingEnabled(false);
        OnFinalized?.Invoke(target);
        if (invokeAdditionalEvents) OnFinalizedAdditional?.Invoke(target);
        targetProgressReference = 1f;
        //targetProgressReference.Value = 1f;
        ClearSeriesForReseed(false);
        _qrList.Clear();
    }

    public void RestartCalibration() => StartCoroutine(RestartRoutine());

    void Awake()
    {
        if (defaultConfig != null) ApplyConfig(defaultConfig);

        if (targetToPlace)
        {
            _originalTargetPose = new Pose(targetToPlace.position, targetToPlace.rotation);
            _haveOriginalTargetPose = true;
        }

        _cam = cameraOverride ? cameraOverride : (Camera.main ? Camera.main.transform : null);

        if (OVRManager.instance != null)
        {
            OVRManager.instance.isInsightPassthroughEnabled = true;
            Debug.Log("Insight passthrough enabled to pre-warm QR tracking.");
            //Log.Info(LogCat, "Insight passthrough enabled to pre-warm QR tracking.", this);
        }
    }

    void OnDisable()
    {
        StopScanning();
        ClearSeriesForReseed(false);

        if (_subscribed && mruk != null && mruk.SceneSettings != null)
        {
            mruk.SceneSettings.TrackableAdded.RemoveListener(OnTrackableAdded);
            mruk.SceneSettings.TrackableRemoved.RemoveListener(OnTrackableRemoved);
            _subscribed = false;
            Debug.Log("Unsubscribed from MRUK trackable events.");
            //Log.Info(LogCat, "Unsubscribed from MRUK trackable events.", this);
        }
    }

    void Start()
    {
        _cam = cameraOverride ? cameraOverride : (Camera.main ? Camera.main.transform : null);
        if (autoStart && !_managedByManager) Begin();
    }

    void Update()
    {
        if (!_cam) _cam = cameraOverride ? cameraOverride : (Camera.main ? Camera.main.transform : null);
        if (!allowButtonActions) return;

        if (Input.GetKeyDown(restartKey)) RestartCalibration();
        if (Input.GetKeyDown(toggleKey)) SetActive(!IsActive);
    }

    void StopInternalCoroutines()
    {
        if (_scanLoopCo != null) { StopCoroutine(_scanLoopCo); _scanLoopCo = null; }
        if (_startupCo != null) { StopCoroutine(_startupCo); _startupCo = null; }
    }

    IEnumerator RobustEnableTracking(Action onReady)
    {
        float waited = 0f;
        const float ovrTimeout = 5f;

        while (!IsOvrTrackingReady() && waited < ovrTimeout)
        {
            yield return new WaitForSecondsRealtime(0.1f);
            waited += 0.1f;
        }

        if (waited >= ovrTimeout)
            Debug.LogWarning("OVR tracking not ready after timeout; proceeding anyway.");
            //Log.Warn(LogCat, "OVR tracking not ready after timeout; proceeding anyway.", this);

        onReady?.Invoke();
    }

    IEnumerator WaitAndSubscribe()
    {
        while (mruk == null || mruk.SceneSettings == null)
            yield return null;

        if (_pendingNeedsResetCycle)
        {
            _pendingNeedsResetCycle = false;
            _hasPendingQrEnable = false;

            var cfg = mruk.SceneSettings.TrackerConfiguration;
            cfg.QRCodeTrackingEnabled = false;
            mruk.SceneSettings.TrackerConfiguration = cfg;
            Debug.Log("Reset cycle: QR tracking disabled.");
            //Log.Info(LogCat, "Reset cycle: QR tracking disabled.", this);

            yield return null;
            yield return null;

            cfg = mruk.SceneSettings.TrackerConfiguration;
            cfg.QRCodeTrackingEnabled = true;
            mruk.SceneSettings.TrackerConfiguration = cfg;
            Debug.Log("Reset cycle: QR tracking re-enabled.");
            //Log.Info(LogCat, "Reset cycle: QR tracking re-enabled.", this);

            yield return null;
            yield return null;
        }
        else if (_hasPendingQrEnable)
        {
            EnsureQrTrackingEnabled(_pendingQrEnableValue);
        }

        if (!_subscribed)
        {
            mruk.SceneSettings.TrackableAdded.AddListener(OnTrackableAdded);
            mruk.SceneSettings.TrackableRemoved.AddListener(OnTrackableRemoved);
            _subscribed = true;
            Debug.Log("Subscribed to MRUK trackable events.");
            //Log.Info(LogCat, "Subscribed to MRUK trackable events.", this);
        }

        _running = true;
        _nextSeedAttemptTime = Time.time + PostResetSeedDelaySeconds;

        yield return new WaitForSeconds(PostResetSeedDelaySeconds);

        SeedFromAlreadyTracked();
        TrySeedFromExistingQrs();

        if (_scanLoopCo != null) { StopCoroutine(_scanLoopCo); _scanLoopCo = null; }
        _scanLoopCo = StartCoroutine(ScanLoop());
        _startupCo = null;
    }

    IEnumerator RestartRoutine()
    {
        targetProgressReference = 0f;
        //targetProgressReference.ResetValue();
        StopScanning();
        EnsureQrTrackingEnabled(false);

        if (resetTargetOnRestart && targetToPlace && _haveOriginalTargetPose)
            targetToPlace.SetPositionAndRotation(_originalTargetPose.position, _originalTargetPose.rotation);

        ClearSeriesForReseed(false);
        _qrList.Clear();
        _nextSeedAttemptTime = 0f;
        _hasPendingQrEnable = false;
        _pendingNeedsResetCycle = true;

        yield return null;
        yield return null;

        _startupCo = StartCoroutine(RobustEnableTracking(() =>
        {
            _startupCo = StartCoroutine(WaitAndSubscribe());
        }));
    }

    IEnumerator ScanLoop()
    {
        while (_running)
        {
            if (_series != null && !IsSeriesAliveWithGrace(_series))
            {
                Debug.Log("Active QR is no longer tracked/usable. Clearing series.");
                //Log.Info(LogCat, "Active QR is no longer tracked/usable. Clearing series.", this);
                ClearSeriesForReseed();
                yield return null;
                continue;
            }

            if (_series == null)
            {
                if (Time.time >= _nextSeedAttemptTime)
                {
                    TrySeedFromExistingQrs();
                    if (_series == null) SeedFromAlreadyTracked();
                    _nextSeedAttemptTime = Time.time + 0.25f;
                }

                yield return null;
                continue;
            }

            if (!_series.Finalized && IsSeriesTrackedNow(_series) && _series.Anchor != null)
            {
                if (Time.time >= _series.NextEligibleTime &&
                    (iterationAmount <= 1 || PassesViewGate(_series.Anchor)))
                {
                    int burstFrames = (iterationAmount <= 1) ? Mathf.Clamp(samplesPerBurst, 3, 4) : samplesPerBurst;

                    _burst.Clear();
                    bool lostTrackingDuringBurst = false;

                    for (int i = 0; i < burstFrames; i++)
                    {
                        if (!IsSeriesTrackedNow(_series) || _series.Anchor == null)
                        {
                            lostTrackingDuringBurst = true;
                            break;
                        }

                        _burst.Add(new Pose(_series.Anchor.position, _series.Anchor.rotation));
                        yield return null;
                    }

                    if (lostTrackingDuringBurst)
                    {
                        if (!IsSeriesAliveWithGrace(_series))
                        {
                            Debug.Log("Lost QR tracking during burst. Clearing series.");
                            //Log.Info(LogCat, "Lost QR tracking during burst. Clearing series.", this);
                            ClearSeriesForReseed();
                        }

                        continue;
                    }

                    if (_burst.Count == 0)
                    {
                        yield return null;
                        continue;
                    }

                    var central = ComputeBurstCentralPose(_burst, out float rmsPosM, out float rmsAngDeg);
                    float rmsPosMm = rmsPosM * 1000f;

                    bool stable = (iterationAmount <= 1) || (
                        rmsPosMm <= Mathf.Max(0.001f, burstPosRmsThresholdMm) &&
                        rmsAngDeg <= Mathf.Max(0.001f, burstAngleRmsThresholdDeg));

                    if (stable)
                    {
                        bool diverseOK = true;

                        if (requireViewpointDiversity && iterationAmount > 1 && _cam)
                        {
                            var viewDir = (_series.Anchor.position - _cam.position).normalized;
                            float camDist = Vector3.Distance(_cam.position, _series.Anchor.position);

                            diverseOK = IsDiverse(viewDir, camDist, _series.ViewDirs, _series.CamDists,
                                            minAngleBetweenBurstsDeg, minDistanceDeltaM)
                                        || _series.AcceptedBursts == 0;

                            if (diverseOK)
                            {
                                _series.ViewDirs.Add(viewDir);
                                _series.CamDists.Add(camDist);
                            }
                        }

                        if (diverseOK)
                        {
                            _series.Poses.Add(central);
                            _series.AcceptedBursts++;
                            _series.LastTrackedTime = Time.time;

                            targetProgressReference = _series.AcceptedBursts / (float)iterationAmount;
                            //targetProgressReference.Value = _series.AcceptedBursts / (float)iterationAmount;
                            _series.NextEligibleTime = Time.time + Mathf.Max(0f, interScanIntervalSeconds);

                            if (_series.AcceptedBursts >= iterationAmount)
                            {
                                FinalizeSeries(_series);
                                if (!stopAfterFinalize)
                                {
                                    ClearSeriesForReseed(false);
                                    _nextSeedAttemptTime = 0f;
                                }
                            }
                            else
                            {
                                OnValidScan?.Invoke(_series.AcceptedBursts);
                                Debug.Log("ValidScan");
                            }
                        }
                    }
                }
            }

            yield return null;
        }
    }

    bool IsOvrTrackingReady()
    {
        if (OVRManager.instance == null || !OVRManager.isHmdPresent) return false;
        try { return OVRPlugin.initialized; }
        catch { return false; }
    }

    void EnsureQrTrackingEnabled(bool enabled)
    {
        if (mruk == null || mruk.SceneSettings == null)
        {
            _hasPendingQrEnable = true;
            _pendingQrEnableValue = enabled;
            return;
        }

        try
        {
            var cfg = mruk.SceneSettings.TrackerConfiguration;
            cfg.QRCodeTrackingEnabled = enabled;
            mruk.SceneSettings.TrackerConfiguration = cfg;
            _hasPendingQrEnable = false;
            Debug.Log($"QR tracking set to {enabled}");
            //Log.Info(LogCat, $"QR tracking set to {enabled}", this);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Failed to set QR tracking: {e.Message}");
            //Log.Warn(LogCat, $"Failed to set QR tracking: {e.Message}", this);
            _hasPendingQrEnable = true;
            _pendingQrEnableValue = enabled;
        }
    }

    void OnTrackableAdded(MRUKTrackable trk)
    {
        if (trk == null || trk.TrackableType != OVRAnchor.TrackableType.QRCode) return;
        if (!MatchesPayloadFilter(trk)) return;

        if (!_qrList.Contains(trk)) _qrList.Add(trk);

        Debug.Log($"QR trackable added: payload=\"{trk.MarkerPayloadString ?? ""}\", uuid={trk.Anchor.Uuid}, tracked={trk.IsTracked}");
        //Log.Info(LogCat, $"QR trackable added: payload=\"{trk.MarkerPayloadString ?? ""}\", uuid={trk.Anchor.Uuid}, tracked={trk.IsTracked}", this);

        if (!_running) return;
        if (!IsTrackableUsable(trk)) return;

        var payload = trk.MarkerPayloadString ?? "";

        if (_series != null && _series.Payload == payload && _series.Uuid != trk.Anchor.Uuid)
        {
            _series.Key = MakeKey(trk.Anchor.Uuid, payload);
            _series.Uuid = trk.Anchor.Uuid;
            _series.Payload = payload;
            _series.Anchor = trk.transform;
            _series.Trackable = trk;
            _series.LastTrackedTime = Time.time;
            _series.NextEligibleTime = Time.time + Mathf.Max(0f, reacquireCooldownSeconds);
            return;
        }

        if (_series != null && _series.Finalized)
            ClearSeriesForReseed(false);

        if (_series == null)
        {
            _series = new Series
            {
                Key = MakeKey(trk.Anchor.Uuid, payload),
                Uuid = trk.Anchor.Uuid,
                Payload = payload,
                Anchor = trk.transform,
                Trackable = trk,
                LastTrackedTime = Time.time,
                NextEligibleTime = (iterationAmount <= 1) ? Time.time : Time.time + Mathf.Max(0f, reacquireCooldownSeconds)
            };
        }
        else if (_series.Uuid == trk.Anchor.Uuid)
        {
            _series.Anchor = trk.transform;
            _series.Trackable = trk;
            _series.LastTrackedTime = Time.time;
        }
    }

    void OnTrackableRemoved(MRUKTrackable trk)
    {
        if (trk == null || trk.TrackableType != OVRAnchor.TrackableType.QRCode) return;
        _qrList.Remove(trk);
    }

    void TrySeedFromExistingQrs()
    {
        if (_series != null) return;

        PruneQrList();
        if (_qrList.Count == 0) return;

        MRUKTrackable best = null;
        float bestAng = float.MaxValue;

        foreach (var t in _qrList)
        {
            if (!IsTrackableUsable(t)) continue;

            float ang = _cam
                ? Vector3.Angle(_cam.forward, (t.transform.position - _cam.position).normalized)
                : 0f;

            if (ang < bestAng)
            {
                bestAng = ang;
                best = t;
            }
        }

        if (best == null) return;

        var payload = best.MarkerPayloadString ?? "";
        _series = new Series
        {
            Key = MakeKey(best.Anchor.Uuid, payload),
            Uuid = best.Anchor.Uuid,
            Payload = payload,
            Anchor = best.transform,
            Trackable = best,
            LastTrackedTime = Time.time,
            NextEligibleTime = (iterationAmount <= 1)
                ? Time.time
                : Time.time + Mathf.Max(0f, reacquireCooldownSeconds)
        };

        Debug.Log($"Seeded series from existing tracked QR: {_series.Key}");
        //Log.Info(LogCat, $"Seeded series from existing tracked QR: {_series.Key}", this);
    }

    void SeedFromAlreadyTracked()
    {
        foreach (var t in FindObjectsOfType<MRUKTrackable>())
        {
            if (t == null || t.TrackableType != OVRAnchor.TrackableType.QRCode) continue;
            if (_qrList.Contains(t)) continue;
            if (!IsTrackableUsable(t)) continue;
            OnTrackableAdded(t);
        }
    }

    void FinalizeSeries(Series s)
    {
        if (s == null)
        {
            Debug.LogWarning("FinalizeSeries called with null series.");
            //Log.Warn(LogCat, "FinalizeSeries called with null series.", this);
            return;
        }

        if (s.Finalized) return;

        var poses = (finalWindow > 0 && s.Poses.Count > finalWindow)
            ? s.Poses.GetRange(s.Poses.Count - finalWindow, finalWindow)
            : s.Poses;

        if (poses == null || poses.Count == 0)
        {
            Debug.LogWarning("FinalizeSeries: no poses collected.");
            //Log.Warn(LogCat, "FinalizeSeries: no poses collected.", this);
            return;
        }

        var seed = ComputeSetMedianPose(poses);
        var inliers = new List<Pose>(poses.Count);

        foreach (var p in poses)
        {
            if (Vector3.Distance(p.position, seed.position) * 1000f <= Mathf.Max(0.0001f, inlierMaxMm) &&
                AngleBetween(seed.rotation, p.rotation) <= Mathf.Max(0.0001f, inlierMaxDeg))
            {
                inliers.Add(p);
            }
        }

        if (inliers.Count < Mathf.Max(3, poses.Count / 3))
            inliers = poses;

        Pose finalPose = RobustIrlsPose(inliers, irlsIterations, huberPosK_mm, huberRotK_deg);

        if (targetToPlace)
        {
            targetToPlace.gameObject.SetActive(true);   
            targetToPlace.SetPositionAndRotation(finalPose.position, finalPose.rotation);
            OnFinalized?.Invoke(targetToPlace);
            if (invokeAdditionalEvents) OnFinalizedAdditional?.Invoke(targetToPlace);
        }
        else
        {
            Debug.LogWarning("targetToPlace not assigned; pose computed but not applied.");
            //Log.Warn(LogCat, "targetToPlace not assigned; pose computed but not applied.", this);
        }

        OnQrScanFinished?.Invoke(finalPose.position, finalPose.rotation);

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.transform.position = finalPose.position;
        go.transform.localScale = Vector3.one * 0.05f;

        s.Finalized = true;
        targetProgressReference = 1f;
        //targetProgressReference.Value = 1f;

        if (stopAfterFinalize)
        {
            StopScanning();
            EnsureQrTrackingEnabled(false);
            ClearSeriesForReseed(false);
            _qrList.Clear();
        }
    }

    bool MatchesPayloadFilter(MRUKTrackable trk)
    {
        if (trk == null) return false;
        if (string.IsNullOrEmpty(payloadContainsFilter)) return true;
        return (trk.MarkerPayloadString ?? "").IndexOf(payloadContainsFilter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    bool IsTrackableUsable(MRUKTrackable trk)
    {
        if (trk == null) return false;
        if (!trk.isActiveAndEnabled) return false;
        if (trk.TrackableType != OVRAnchor.TrackableType.QRCode) return false;
        if (!MatchesPayloadFilter(trk)) return false;
        return trk.IsTracked;
    }

    bool IsSeriesTrackedNow(Series s)
    {
        if (s == null || s.Trackable == null) return false;

        bool trackedNow = IsTrackableUsable(s.Trackable);
        if (trackedNow)
        {
            s.Anchor = s.Trackable.transform;
            s.LastTrackedTime = Time.time;
        }

        return trackedNow;
    }

    bool IsSeriesAliveWithGrace(Series s)
    {
        if (s == null) return false;
        if (IsSeriesTrackedNow(s)) return true;
        return Time.time - s.LastTrackedTime <= Mathf.Max(0f, trackingLostGraceSeconds);
    }

    void ClearSeriesForReseed(bool resetProgress = true)
    {
        _series = null;
        _nextSeedAttemptTime = 0f;

        if (resetProgress)
            targetProgressReference = 0f;
            //targetProgressReference.ResetValue();
    }

    void PruneQrList()
    {
        _qrList.RemoveAll(t => t == null || !t.isActiveAndEnabled || t.TrackableType != OVRAnchor.TrackableType.QRCode);
    }

    static bool IsDiverse(Vector3 newDir, float newDist, List<Vector3> dirs, List<float> dists, float minAngleDeg, float minDistM)
    {
        if (dirs == null || dirs.Count == 0) return true;

        float bestAngle = 180f;
        float bestDelta = float.MaxValue;

        for (int i = 0; i < dirs.Count; i++)
        {
            float a = Vector3.Angle(newDir, dirs[i]);
            if (a < bestAngle) bestAngle = a;

            float d = Mathf.Abs(newDist - dists[i]);
            if (d < bestDelta) bestDelta = d;
        }

        return bestAngle >= minAngleDeg || bestDelta >= minDistM;
    }

    static Pose ComputeBurstCentralPose(List<Pose> burst, out float rmsTransM, out float rmsAngleDeg)
    {
        if (burst == null || burst.Count == 0)
        {
            rmsTransM = 0;
            rmsAngleDeg = 0;
            return default;
        }

        float Median(IList<float> arr)
        {
            var a = arr.OrderBy(v => v).ToArray();
            int n = a.Length;
            return n % 2 == 1 ? a[n / 2] : 0.5f * (a[n / 2 - 1] + a[n / 2]);
        }

        var xs = new List<float>(burst.Count);
        var ys = new List<float>(burst.Count);
        var zs = new List<float>(burst.Count);

        foreach (var p in burst)
        {
            xs.Add(p.position.x);
            ys.Add(p.position.y);
            zs.Add(p.position.z);
        }

        var medPos = new Vector3(Median(xs), Median(ys), Median(zs));
        Quaternion refQ = burst[0].rotation;
        Vector4 acc = Vector4.zero;

        foreach (var p in burst)
        {
            var q = p.rotation;
            if (Quaternion.Dot(q, refQ) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            acc += new Vector4(q.x, q.y, q.z, q.w);
        }

        Quaternion meanQ = NormalizeQuaternion(new Quaternion(acc.x, acc.y, acc.z, acc.w));

        double sumT2 = 0;
        double sumA2 = 0;

        for (int i = 0; i < burst.Count; i++)
        {
            float dPos = Vector3.Distance(burst[i].position, medPos);
            float dAng = AngleBetween(meanQ, burst[i].rotation);
            sumT2 += dPos * dPos;
            sumA2 += dAng * dAng;
        }

        rmsTransM = Mathf.Sqrt((float)(sumT2 / burst.Count));
        rmsAngleDeg = Mathf.Sqrt((float)(sumA2 / burst.Count));
        return new Pose(medPos, meanQ);
    }

    static Pose ComputeSetMedianPose(List<Pose> poses)
    {
        if (poses == null || poses.Count == 0) return default;

        float Median(IList<float> arr)
        {
            var a = arr.OrderBy(v => v).ToArray();
            int n = a.Length;
            return n % 2 == 1 ? a[n / 2] : 0.5f * (a[n / 2 - 1] + a[n / 2]);
        }

        var xs = new List<float>(poses.Count);
        var ys = new List<float>(poses.Count);
        var zs = new List<float>(poses.Count);

        foreach (var p in poses)
        {
            xs.Add(p.position.x);
            ys.Add(p.position.y);
            zs.Add(p.position.z);
        }

        Vector3 medPos = new(Median(xs), Median(ys), Median(zs));
        int bestIdx = 0;
        float bestCost = float.MaxValue;

        for (int i = 0; i < poses.Count; i++)
        {
            float cost = 0f;
            var qi = poses[i].rotation;

            for (int j = 0; j < poses.Count; j++)
            {
                if (i != j) cost += AngleBetween(qi, poses[j].rotation);
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                bestIdx = i;
            }
        }

        return new Pose(medPos, poses[bestIdx].rotation);
    }

    static Pose RobustIrlsPose(IReadOnlyList<Pose> poses, int iters, float kPos_mm, float kRot_deg)
    {
        if (poses == null || poses.Count == 0) return default;

        Quaternion refQ = poses[0].rotation;
        Vector4 accQ = Vector4.zero;
        Vector3 accP = Vector3.zero;

        for (int i = 0; i < poses.Count; i++)
        {
            var q = poses[i].rotation;
            if (Quaternion.Dot(q, refQ) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            accQ += new Vector4(q.x, q.y, q.z, q.w);
            accP += poses[i].position;
        }

        Quaternion qEst = NormalizeQuaternion(new Quaternion(accQ.x, accQ.y, accQ.z, accQ.w));
        Vector3 pEst = accP / poses.Count;

        for (int it = 0; it < Mathf.Max(1, iters); it++)
        {
            double wSum = 0;
            Vector3 pNum = Vector3.zero;
            Vector4 qNum = Vector4.zero;
            refQ = qEst;

            for (int i = 0; i < poses.Count; i++)
            {
                float dPosMm = Vector3.Distance(poses[i].position, pEst) * 1000f;
                float dAngDeg = AngleBetween(poses[i].rotation, qEst);

                double w = Math.Sqrt(
                    HuberWeight(dPosMm, Mathf.Max(0.001f, kPos_mm)) *
                    HuberWeight(dAngDeg, Mathf.Max(0.001f, kRot_deg)));

                if (w <= 0) continue;

                pNum += poses[i].position * (float)w;

                var q = poses[i].rotation;
                if (Quaternion.Dot(q, refQ) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                qNum += new Vector4(q.x, q.y, q.z, q.w) * (float)w;
                wSum += w;
            }

            if (wSum <= 0) break;

            pEst = pNum / (float)wSum;
            qEst = NormalizeQuaternion(new Quaternion(qNum.x, qNum.y, qNum.z, qNum.w));
        }

        return new Pose(pEst, qEst);
    }

    static float HuberWeight(float r, float k) => r <= k ? 1f : k / r;

    static Quaternion NormalizeQuaternion(Quaternion q)
    {
        float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return m <= 1e-8f ? Quaternion.identity : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
    }

    static float AngleBetween(Quaternion a, Quaternion b)
    {
        float dot = Mathf.Clamp(Mathf.Abs(Quaternion.Dot(a, b)), 0f, 1f);
        return Mathf.Rad2Deg * 2f * Mathf.Acos(dot);
    }

    bool PassesViewGate(Transform anchor)
    {
        float gate = (iterationAmount <= 1) ? Mathf.Max(maxViewAngleFromNormalDeg, 75f) : maxViewAngleFromNormalDeg;
        if (gate <= 0f || !_cam || anchor == null) return true;

        Vector3 camToMarker = (anchor.position - _cam.position).normalized;
        float aFwd = Vector3.Angle(-camToMarker, anchor.rotation * Vector3.forward);
        float aBack = Vector3.Angle(-camToMarker, anchor.rotation * Vector3.back);
        return Mathf.Min(aFwd, aBack) <= gate;
    }

    static string MakeKey(Guid uuid, string payload)
    {
        return !string.IsNullOrEmpty(payload)
            ? $"payload:{payload}"
            : $"uuid:{uuid.ToString("N").Substring(0, 8)}";
    }


    public void ApplyConfig(QRScannerConfig config)
    {
        var qr = config;
        if (qr == null)
        {
            Debug.LogWarning("ApplyConfig received wrong type. Expected QRScannerConfig.");
            //Log.Warn(LogCat, "ApplyConfig received wrong type. Expected QRScannerConfig.", this);
            return;
        }

        autoStart = qr.autoStart;
        stopAfterFinalize = qr.stopAfterFinalize;
        payloadContainsFilter = qr.payloadContainsFilter;
        iterationAmount = qr.iterationAmount;
        burstPosRmsThresholdMm = qr.burstPosRmsThresholdMm;
        burstAngleRmsThresholdDeg = qr.burstAngleRmsThresholdDeg;
        samplesPerBurst = qr.samplesPerBurst;
        interScanIntervalSeconds = qr.interScanIntervalSeconds;
        reacquireCooldownSeconds = qr.reacquireCooldownSeconds;
        maxViewAngleFromNormalDeg = qr.maxViewAngleFromNormalDeg;
        requireViewpointDiversity = qr.requireViewpointDiversity;
        minAngleBetweenBurstsDeg = qr.minAngleBetweenBurstsDeg;
        minDistanceDeltaM = qr.minDistanceDeltaM;
        finalWindow = qr.finalWindow;
        inlierMaxMm = qr.inlierMaxMm;
        inlierMaxDeg = qr.inlierMaxDeg;
        irlsIterations = qr.irlsIterations;
        huberPosK_mm = qr.huberPosK_mm;
        huberRotK_deg = qr.huberRotK_deg;
        restartKey = qr.restartKey;
        resetTargetOnRestart = qr.resetTargetOnRestart;
        invokeAdditionalEvents = qr.invokeAdditionalEvents;
    }
}