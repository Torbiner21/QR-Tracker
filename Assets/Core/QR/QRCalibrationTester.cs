using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Meta.XR.MRUtilityKit;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

public class QRCalibrationTester : MonoBehaviour
{
    [Header("Calibration Settings")]
    public MRUK mruk;
    [Min(3)] public int iterationAmount = 10; // Requires valid scans per QR before finalization
    public float interScanIntervalSeconds = 1.5f;
    public UnityEvent<int> OnValidScan; // Invoked every time a burst is accepted for ANY QR
    public UnityEvent OnSeriesFinalized; // Invoked once when a QR reaches the required iterationAmount and gets finalized

    [Header("Burst Sampling")]
    [Range(3, 20)] public int samples = 7; // frames per burst
    public float posStabilityThresholdMm = 1f; // RMS translation gate (mm)
    public float angleStabilityThresholdDeg = 5f; // RMS rotation gate (deg)

    [Header("Stability Gates")]
    [Tooltip("0° = face-on; higher = more oblique. 0 disables.")] [Range(0f, 30)] public float maxViewAngleFromNormalDeg = 15f;
    public float normalRmsThresholdMm = 1.0f; // Reject burst if local Z RMS (marker normal) exceeds this (mm). 0 disables
    public float inlierMaxMm = 2f; // Final inlier gate for robust re-median (position in mm)
    public float inlierMaxDeg = 2; // Final inlier gate for robust re-median (angle in deg)

    [Header("Cross-Session Save")]
    public bool saveToPlayerPrefs = true;
    public string PlayerPrefsPosePrefix = "QRFinalPose_";

    [Header("Dev Tools")]
    public bool enableDevFeatures = true;
    public TMP_Text logsText;
    public GameObject finalPosPrefab; // optional final pose marker prefab
    public GameObject qrPrefab; // optional live widget prefab
    public string csvFilename = "QRResults.csv";

    bool _listening;
    bool _running;
    readonly StringBuilder _logBuilder = new();
    int _logLines;

    private class Series
    {
        public string Key;
        public Guid Uuid;
        public int? Index;
        public string Payload = "(no payload)";
        public Transform AnchorTransform;
        public List<Pose> Poses = new();
        public int Captures;
        public bool IsFinalized;

        public GameObject WidgetGO;
        public Transform WidgetTransform;
        public QRVarianceWidget Widget;

        public Vector3 finalAnchor;
        public Quaternion finalRotation = Quaternion.identity;

        public Rect? PlaneRectLocal;
        public GameObject FinalGO;
        public Transform FinalTransform;

        public float NextEligibleTime;
    }

    private readonly Dictionary<string, Series> _seriesByKey = new();
    private readonly Dictionary<Guid, string> _uuidToKey = new();

    void OnEnable()
    {
        mruk.SceneSettings.TrackableAdded.AddListener(OnTrackableAdded);
        mruk.SceneSettings.TrackableRemoved.AddListener(OnTrackableRemoved);
        _listening = true;
    }

    void OnDisable()
    {
        if (_listening && mruk)
        {
            mruk.SceneSettings.TrackableAdded.RemoveListener(OnTrackableAdded);
            mruk.SceneSettings.TrackableRemoved.RemoveListener(OnTrackableRemoved);
            _listening = false;
        }
    }

    void Start()
    {
        SetQrTrackingEnabled(true);
        _running = true;
        if (enableDevFeatures) Log($"Collecting until each QR reaches {iterationAmount} valid scans…");
        StartCoroutine(ScanLoop());
    }

