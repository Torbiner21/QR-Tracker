using System.Collections;
using System.Reflection;
using Meta.XR.MRUtilityKit;
//using SurgicalScience.Logger;
//using SurgicalScience.TraumaVR.Configuration;
using UnityEngine;

public class AlignTrackingSpaceOffsetToTarget : MonoBehaviour
{
    [Header("References")]
    [Tooltip("TrackingSpace (A)")]
    public GameObject parentA;

    [Tooltip("Scanned QR transform (B) — pose source for calibration")]
    public GameObject childB;

    [Tooltip("Target to overlap (C) — changes per scene")]
    public GameObject targetC;

    [Header("Alignment Mode")]
    public AlignMode alignMode = AlignMode.Auto;
    public enum AlignMode { Auto, ForceMrukReflection, ForceDirectTransform }

    private enum ComposeMode { PreMultiply, PostMultiply }
    private readonly ComposeMode _composeMode = ComposeMode.PreMultiply;

    [Header("Persistence")]
    public bool persistAcrossScenes = true;

    [Header("Continuous Correction")]
    [Tooltip("Continuously realigns if smoothed anchor drifts from target.")]
    public bool continuousCorrection = false;
    public float correctionPosThreshold = 0.005f;
    public float correctionRotThreshold = 1f;
    [Tooltip("Frames to skip continuous correction after an alignment.")]
    public int correctionCooldownFrames = 10;

    [Header("Anchor Smoothing")]
    public float smoothPosSpeed = 8f;
    public float smoothRotSpeed = 8f;
    public float stablePosTolerance  = 0.001f;
    public float stableRotTolerance  = 0.5f;
    public int   stableFramesRequired = 15;

    [Header("Debug / Manual Trigger")]
    public bool    useManualAlignment = false;
    public KeyCode alignKey = KeyCode.T;

    //private static readonly LogCategory LogCat = LogCatalogCalibration.Align;

    // ── Runtime state ──────────────────────────────────────────────────────────

    private OVRSpatialAnchor _qrAnchor;
    private Transform        _smoothAnchor;
    private bool             _smoothInitialized;

    private bool _isBusy;
    private int  _skipCorrectionFrames;
    private int  _consecutiveStableFrames;

    private Vector3    _prevSmoothPos;
    private Quaternion _prevSmoothRot;

    private Transform _cachedA;
    private Transform _cachedC;