    IEnumerator ScanLoop()
    {
        while (_running)
        {
            var cam = Camera.main ? Camera.main.transform : null;

            foreach (var s in _seriesByKey.Values
                         .Where(v => !v.IsFinalized)
                         .OrderBy(v => v.Index.HasValue ? 0 : 1)
                         .ThenBy(v => v.Index ?? int.MaxValue)
                         .ThenBy(v => v.Key))
            {
                if (!s.AnchorTransform)
                    continue;

                if (interScanIntervalSeconds > 0f && Time.time < s.NextEligibleTime)
                    continue;

                if (!PassesViewGeometry(s.AnchorTransform, cam))
                    continue;

                var burst = new List<Pose>(Mathf.Max(3, samples));
                for (int i = 0; i < samples; i++)
                {
                    var tr = s.AnchorTransform;
                    burst.Add(new Pose(tr.position, tr.rotation));
                    yield return null;
                }

                var (medianPos, medoidRot, rmsTransM, rmsAngleDeg) = ComputeBurstMedianAndRms(burst);
                float rmsLocalNormalMm = ComputeLocalNormalRmsMm(burst, medianPos, medoidRot);

                bool stable =
                    (rmsTransM * 1000f <= Mathf.Max(0.001f, posStabilityThresholdMm)) &&
                    (rmsAngleDeg <= Mathf.Max(0.001f, angleStabilityThresholdDeg)) &&
                    ((normalRmsThresholdMm <= 0f) || (rmsLocalNormalMm <= normalRmsThresholdMm));

                if (!stable)
                    continue;

                var acceptedPose = new Pose(medianPos, medoidRot);
                s.Poses.Add(acceptedPose);
                s.Captures++;

                if (s.WidgetTransform)
                {
                    s.WidgetTransform.position = acceptedPose.position;
                    s.WidgetTransform.rotation = acceptedPose.rotation;
                }
                var stats = ComputeStats(s.Poses);
                s.Widget?.SetStats(s.Payload, s.Uuid, s.Poses.Count, stats, acceptedPose);

                OnValidScan?.Invoke(s.Poses.Count);

                if (enableDevFeatures)
                    Log($"[{s.Key}] accepted #{s.Poses.Count}/{iterationAmount}  (burstRMS={rmsTransM*1000f:0.000}mm, {rmsAngleDeg:0.00}°  Zrms={rmsLocalNormalMm:0.000}mm)");

                s.NextEligibleTime = Time.time + interScanIntervalSeconds;

                if (s.Poses.Count >= iterationAmount)
                {
                    FinalizeSeries(s);
                    OnSeriesFinalized?.Invoke();
                }
            }

            if (_seriesByKey.Count > 0 && _seriesByKey.Values.All(v => v.IsFinalized))
            {
                if (enableDevFeatures) Log("All known QRs finalized; stopping collection.");
                _running = false;
            }

            yield return null;
        }
    }

    void FinalizeSeries(Series s)
    {
        if (s.IsFinalized)
            return;

        Pose finalPose = ComputeSetMedianPoseRobust(s.Poses, inlierMaxMm, inlierMaxDeg);
        s.finalAnchor = finalPose.position;
        s.finalRotation = finalPose.rotation;
        s.IsFinalized = true;

        if (enableDevFeatures)
            PlaceOrUpdateFinalMarker(s);

        if (saveToPlayerPrefs)
            SaveFinalPoseToPrefs(s);

        AppendSeriesToCsv(s);

        var stats = ComputeStats(s.Poses);
        Vector3 stdMm = stats.stdPos * 1000f;
        s.Widget?.SetFinal(finalPose, stdMm, s.Poses.Count);

        if (enableDevFeatures)
            Log($"[{s.Key}] FINAL n={s.Poses.Count}  std(mm)=({stdMm.x:0.000},{stdMm.y:0.000},{stdMm.z:0.000})  meanAngle={stats.meanAngleDeg:0.000}°  stdAngle={stats.stdAngleDeg:0.000}°");
    }
    
    // Math Helpers

    static (Vector3 medianPos, Quaternion medoidRot, float rmsTransM, float rmsAngleDeg)
        ComputeBurstMedianAndRms(List<Pose> burst)
    {
        if (burst == null || burst.Count == 0)
            return (Vector3.zero, Quaternion.identity, 0f, 0f);

        float Median(IList<float> xs)
        {
            var a = xs.OrderBy(v => v).ToArray();
            int n = a.Length;
            return (n % 2 == 1) ? a[n / 2] : 0.5f * (a[n / 2 - 1] + a[n / 2]);
        }

        var xs = new List<float>(burst.Count);
        var ys = new List<float>(burst.Count);
        var zs = new List<float>(burst.Count);
        foreach (var p in burst) { xs.Add(p.position.x); ys.Add(p.position.y); zs.Add(p.position.z); }
        var medianPos = new Vector3(Median(xs), Median(ys), Median(zs));

        int bestIdx = 0; float bestCost = float.MaxValue;
        for (int i = 0; i < burst.Count; i++)
        {
            float cost = 0f; var qi = burst[i].rotation;
            for (int j = 0; j < burst.Count; j++) { if (i == j) continue; cost += AngleBetween(qi, burst[j].rotation); }
            if (cost < bestCost) { bestCost = cost; bestIdx = i; }
        }
        var medoidRot = burst[bestIdx].rotation;

        double sumT2 = 0.0, sumA2 = 0.0;
        for (int i = 0; i < burst.Count; i++)
        {
            float dPos = Vector3.Distance(burst[i].position, medianPos);
            float dAng = AngleBetween(medoidRot, burst[i].rotation);
            sumT2 += dPos * dPos; sumA2 += dAng * dAng;
        }
        float rmsTrans = Mathf.Sqrt((float)(sumT2 / burst.Count));
        float rmsAngle = Mathf.Sqrt((float)(sumA2 / burst.Count));
        return (medianPos, medoidRot, rmsTrans, rmsAngle);
    }

    static Pose ComputeSetMedianPose(List<Pose> poses)
    {
        if (poses == null || poses.Count == 0) return default;

        float Median(IList<float> xs)
        {
            var a = xs.OrderBy(v => v).ToArray();
            int n = a.Length;
            return (n % 2 == 1) ? a[n / 2] : 0.5f * (a[n / 2 - 1] + a[n / 2]);
        }

        var xs = new List<float>(poses.Count);
        var ys = new List<float>(poses.Count);
        var zs = new List<float>(poses.Count);
        for (int i = 0; i < poses.Count; i++) { xs.Add(poses[i].position.x); ys.Add(poses[i].position.y); zs.Add(poses[i].position.z); }
        Vector3 medPos = new(Median(xs), Median(ys), Median(zs));

        int bestIdx = 0; float bestCost = float.MaxValue;
        for (int i = 0; i < poses.Count; i++)
        {
            float cost = 0f; var qi = poses[i].rotation;
            for (int j = 0; j < poses.Count; j++) { if (i == j) continue; cost += AngleBetween(qi, poses[j].rotation); }
            if (cost < bestCost) { bestCost = cost; bestIdx = i; }
        }
        Quaternion medRot = poses[bestIdx].rotation;
        return new Pose(medPos, medRot);
    }

    static Pose ComputeSetMedianPoseRobust(List<Pose> poses, float inlierMaxMm, float inlierMaxDeg)
    {
        if (poses == null || poses.Count == 0) return default;

        Pose seed = ComputeSetMedianPose(poses);
        float maxDistM = Mathf.Max(0.00001f, inlierMaxMm / 1000f);
        float maxAngDeg = Mathf.Max(0.0001f, inlierMaxDeg);

        var inliers = new List<Pose>(poses.Count);
        foreach (var p in poses)
        {
            float dPos = Vector3.Distance(p.position, seed.position);
            float dAng = AngleBetween(seed.rotation, p.rotation);
            if (dPos <= maxDistM && dAng <= maxAngDeg) inliers.Add(p);
        }

        if (inliers.Count < Mathf.Max(3, poses.Count / 3))
            return seed;

        return ComputeSetMedianPose(inliers);
    }