    // ── Unity ──────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (persistAcrossScenes) DontDestroyOnLoad(gameObject);
    }

    private void Update()
    {
        if (useManualAlignment && Input.GetKeyDown(alignKey)) AlignNow();
        UpdateSmoothedAnchor();
    }

    private void LateUpdate()
    {
        if (!continuousCorrection || _smoothAnchor == null || _isBusy) return;
        if (_skipCorrectionFrames > 0) { _skipCorrectionFrames--; return; }

        float moveDist  = Vector3.Distance(_smoothAnchor.position, _prevSmoothPos);
        float moveAngle = Quaternion.Angle(_smoothAnchor.rotation, _prevSmoothRot);
        _prevSmoothPos  = _smoothAnchor.position;
        _prevSmoothRot  = _smoothAnchor.rotation;

        if (moveDist <= stablePosTolerance && moveAngle <= stableRotTolerance)
            _consecutiveStableFrames++;
        else
            _consecutiveStableFrames = 0;

        if (_consecutiveStableFrames < stableFramesRequired) return;

        RefreshCachedTransforms();
        if (_cachedA == null || _cachedC == null) return;

        float posDrift = Vector3.Distance(_smoothAnchor.position, _cachedC.position);
        float rotDrift = Quaternion.Angle(_smoothAnchor.rotation, _cachedC.rotation);

        if (posDrift > correctionPosThreshold || rotDrift > correctionRotThreshold)
        {
            Debug.Log($"Auto-correction applied (drift: pos={posDrift * 1000f:F1}mm, rot={rotDrift:F2}°).");
            //Log.Info(LogCat,
            //    $"Auto-correction applied (drift: pos={posDrift * 1000f:F1}mm, rot={rotDrift:F2}°).", this);
            ApplyAlign(_cachedA, _smoothAnchor, _cachedC);
        }
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public bool HasAnchor => _qrAnchor != null;

    /// <summary>
    /// Full fresh calibration: resolves A/B/C, snaps A so B overlaps C,
    /// creates a new spatial anchor at B, rebuilds the smooth anchor.
    /// </summary>
    [ContextMenu("Align Now")]
    public void AlignNow()
    {
        if (_isBusy) return;
        StartCoroutine(AlignNowRoutine());
    }

    /// <summary>
    /// Scene-change path: reapplies alignment using the existing spatial anchor.
    /// Does not create a new anchor or require B.
    /// </summary>
    public void AlignOnSceneChange()
    {
        if (_qrAnchor == null)
        {
            Debug.Log("No anchor exists — skipping scene-change alignment.");
            //Log.Info(LogCat, "No anchor exists — skipping scene-change alignment.", this);
            return;
        }
        if (_isBusy) return;
        StartCoroutine(AlignOnSceneChangeRoutine());
    }

    /// <summary>Destroys the existing anchor so the next AlignNow starts fresh.</summary>
    public void ResetAnchor()
    {
        DestroyAnchor();
        DestroySmoothedAnchor();

        _cachedA                 = null;
        _cachedC                 = null;
        _isBusy                  = false;
        _skipCorrectionFrames    = 0;
        _consecutiveStableFrames = 0;

        Debug.Log("Anchor Reset");
        //Log.Info(LogCat, "Anchor reset.", this);
    }

    // ── AlignNow routine ───────────────────────────────────────────────────────

    private IEnumerator AlignNowRoutine()
    {
        _isBusy = true;

        if (!parentA || !childB || !targetC)
        {
            Debug.LogError("A, B and C must all be assigned before aligning.");
            //Log.Error(LogCat, "A, B and C must all be assigned before aligning.", this);
            _isBusy = false;
            yield break;
        }

        bool useMruk = ShouldUseMruk();
        if (useMruk) yield return WaitForMrukStable();

        var aObj = parentA;
        var bObj = childB;
        var cObj = targetC;

        if (aObj == null || bObj == null || cObj == null)
        {
            Debug.Log("Could not resolve A, B or C at runtime.");
            //Log.Error(LogCat, "Could not resolve A, B or C at runtime.", this);
            _isBusy = false;
            yield break;
        }

        _cachedA = aObj.transform;
        _cachedC = cObj.transform;
        Transform b = bObj.transform;

        ApplyAlign(_cachedA, b, _cachedC);

        Debug.Log($"Alignment applied. Mode={(useMruk ? "MRUK" : "Direct")} " + $"B={b.position} → C={_cachedC.position}");

        //Log.Info(LogCat,
        //    $"Alignment applied. Mode={(useMruk ? "MRUK" : "Direct")} " +
        //    $"B={b.position} → C={_cachedC.position}", this);

        DestroyAnchor();
        DestroySmoothedAnchor();

        _qrAnchor = CreateAnchorAtPose(b.position, b.rotation);
        yield return WaitForAnchorCreated(_qrAnchor);

        if (_qrAnchor == null || _qrAnchor.Uuid == System.Guid.Empty)
            Debug.LogWarning("Spatial anchor creation failed or timed out.");
        //Log.Warn(LogCat, "Spatial anchor creation failed or timed out.", this);
        else
            Debug.Log($"Spatial anchor created: {_qrAnchor.Uuid}");
            //Log.Info(LogCat, $"Spatial anchor created: {_qrAnchor.Uuid}", this);

        EnsureSmoothedAnchor();
        SnapSmoothedAnchor();

        _skipCorrectionFrames    = correctionCooldownFrames;
        _consecutiveStableFrames = 0;
        _isBusy                  = false;
    }

    // ── AlignOnSceneChange routine ─────────────────────────────────────────────

    private IEnumerator AlignOnSceneChangeRoutine()
    {
        _isBusy = true;

        bool useMruk = ShouldUseMruk();
        if (useMruk) yield return WaitForMrukStable();

        var aObj = parentA;
        var cObj = targetC;

        if (aObj == null || cObj == null)
        {
            Debug.LogError("Cannot resolve A or C for scene-change alignment.");
            //Log.Error(LogCat, "Cannot resolve A or C for scene-change alignment.", this);
            _isBusy = false;
            yield break;
        }

        _cachedA = aObj.transform;
        _cachedC = cObj.transform;

        ApplyAlign(_cachedA, _qrAnchor.transform, _cachedC);
        Debug.Log($"Scene-change alignment applied. Mode={(useMruk ? "MRUK" : "Direct")} " + $"Target={_cachedC.position}");
        //Log.Info(LogCat,
        //    $"Scene-change alignment applied. Mode={(useMruk ? "MRUK" : "Direct")} " +
        //    $"Target={_cachedC.position}", this);

        EnsureSmoothedAnchor();
        SnapSmoothedAnchor();

        _skipCorrectionFrames    = correctionCooldownFrames;
        _consecutiveStableFrames = 0;
        _isBusy                  = false;
    }

    // ── Smooth anchor ──────────────────────────────────────────────────────────

    private void EnsureSmoothedAnchor()
    {
        if (_smoothAnchor != null) return;
        var go = new GameObject("QR_PhysicalAnchor_Smooth");
        DontDestroyOnLoad(go);
        _smoothAnchor      = go.transform;
        _smoothInitialized = false;
    }

    private void SnapSmoothedAnchor()
    {
        if (_qrAnchor == null || _smoothAnchor == null) return;
        _smoothAnchor.SetPositionAndRotation(_qrAnchor.transform.position, _qrAnchor.transform.rotation);
        _prevSmoothPos           = _smoothAnchor.position;
        _prevSmoothRot           = _smoothAnchor.rotation;
        _consecutiveStableFrames = 0;
        _smoothInitialized       = true;
    }

    private void UpdateSmoothedAnchor()
    {
        if (_qrAnchor == null || _smoothAnchor == null || !_smoothInitialized) return;

        float dt = Time.deltaTime;
        _smoothAnchor.position = Vector3.Lerp(
            _smoothAnchor.position, _qrAnchor.transform.position,
            1f - Mathf.Exp(-smoothPosSpeed * dt));
        _smoothAnchor.rotation = Quaternion.Slerp(
            _smoothAnchor.rotation, _qrAnchor.transform.rotation,
            1f - Mathf.Exp(-smoothRotSpeed * dt));
    }

    private void DestroySmoothedAnchor()
    {
        if (_smoothAnchor == null) return;
        Destroy(_smoothAnchor.gameObject);
        _smoothAnchor      = null;
        _smoothInitialized = false;
    }

    // ── Core alignment math ────────────────────────────────────────────────────

    private void ApplyAlign(Transform a, Transform b, Transform c)
    {
        if (ShouldUseMruk()) AlignViaMruk(a, b, c);
        else                 AlignDirect (a, b, c);

        _skipCorrectionFrames = correctionCooldownFrames;
    }

    private static void AlignDirect(Transform a, Transform b, Transform c)
    {
        Vector3    localPos = a.InverseTransformPoint(b.position);
        Quaternion localRot = Quaternion.Inverse(a.rotation) * b.rotation;

        Quaternion desRot = c.rotation * Quaternion.Inverse(localRot);
        Vector3    desPos = c.position - desRot * localPos;

        a.SetPositionAndRotation(desPos, desRot);
    }

    private void AlignViaMruk(Transform a, Transform b, Transform c)
    {
        var mruk = MRUK.Instance;
        if (mruk == null) return;

        Vector3    localPos = a.InverseTransformPoint(b.position);
        Quaternion localRot = Quaternion.Inverse(a.rotation) * b.rotation;

        Quaternion desRot = c.rotation * Quaternion.Inverse(localRot);
        Vector3    desPos = c.position - desRot * localPos;

        Quaternion deltaRot = desRot * Quaternion.Inverse(a.rotation);
        Vector3    deltaPos = desPos - deltaRot * a.position;
        Matrix4x4  deltaM   = Matrix4x4.TRS(deltaPos, deltaRot, Vector3.one);

        Matrix4x4 current = mruk.TrackingSpaceOffset;
        Matrix4x4 total   = _composeMode == ComposeMode.PreMultiply
            ? deltaM  * current
            : current * deltaM;

        if (!TrySetMrukTrackingSpaceOffset(mruk, total))
            Debug.LogError("MRUK reflection write failed — TrackingSpaceOffset could not be set.");
            //Log.Error(LogCat, "MRUK reflection write failed — TrackingSpaceOffset could not be set.", this);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private bool ShouldUseMruk() => alignMode switch
    {
        AlignMode.ForceMrukReflection  => true,
        AlignMode.ForceDirectTransform => false,
        _                              => MRUK.Instance != null && MRUK.Instance.IsWorldLockActive
    };

    private void RefreshCachedTransforms()
    {
        if (_cachedA == null && parentA != null)
        {
            var obj = parentA;
            if (obj != null) _cachedA = obj.transform;
        }
        if (_cachedC == null && targetC != null)
        {
            var obj = targetC;
            if (obj != null) _cachedC = obj.transform;
        }
    }

    private void DestroyAnchor()
    {
        if (_qrAnchor == null) return;
        Destroy(_qrAnchor.gameObject);
        _qrAnchor = null;
    }

    private static OVRSpatialAnchor CreateAnchorAtPose(Vector3 pos, Quaternion rot)
    {
        var go = new GameObject("QR_PhysicalAnchor");
        DontDestroyOnLoad(go);
        go.transform.SetPositionAndRotation(pos, rot);
        return go.AddComponent<OVRSpatialAnchor>();
    }

    private static IEnumerator WaitForAnchorCreated(OVRSpatialAnchor anchor)
    {
        if (anchor == null) yield break;
        for (int i = 0; i < 450; i++)
        {
            if (anchor != null && anchor.Uuid != System.Guid.Empty) yield break;
            yield return null;
        }
    }

    private IEnumerator WaitForMrukStable()
    {
        while (MRUK.Instance == null) yield return null;

        var mruk = MRUK.Instance;
        float waited = 0f;
        while (!mruk.IsWorldLockActive && waited < 10f)
        {
            yield return new WaitForSeconds(0.1f);
            waited += 0.1f;
        }

        Matrix4x4 prev = mruk.TrackingSpaceOffset;
        int stableFrames = 0;
        float stabWaited = 0f;
        while (stableFrames < 15 && stabWaited < 5f)
        {
            yield return null;
            stabWaited += Time.deltaTime;
            Matrix4x4 cur = mruk.TrackingSpaceOffset;
            if (MatrixApproxEqual(cur, prev)) stableFrames++;
            else { stableFrames = 0; prev = cur; }
        }
    }

    private static bool MatrixApproxEqual(Matrix4x4 a, Matrix4x4 b, float eps = 0.0001f)
    {
        for (int i = 0; i < 16; i++)
            if (Mathf.Abs(a[i] - b[i]) > eps) return false;
        return true;
    }

    private static bool TrySetMrukTrackingSpaceOffset(MRUK mruk, Matrix4x4 value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var t = mruk.GetType();

        var prop = t.GetProperty("TrackingSpaceOffset", flags);
        if (prop != null && prop.CanWrite) { prop.SetValue(mruk, value); return true; }

        var field = t.GetField("TrackingSpaceOffset", flags);
        if (field != null) { field.SetValue(mruk, value); return true; }

        var backing = t.GetField("<TrackingSpaceOffset>k__BackingField", flags);
        if (backing != null) { backing.SetValue(mruk, value); return true; }

        foreach (var name in new[] { "_trackingSpaceOffset", "trackingSpaceOffset", "_TrackingSpaceOffset" })
        {
            var f = t.GetField(name, flags);
            if (f != null && f.FieldType == typeof(Matrix4x4)) { f.SetValue(mruk, value); return true; }
        }
        return false;
    }
}