    static float ComputeLocalNormalRmsMm(List<Pose> burst, Vector3 medianPos, Quaternion medoidRot)
    {
        if (burst == null || burst.Count == 0) return 0f;
        double sumZ2 = 0.0;
        for (int i = 0; i < burst.Count; i++)
        {
            Vector3 dw = burst[i].position - medianPos;
            Vector3 dl = Quaternion.Inverse(medoidRot) * dw;
            sumZ2 += dl.z * dl.z;
        }
        return Mathf.Sqrt((float)(sumZ2 / burst.Count)) * 1000f;
    }

    static (Vector3 meanPos, Vector3 stdPos,
            float meanRadial, float stdRadial,
            float meanAngleDeg, float stdAngleDeg,
            float maxAngleDeg, float maxDistance, Pose reference)
        ComputeStats(IReadOnlyList<Pose> poses)
    {
        if (poses == null || poses.Count == 0)
            return (Vector3.zero, Vector3.zero, 0, 0, 0, 0, 0, 0, default);

        Pose p0 = poses[0];

        Vector3 mean = Vector3.zero;
        foreach (var p in poses) mean += p.position;
        mean /= poses.Count;

        Vector3 varSum = Vector3.zero;
        foreach (var p in poses)
        {
            Vector3 d = p.position - mean;
            varSum += new Vector3(d.x * d.x, d.y * d.y, d.z * d.z);
        }
        Vector3 std = new Vector3(
            Mathf.Sqrt(varSum.x / poses.Count),
            Mathf.Sqrt(varSum.y / poses.Count),
            Mathf.Sqrt(varSum.z / poses.Count)
        );

        List<float> radial = new();
        float maxDist = 0f;
        foreach (var p in poses)
        {
            float d = Vector3.Distance(p.position, mean);
            radial.Add(d);
            if (d > maxDist) maxDist = d;
        }
        float meanRadial = radial.Average();
        float stdRadial = Mathf.Sqrt(radial.Average(v => (v - meanRadial) * (v - meanRadial)));

        List<float> angs = new();
        float maxAng = 0f;
        foreach (var p in poses)
        {
            float ang = AngleBetween(p0.rotation, p.rotation);
            angs.Add(ang);
            if (ang > maxAng) maxAng = ang;
        }
        float meanAng = angs.Average();
        float stdAng = Mathf.Sqrt(angs.Average(v => (v - meanAng) * (v - meanAng)));

        return (mean, std, meanRadial, stdRadial, meanAng, stdAng, maxAng, maxDist, p0);
    }

    static float AngleBetween(Quaternion a, Quaternion b)
    {
        var d = Quaternion.Inverse(a) * b;
        d.ToAngleAxis(out float angleDeg, out _);
        if (angleDeg > 180f) angleDeg = 360f - angleDeg;
        return angleDeg;
    }

    void PlaceOrUpdateFinalMarker(Series s)
    {
        if (!finalPosPrefab) return;

        if (!s.FinalGO)
        {
            s.FinalGO = Instantiate(finalPosPrefab);
            s.FinalTransform = s.FinalGO.transform;
        }

        s.FinalTransform.position = s.finalAnchor;
        s.FinalTransform.rotation = s.finalRotation;

        if (s.PlaneRectLocal.HasValue)
        {
            var rectViz = s.FinalGO.GetComponentInChildren<FinalRectVisualizer>();
            if (rectViz)
                rectViz.SetRect(s.PlaneRectLocal.Value);
        }
    }


    void SaveFinalPoseToPrefs(Series s)
    {
        string baseKey = PlayerPrefsPosePrefix + s.Key; // ie: "QRFinalPose_idx:7"
        PlayerPrefs.SetFloat(baseKey + "_pos_x", s.finalAnchor.x);
        PlayerPrefs.SetFloat(baseKey + "_pos_y", s.finalAnchor.y);
        PlayerPrefs.SetFloat(baseKey + "_pos_z", s.finalAnchor.z);

        PlayerPrefs.SetFloat(baseKey + "_rot_x", s.finalRotation.x);
        PlayerPrefs.SetFloat(baseKey + "_rot_y", s.finalRotation.y);
        PlayerPrefs.SetFloat(baseKey + "_rot_z", s.finalRotation.z);
        PlayerPrefs.SetFloat(baseKey + "_rot_w", s.finalRotation.w);

        PlayerPrefs.SetString(baseKey + "_ver", "3"); // format/version tag
        PlayerPrefs.Save();
    }

    void AppendSeriesToCsv(Series s)
    {
        try
        {
            string dir = Application.persistentDataPath;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, csvFilename);
            bool writeHeader = !File.Exists(path);

            var stats = ComputeStats(s.Poses);
            Vector3 stdMm = stats.stdPos * 1000f;
            string ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            var sb = new StringBuilder();
            if (writeHeader)
            {
                sb.AppendLine(string.Join(",",
                    "timestamp",
                    "qrIndex",
                    "validScansN",
                    "stdPosX_mm","stdPosY_mm","stdPosZ_mm",
                    "stdAngle_deg","maxAngle_deg",
                    "maxViewAngle_deg",
                    "posStability_mm","angleStability_deg","normalRms_mm",
                    "burstSamples",
                    "iterationTarget"
                ));
            }

            string row = string.Join(",",
                ts,
                s.Index.HasValue ? s.Index.Value.ToString(CultureInfo.InvariantCulture) : "",
                s.Poses.Count.ToString(CultureInfo.InvariantCulture),
                stdMm.x.ToString(CultureInfo.InvariantCulture),
                stdMm.y.ToString(CultureInfo.InvariantCulture),
                stdMm.z.ToString(CultureInfo.InvariantCulture),
                stats.stdAngleDeg.ToString(CultureInfo.InvariantCulture),
                stats.maxAngleDeg.ToString(CultureInfo.InvariantCulture),
                maxViewAngleFromNormalDeg.ToString(CultureInfo.InvariantCulture),
                posStabilityThresholdMm.ToString(CultureInfo.InvariantCulture),
                angleStabilityThresholdDeg.ToString(CultureInfo.InvariantCulture),
                normalRmsThresholdMm.ToString(CultureInfo.InvariantCulture),
                samples.ToString(CultureInfo.InvariantCulture),
                iterationAmount.ToString(CultureInfo.InvariantCulture)
            );

            sb.AppendLine(row);
            File.AppendAllText(path, sb.ToString());

            if (enableDevFeatures) Log($"CSV appended: {path}");
        }
        catch (Exception ex)
        {
            if (enableDevFeatures) LogErr($"CSV export failed: {ex.Message}");
        }
    }

    void OnTrackableAdded(MRUKTrackable trackable)
    {
        if (trackable.TrackableType != OVRAnchor.TrackableType.QRCode) return;

        var uuid = trackable.Anchor.Uuid;
        string payload = trackable.MarkerPayloadString ?? "(no payload)";
        var key = MakeSeriesKey(uuid, payload);

        if (!_seriesByKey.TryGetValue(key, out var s))
        {
            s = new Series
            {
                Key = key,
                Uuid = uuid,
                Index = TryFirstInteger(payload),
                Payload = payload,
                AnchorTransform = trackable.transform
            };

            if (trackable.PlaneRect.HasValue)
                s.PlaneRectLocal = trackable.PlaneRect.Value;

            if (qrPrefab && enableDevFeatures)
            {
                s.WidgetGO = Instantiate(qrPrefab);
                s.WidgetTransform = s.WidgetGO.transform;
                s.WidgetTransform.position = trackable.transform.position;
                s.WidgetTransform.rotation = trackable.transform.rotation;
                s.Widget = s.WidgetGO.GetComponent<QRVarianceWidget>();
                if (s.Widget) s.Widget.SetIdentity(s.Key, s.Uuid);
            }

            _seriesByKey[key] = s;
        }
        else
        {
            s.Uuid = uuid;
            s.Payload = payload;
            s.AnchorTransform = trackable.transform;
            if (trackable.PlaneRect.HasValue)
                s.PlaneRectLocal = trackable.PlaneRect.Value;

            if (s.WidgetTransform)
            {
                s.WidgetTransform.position = trackable.transform.position;
                s.WidgetTransform.rotation = trackable.transform.rotation;
            }

            s.Widget?.SetIdentity(s.Key, s.Uuid);
        }

        _uuidToKey[uuid] = key;
    }

    void OnTrackableRemoved(MRUKTrackable trackable)
    {
        if (trackable.TrackableType != OVRAnchor.TrackableType.QRCode) return;

        var uuid = trackable.Anchor.Uuid;
        if (_uuidToKey.TryGetValue(uuid, out var key) && _seriesByKey.TryGetValue(key, out var s))
            s.AnchorTransform = null;
    }

    void SetQrTrackingEnabled(bool enabled)
    {
        var cfg = mruk.SceneSettings.TrackerConfiguration;
        if (cfg.QRCodeTrackingEnabled == enabled) return;
        cfg.QRCodeTrackingEnabled = enabled;
        mruk.SceneSettings.TrackerConfiguration = cfg;
    }

    static string MakeSeriesKey(Guid uuid, string payload)
    {
        var idx = string.IsNullOrEmpty(payload) ? (int?)null : TryFirstInteger(payload);
        if (idx.HasValue && idx.Value >= 0) return $"idx:{idx.Value}";
        return $"uuid:{uuid.ToString("N").Substring(0, 8)}";
    }

    static int? TryFirstInteger(string s)
    {
        int sign = 1; bool inDigits = false; long acc = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (!inDigits)
            {
                if ((c == '+' || c == '-') && i + 1 < s.Length && char.IsDigit(s[i + 1]))
                { sign = (c == '-') ? -1 : 1; inDigits = true; i++; acc = s[i] - '0'; continue; }
                if (char.IsDigit(c)) { sign = 1; inDigits = true; acc = c - '0'; continue; }
            }
            else
            {
                if (char.IsDigit(c)) acc = acc * 10 + (c - '0');
                else return (int)(sign * acc);
            }
        }
        if (inDigits) return (int)(sign * acc);
        return null;
    }

    bool PassesViewGeometry(Transform anchor, Transform cam)
    {
        if (!cam) return true;
        Vector3 camToMarker = (anchor.position - cam.position).normalized;
        Vector3 markerNormal = anchor.rotation * Vector3.forward;
        float angleFromFaceOn = Vector3.Angle(-camToMarker, markerNormal);
        if (maxViewAngleFromNormalDeg > 0f && angleFromFaceOn > maxViewAngleFromNormalDeg)
            return false;
        return true;
    }

    void UILog(object msg, LogType type = LogType.Log)
    {
        if (!enableDevFeatures) return;

        if (_logLines++ > 0) _logBuilder.Append('\n');
        _logBuilder.Append(_logLines);

        string colorTag = null;
        switch (type)
        {
            case LogType.Error:
            case LogType.Assert:
            case LogType.Exception: colorTag = "<color=red>"; break;
        }

        _logBuilder.Append($"{colorTag} | {msg}");
        if (colorTag is not null) _logBuilder.Append("</color>");

        if (logsText)
        {
            logsText.SetText(_logBuilder);
            logsText.pageToDisplay = logsText.textInfo?.pageCount ?? 1;
        }
    }

    void Log(object msg) => UILog(msg, LogType.Log);
    void LogErr(object msg) => UILog(msg, LogType.Error);
}
