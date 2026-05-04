using UnityEngine;
using Valve.VR;

public class OpenVrTrackerReader : MonoBehaviour
{
    [Header("Local Space Mode (use with AlignChildToTarget snap)")]
    public bool useLocalSpaceMode = false;

    [Header("Target")]
    public Transform trackerTarget;

    [Header("Reference Point (where tracker physically sits during resync)")]
    public Transform syncReference;

    [Header("TrackerRef")]
    public Transform trackerRef;

    [Header("Resync Key")]
    public KeyCode resyncKey = KeyCode.R;
    [Tooltip("If false, resync only fixes position. Rotation stays as-is so the ambu direction doesn't shift.")]
    public bool syncRotationOnResync = false;

    [Header("Your Fine-Tune Offset  (edit freely — never affected by world calibration)")]
    public Vector3 positionOffset = Vector3.zero;
    public Vector3 rotationOffset = Vector3.zero;

    [Header("World Calibration Offset  (auto-set by QR event — do not edit)")]
    [Tooltip("Drag the same 'trackingSpace' ScriptableObject asset used by QRCalibrationManager. " +
             "The tracker will automatically shift whenever the QR world calibration repositions the VR rig.")]
    public Transform trackingSpaceSO;
    [SerializeField] private Vector3 worldCalibPositionDebug;
    [SerializeField] private Vector3 worldCalibRotationDebug;

    [Header("Mounting Offset (tracker→Ambu pivot in local space)")]
    public Vector3 mountingPositionOffset = Vector3.zero;
    public Vector3 mountingRotationOffset = Vector3.zero;

    [Header("Axis Remapping - Position")]
    public TrackerAxis positiveX = TrackerAxis.OpenVR_X;
    public TrackerAxis positiveY = TrackerAxis.OpenVR_Y;
    public TrackerAxis positiveZ = TrackerAxis.OpenVR_NegZ;

    [Header("Axis Remapping - Rotation")]
    public TrackerAxis rotForward = TrackerAxis.OpenVR_NegZ;
    public TrackerAxis rotUp      = TrackerAxis.OpenVR_NegY;
    public TrackerAxis rotRight   = TrackerAxis.OpenVR_X;


    [Header("Local Space Mode - Axis Remapping Position")]
    public TrackerAxis localPositiveX = TrackerAxis.OpenVR_X;
    public TrackerAxis localPositiveY = TrackerAxis.OpenVR_Y;
    public TrackerAxis localPositiveZ = TrackerAxis.OpenVR_NegZ;

    [Header("Local Space Mode - Axis Remapping Rotation")]
    public TrackerAxis localRotForward = TrackerAxis.OpenVR_NegZ;
    public TrackerAxis localRotUp = TrackerAxis.OpenVR_NegY;
    public TrackerAxis localRotRight = TrackerAxis.OpenVR_X;

    [Header("HMD Sync (SteamVR HMD ↔ Meta Camera)")]
    [Tooltip("Enable the HMD-based sync option (uses Camera.main as the Meta/OpenXR headset).")]
    public bool enableHmdSync = true;
    [Tooltip("Key to trigger HMD-based sync.")]
    public KeyCode hmdSyncKey = KeyCode.H;
    [Tooltip("World-space Y added to the SteamVR tracker height when computing Meta scene position.\n" +
             "Adjust this if the calibrated tracker floats or sinks. " +
             "Zero = SteamVR standing floor (Y 0) matches Meta scene floor (Y 0).")]
    public float floorYOffset = 0f;
    [Tooltip("Only used in local space mode. The parent of trackerTarget will be snapped so the tracker aligns to Meta space.")]
    [SerializeField] private bool _hmdSynced = false;
    [SerializeField] private Vector3 _debugSteamHmdPos;
    [SerializeField] private Vector3 _debugMetaHmdPos;
    [SerializeField] private Vector3 _debugComputedTrackerWorldPos;

    [Header("Debug")]
    public Vector3 rawPosition;
    public Vector3 calibratedPosition;
    public Vector3 calibratedRotationEuler;


    public Vector3 straightPosition;
    public Vector3 straightRotation;
    public bool isSynced = false;
    [Tooltip("Leave empty to use the first tracker found. Set to a serial number or role substring (e.g. 'LHR-', 'handheld') to lock to a specific tracker.")]
    public string targetTrackerSerial = "";
    [SerializeField] private string detectedTrackerSerial = "";  // read-only, shows what's currently active

    // ── Public read-only pose (used by TrackerDeviceMount) ─────
    /// <summary>Current calibrated tracker world position.</summary>
    public Vector3 TrackerWorldPosition => calibratedPosition;
    /// <summary>Current calibrated tracker world rotation.</summary>
    public Quaternion TrackerWorldRotation { get; private set; } = Quaternion.identity;

    /// <summary>
    /// OpenVR tracker rotation converted to Unity space using the user's
    /// Inspector axis-remapping dropdowns — the same convention that OVRInput uses.
    /// Delta rotations computed from this are consistent with delta rotations from
    /// metaDevice.rotation, so the Kabsch axis-alignment is correct.
    /// </summary>
    public Quaternion rawRotationQ { get; private set; } = Quaternion.identity;

    /// <summary>
    /// OpenVR tracker position converted to Unity space using the user's
    /// Inspector axis-remapping dropdowns — same convention as rawRotationQ.
    /// </summary>
    public Vector3 calibRawPosition { get; private set; } = Vector3.zero;

    /// <summary>
    /// Tracker world position as produced by the existing SteamVR-Unity pipeline
    /// (sync-origin, syncReference, positionOffset, worldCalib)
    /// BEFORE any space-calibration profile is applied.
    /// Use this in SpaceCalibratorManager — it already represents the tracker in
    /// the SteamVR space the reader natively works in.
    /// </summary>
    public Vector3 preCalibratedPos { get; private set; } = Vector3.zero;

    /// <summary>
    /// Tracker world rotation as produced by the existing SteamVR-Unity pipeline
    /// BEFORE any space-calibration profile is applied.
    /// </summary>
    public Quaternion preCalibratedRot { get; private set; } = Quaternion.identity;

    // ── internals ──────────────────────────────────────────────
    private bool initialized = false;
    private Vector3 originPosition = Vector3.zero;
    private Quaternion originRotation = Quaternion.identity;

    // World calibration delta (accumulated across multiple QR calibrations)
    private Vector3 _worldCalibPos = Vector3.zero;
    private Quaternion _worldCalibRot = Quaternion.identity;

    // Space calibration profile — when set, raw SteamVR pose is transformed directly
    // into Meta world-space BEFORE any other processing.  Set by SpaceCalibratorManager.
    private ViveTrackerSolution.SpaceCalibration.CalibrationProfile _spaceCalibProfile;

    /// <summary>
    /// Inject a solved space-calibration profile.  Once set, every Update() tick
    /// the raw SteamVR tracker pose is converted to Meta world-space and written
    /// directly to trackerTarget — bypassing the sync-origin heuristic entirely.
    /// Pass null to revert to the legacy sync-origin behaviour.
    /// </summary>
    public void SetSpaceCalibration(ViveTrackerSolution.SpaceCalibration.CalibrationProfile profile)
    {
        _spaceCalibProfile = profile;

        if (profile != null)
        {
            // ── Snap calibration parent ─────────────────────────────────────────
            // The calibration is baked into the parent transform ONCE here.
            // Every Update() frame the child simply writes its raw SteamVR local
            // pose and Unity's hierarchy applies the offset automatically —
            // no per-frame matrix math, no snap artefacts on tracking re-acquisition.
            if (trackerTarget != null && trackerTarget.parent != null)
            {
                trackerTarget.parent.position = profile.Translation;
                trackerTarget.parent.rotation = profile.Rotation;
                Debug.Log($"[Tracker] Calibration parent snapped — " +
                          $"rot={profile.Rotation.eulerAngles:F1}  trans={profile.Translation:F3}", this);
            }
            else
            {
                Debug.LogWarning("[Tracker] SetSpaceCalibration: trackerTarget has no parent — " +
                                 "create a parent GameObject for trackerTarget so the calibration offset can be stored there.", this);
            }
        }
        else
        {
            Debug.Log("[Tracker] Space calibration profile cleared.", this);
        }
    }

    // Pre-calibration rig snapshot (captured just before VRCameraPositioner runs)
    private Vector3 _preCalibRigPos;
    private Quaternion _preCalibRigRot;
    private bool _pendingCalibCapture = false;

    // Last valid tracker pose from Update — used by SyncViaHmd to avoid a stale second query
    private HmdMatrix34_t _lastTrackerMatrix;
    private bool _lastTrackerMatrixValid = false;

    // Pre-world-calib tracker pose stored every Update frame — used by ApplyTrackerHandCalibration
    private Vector3    _lastPreCalibPos    = Vector3.zero;
    private Quaternion _lastPreCalibRawRot = Quaternion.identity;
    private bool       _lastPreCalibValid  = false;


    GameObject debugSphere;

    public enum TrackerAxis
    {
        OpenVR_X, OpenVR_NegX,
        OpenVR_Y, OpenVR_NegY,
        OpenVR_Z, OpenVR_NegZ
    }

    // ── lifecycle ──────────────────────────────────────────────
    void Start()
    {
       
        Invoke(nameof(InitOpenVR), 1.5f);
    }

    void InitOpenVR()
    {
        var err = EVRInitError.None;
        OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
        if (err != EVRInitError.None)
        {
            Debug.LogError($"[Tracker] Init failed: {err}");
            Invoke(nameof(InitOpenVR), 3f);
            return;
        }
        initialized = true;
        Debug.Log("[Tracker] Ready — press R to sync");

        // Auto-load any previously saved space-calibration profile.
        // This runs regardless of whether SpaceCalibratorManager has already pushed one,
        // so persistence is guaranteed even if execution order varies between play sessions.
        if (_spaceCalibProfile == null)
        {
            var saved = ViveTrackerSolution.SpaceCalibration.CalibrationProfile.Load();
            if (saved != null)
                SetSpaceCalibration(saved);
        }
    }

    void Update()
    {
        if (!initialized) return;
        if (Input.GetKeyDown(resyncKey)) Resync();
        if (Input.GetKeyDown(hmdSyncKey))
        {
            if (enableHmdSync)
                SyncViaHmd();
            else
                Debug.Log("[Tracker] HMD sync key pressed but enableHmdSync is OFF in the Inspector.", this);
        }
        //if (!isSynced) return;

        //if (debugSphere == null)
        //    debugSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);

        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(
            ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, poses);

        for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
        {
            if (!poses[i].bPoseIsValid || !poses[i].bDeviceIsConnected) continue;

            // Only accept Running_OK — reject Calibrating_OutOfRange, Running_OutOfRange
            // (IMU-predicted poses that will snap when optical tracking recovers).
            if (poses[i].eTrackingResult != ETrackingResult.Running_OK) continue;
            if (OpenVR.System.GetTrackedDeviceClass(i) != ETrackedDeviceClass.GenericTracker) continue;

            var m = poses[i].mDeviceToAbsoluteTracking;
            detectedTrackerSerial       = GetTrackerSerial(i);
            rawPosition                 = ExtractPosition(m);    // user axis-remap, legacy pipeline
            calibRawPosition            = ExtractPosition(m);    // same — user axis-remap matches Meta OVRInput convention
            rawRotationQ                = ExtractRotation(m);    // user axis-remap rotation — matches Meta OVRInput convention
            _lastTrackerMatrix          = m;
            _lastTrackerMatrixValid     = true;

            straightPosition = new Vector3(m.GetPosition().x, m.GetPosition().y, m.GetPosition().z);
            straightRotation = new Vector3(m.GetRotation().x, m.GetRotation().y, m.GetRotation().z);

            Vector3    finalPos;
            Quaternion finalRot;

            // ── Always compute the legacy pipeline output first ────────────────
            // This is the tracker's position/rotation in the SteamVR-Unity space
            // as the reader naturally produces it — axis-remapped, sync-origin applied,
            // offsets applied, world-calib applied.  SpaceCalibratorManager samples
            // preCalibratedPos/Rot so calibration operates in this same space.
            {
                var relPos = Quaternion.Inverse(originRotation) * (rawPosition - originPosition);
                var relRot = Quaternion.Inverse(originRotation) * ExtractRotation(m);

                var refPos = syncReference != null ? syncReference.position : Vector3.zero;
                var refRot = syncReference != null ? syncReference.rotation : Quaternion.identity;

                finalPos = refPos + refRot * relPos + positionOffset;
                finalRot = refRot * relRot * Quaternion.Euler(rotationOffset);

                // World calibration delta (QR-based — only used in legacy path)
                finalPos = _worldCalibRot * finalPos + _worldCalibPos;
                finalRot = _worldCalibRot * finalRot;
            }

            // Expose pre-calibration pose for the space calibrator to sample.
            preCalibratedPos = finalPos;
            preCalibratedRot = finalRot;

            if (_spaceCalibProfile != null)
            {
                // ── Space-calibration path ────────────────────────────────────────
                // calibRawPosition and rawRotationQ use the same user axis-remap
                // convention as Meta OVRInput — the solver was trained on these
                // exact values, so applying the profile here produces correct output.
                finalPos = _spaceCalibProfile.TransformPoint(calibRawPosition);
                finalRot = _spaceCalibProfile.TransformRotation(rawRotationQ);
            }

            // Store pre-calib pose for ApplyTrackerHandCalibration back-solve.
            _lastPreCalibPos    = preCalibratedPos;
            _lastPreCalibRawRot = rawRotationQ * Quaternion.Euler(rotationOffset);
            _lastPreCalibValid  = true;

            calibratedPosition = finalPos;
            calibratedRotationEuler = finalRot.eulerAngles;
            TrackerWorldRotation = finalRot;

            // finalRot already incorporates whichever path was taken above.
            var rawRot = finalRot;

            if (_spaceCalibProfile != null)
            {
                // Calibration parent holds the space offset — child tracks raw SteamVR
                // pose in local space.  Unity hierarchy does all the math; no per-frame
                // matrix multiply means no snap artefacts when tracking re-acquires.
                if (trackerTarget != null)
                {
                    trackerTarget.localPosition = calibRawPosition;
                    trackerTarget.localRotation = rawRotationQ;
                }
            }
            else if (useLocalSpaceMode)
            {
                // Legacy HMD-sync local-space mode (no space-calibration profile active).
                if (trackerTarget != null)
                {
                    trackerTarget.localPosition = rawPosition + positionOffset;
                    trackerTarget.localRotation = ExtractRotation(m) * Quaternion.Euler(rotationOffset);
                }
            }
            else
            {
                // World-space legacy path (no profile, no local mode).
                if (trackerTarget != null)
                {
                    trackerTarget.position = finalPos;
                    trackerTarget.rotation = rawRot;
                }
            }

           

            break;
        }

        //debugSphere.transform.position = TrackerWorldPosition;
    }

    /// <summary>
    /// HMD-based sync: uses Camera.main (Meta/OpenXR headset) and SteamVR HMD (index 0)
    /// as two known representations of the same physical point to resolve the
    /// SteamVR→Meta space transform.  The parent of trackerTarget is snapped so
    /// the tracker's local-space updates in Update() place it correctly in Meta world space.
    /// Works only when useLocalSpaceMode is true.
    /// </summary>
    [ContextMenu("Sync Via HMD")]
    public void SyncViaHmd()
    {
        Debug.Log("[Tracker] SyncViaHmd() entered.", this);

        if (!useLocalSpaceMode)
            Debug.LogWarning("[Tracker] SyncViaHmd: useLocalSpaceMode is OFF — " +
                             "parent snap will still run but may conflict with world-space Update writes.", this);

        if (Camera.main == null)
        {
            Debug.LogWarning("[Tracker] SyncViaHmd: Camera.main is null — cannot read Meta headset pose.", this);
            return;
        }

        if (trackerTarget == null || trackerTarget.parent == null)
        {
            Debug.LogWarning("[Tracker] SyncViaHmd: trackerTarget or its parent is null.", this);
            return;
        }

        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(
            ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, poses);

        // ── 1. SteamVR HMD (index 0) ──────────────────────────────────────────
        var hmdClass = OpenVR.System.GetTrackedDeviceClass(0);
        Debug.Log($"[Tracker] SyncViaHmd: device 0 class={hmdClass}  poseValid={poses[0].bPoseIsValid}  connected={poses[0].bDeviceIsConnected}", this);

        if (!poses[0].bPoseIsValid)
        {
            Debug.LogWarning("[Tracker] SyncViaHmd: SteamVR HMD pose is not valid.", this);
            return;
        }
        if (hmdClass != ETrackedDeviceClass.HMD)
            Debug.LogWarning($"[Tracker] SyncViaHmd: Device 0 is {hmdClass}, not HMD — results may be incorrect.", this);

        var hmdMatrix = poses[0].mDeviceToAbsoluteTracking;
        Vector3    steamHmdPos = ExtractRawPosition(hmdMatrix);
        Quaternion steamHmdRot = ExtractRawRotation(hmdMatrix);
        Debug.Log($"[Tracker] SyncViaHmd: steamHmdPos={steamHmdPos}  steamHmdRot={steamHmdRot.eulerAngles}", this);

        // ── 2. Meta / OpenXR HMD — raw tracking-space pose ────────────────────
        // Camera.main.transform.position is a *scene* world position: it is affected
        // by the XR Camera Rig's placement and can shift whenever the rig moves
        // (e.g. floor recalibration, rig repositioning, Guardian resets).
        // Instead we read the raw device position via the InputDevices API, which is
        // always relative to the Meta tracking origin (stable, floor-relative).
        // We then derive the "tracking → world" scene offset as:
        //   xrWorldOffset = Camera.main.transform.position − rawDevicePos
        // so that results are expressed in the same Unity world space as everything else.
        var _xrHeadDevices = new System.Collections.Generic.List<UnityEngine.XR.InputDevice>();
        UnityEngine.XR.InputDevices.GetDevicesAtXRNode(UnityEngine.XR.XRNode.Head, _xrHeadDevices);
        if (_xrHeadDevices.Count == 0)
        {
            Debug.LogWarning("[Tracker] SyncViaHmd: No XR head device found at XRNode.Head.", this);
            return;
        }
        Vector3    metaHeadRaw;
        Quaternion metaHeadRawRot;
        if (!_xrHeadDevices[0].TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition,  out metaHeadRaw) ||
            !_xrHeadDevices[0].TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation,  out metaHeadRawRot))
        {
            Debug.LogWarning("[Tracker] SyncViaHmd: Could not read XR head device pose.", this);
            return;
        }

        // Scene-space offset: how the tracking origin sits in Unity world space.
        Vector3 xrWorldOffset = Camera.main.transform.position - metaHeadRaw;

        Debug.Log($"[Tracker] SyncViaHmd: metaHeadRaw={metaHeadRaw}  metaHeadRawRot={metaHeadRawRot.eulerAngles}  xrWorldOffset={xrWorldOffset}", this);

        Debug.Log($"[Tracker] SyncViaHmd: searching for tracker (serial filter='{targetTrackerSerial}')...", this);
        // ── 3. Find target tracker — use cached matrix from last Update frame ─
        if (!_lastTrackerMatrixValid)
        {
            Debug.Log("[Tracker] SyncViaHmd: No cached tracker matrix yet — move the tracker so Update picks it up, then try again.", this);
            return;
        }
        HmdMatrix34_t trackerMatrix = _lastTrackerMatrix;
        Debug.Log("[Tracker] SyncViaHmd: using cached tracker matrix from Update.", this);

        try
        {
            // ── 4. Tracker pose ────────────────────────────────────────────────
            // For the GEOMETRY (space bridging), we need both HMD and tracker in the
            // same coordinate space. We use ExtractRawPosition/Rotation (standard
            // OpenVR→Unity: X=right, Y=up, Z=forward) for all geometry.
            // The axis-remapping dropdowns are only for the visual localPosition/localRotation.

            Vector3    steamTrackerPos = ExtractRawPosition(trackerMatrix);  // raw, same space as steamHmdPos
            Quaternion steamTrackerRot = ExtractRawRotation(trackerMatrix);  // raw, same space as steamHmdRot
            Debug.Log($"[Tracker] SyncViaHmd: steamTrackerPos={steamTrackerPos}  steamTrackerRot={steamTrackerRot.eulerAngles}", this);

            // ── 5. Express tracker in Meta/Unity world space ──────────────────
            // After OpenVR Space Calibrator, steamTrackerPos is in the same physical
            // tracking space as metaHeadRaw. We cannot just add xrWorldOffset
            // (translation only) because the XR rig may also have a rotation applied
            // (e.g. from guardian/room alignment). The full transform is:
            //   rigRot = Camera.main.rotation * Inverse(metaHeadRawRot)
            //   worldPos = Camera.main.pos + rigRot * (trackingPos - metaHeadRaw)
            // This is equivalent to xrRig.TransformPoint(trackingPos).

            Quaternion rigRot = Camera.main.transform.rotation * Quaternion.Inverse(metaHeadRawRot);
            Vector3 steamTrackerPosWithFloor = new Vector3(steamTrackerPos.x, steamTrackerPos.y + floorYOffset, steamTrackerPos.z);
            Vector3 metaTrackerWorld = Camera.main.transform.position + rigRot * (steamTrackerPosWithFloor - metaHeadRaw);

            Debug.Log($"[Tracker] SyncViaHmd: rigRot={rigRot.eulerAngles}  (should be yaw-only; x/z near 0 means rig has no tilt)");

            // Rotation: apply the full rig-space rotation to the tracker.
            // We derive it from flat-forward vectors to avoid Euler gimbal issues.
            Vector3 steamForwardFlat = Vector3.ProjectOnPlane(steamHmdRot     * Vector3.forward, Vector3.up).normalized;
            Vector3 metaForwardFlat  = Vector3.ProjectOnPlane(metaHeadRawRot  * Vector3.forward, Vector3.up).normalized;
            if (steamForwardFlat == Vector3.zero) steamForwardFlat = Vector3.forward;
            if (metaForwardFlat  == Vector3.zero) metaForwardFlat  = Vector3.forward;
            Quaternion yawDelta = Quaternion.Euler(0f, Quaternion.FromToRotation(steamForwardFlat, metaForwardFlat).eulerAngles.y, 0f);

            Quaternion steamTrackerRotRemapped = ExtractRotation(trackerMatrix);
            // Use rigRot (full rig yaw, 142° in this case) — NOT yawDelta (which is only
            // the live head orientation difference, ~16°). rigRot is what maps SteamVR
            // tracking space → Unity world space, same transform we already use for position.
            Quaternion metaTrackerRotRemapped  = rigRot * steamTrackerRotRemapped;

            Debug.Log($"[Tracker] SyncViaHmd: yawDelta={yawDelta.eulerAngles.y:F2}°  rigRot.y={rigRot.eulerAngles.y:F2}°  metaTrackerWorld={metaTrackerWorld}  metaTrackerRot={metaTrackerRotRemapped.eulerAngles}", this);

            // ── 6. Snap parent ─────────────────────────────────────────────────
            // Update() writes every frame:
            //   localPosition = ExtractPosition(m) + positionOffset   ← axis-remapped
            //   localRotation = ExtractRotation(m) * Euler(rotationOffset)
            //
            // We want: parent.position + parent.rotation * localPosition == metaTrackerWorld
            // So:      parentPos = metaTrackerWorld - parentRot * currentLocalPos
            //          parentRot = targetWorldRot * Inverse(currentLocalRot)
            //
            // This is purely algebraic — no coordinate space confusion because
            // both currentLocalPos and metaTrackerWorld are in Unity world space.

            Vector3    currentLocalPos = ExtractPosition(trackerMatrix) + positionOffset;
            Quaternion currentLocalRot = steamTrackerRotRemapped * Quaternion.Euler(rotationOffset);
            Quaternion targetWorldRot  = metaTrackerRotRemapped  * Quaternion.Euler(rotationOffset);
            Debug.Log($"[Tracker] SyncViaHmd: currentLocalPos={currentLocalPos}  currentLocalRot={currentLocalRot.eulerAngles}", this);

            Quaternion parentRot = targetWorldRot  * Quaternion.Inverse(currentLocalRot);
            Vector3    parentPos = metaTrackerWorld - parentRot * currentLocalPos;
            Debug.Log($"[Tracker] SyncViaHmd: parentPos={parentPos}  parentRot={parentRot.eulerAngles}  parent={trackerTarget.parent?.name ?? "NULL"}", this);

            trackerTarget.parent.SetPositionAndRotation(parentPos, parentRot);

            // ── Debug readout ──────────────────────────────────────────────────
            _hmdSynced = true;
            _debugSteamHmdPos            = steamHmdPos;
            _debugMetaHmdPos             = Camera.main.transform.position;
            _debugComputedTrackerWorldPos = metaTrackerWorld;

            Debug.Log($"[Tracker] SyncViaHmd complete.\n" +
                      $"  SteamVR HMD pos  : {steamHmdPos}\n" +
                      $"  Meta HMD raw     : {metaHeadRaw}\n" +
                      $"  rigRot           : {rigRot.eulerAngles}  (x/z near 0 = clean yaw-only rig)\n" +
                      $"  yawDelta         : {yawDelta.eulerAngles.y:F2}°\n" +
                      $"  Computed tracker : {metaTrackerWorld}  rot={metaTrackerRotRemapped.eulerAngles}\n" +
                      $"  Parent snapped   : pos={parentPos}  rot={parentRot.eulerAngles}", this);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Tracker] SyncViaHmd: exception caught — {ex}", this);
        }
    }

    /// <summary>Dumps every tracked device slot to the Console so you can see what OpenVR is actually reporting.</summary>
    [ContextMenu("Dump All OpenVR Devices")]
    public void DumpAllOpenVRDevices()
    {
        if (!initialized) { Debug.LogWarning("[Tracker] Not initialized yet.", this); return; }

        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(
            ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, poses);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[Tracker] === OpenVR Device Dump ===");
        for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
        {
            var cls = OpenVR.System.GetTrackedDeviceClass(i);
            if (cls == ETrackedDeviceClass.Invalid && !poses[i].bDeviceIsConnected) continue;

            var errProp = ETrackedPropertyError.TrackedProp_Success;
            var serialSb = new System.Text.StringBuilder(64);
            OpenVR.System.GetStringTrackedDeviceProperty(i,
                ETrackedDeviceProperty.Prop_SerialNumber_String, serialSb, 64, ref errProp);

            var pos = ExtractRawPosition(poses[i].mDeviceToAbsoluteTracking);
            sb.AppendLine($"  [{i:D2}] class={cls,-20} connected={poses[i].bDeviceIsConnected}  poseValid={poses[i].bPoseIsValid}  pos={pos}  serial={serialSb}");
        }
        Debug.Log(sb.ToString(), this);
    }

    [ContextMenu("Resync Tracker")]
    public void Resync()
    {
        syncReference = trackerRef != null ? trackerRef : null;
        Debug.Log($"[Tracker] Resyncing to '{syncReference?.name ?? "null"}'... {trackerRef.position}" +
                  $"Make sure the tracker is physically positioned at the sync reference, then press R.", this);
        if (syncReference == null)
        {
            Debug.LogWarning("[Tracker] No sync reference assigned!");
            return;
        }

        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(
            ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, poses);

        for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
        {
            if (!poses[i].bPoseIsValid || !poses[i].bDeviceIsConnected) continue;
            if (OpenVR.System.GetTrackedDeviceClass(i) != ETrackedDeviceClass.GenericTracker) continue;
            if (!IsTargetTracker(i)) continue;

            var m = poses[i].mDeviceToAbsoluteTracking;
            originPosition = ExtractPosition(m);
            if (syncRotationOnResync)
                originRotation = ExtractRotation(m);
            isSynced = true;

            Debug.Log($"[Tracker] Synced to '{syncReference.name}'");
            break;
        }
    }

    // ── helpers ────────────────────────────────────────────────
    private string GetTrackerSerial(uint deviceIndex)
    {
        var err = ETrackedPropertyError.TrackedProp_Success;
        var sb = new System.Text.StringBuilder(64);
        OpenVR.System.GetStringTrackedDeviceProperty(deviceIndex,
            ETrackedDeviceProperty.Prop_SerialNumber_String, sb, 64, ref err);
        return sb.ToString();
    }

    private bool IsTargetTracker(uint deviceIndex)
    {
        if (string.IsNullOrEmpty(targetTrackerSerial)) return true;  // no filter — use first found
        var serial = GetTrackerSerial(deviceIndex);
        return serial.IndexOf(targetTrackerSerial, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private Vector3 ExtractPosition(HmdMatrix34_t m) =>
     new Vector3(
         GetAxis(m, useLocalSpaceMode ? localPositiveX : positiveX),
         GetAxis(m, useLocalSpaceMode ? localPositiveY : positiveY),
         GetAxis(m, useLocalSpaceMode ? localPositiveZ : positiveZ));

    private Quaternion ExtractRotation(HmdMatrix34_t m)
    {
        var forward = GetAxisVector(m, useLocalSpaceMode ? localRotForward : rotForward);
        var up = GetAxisVector(m, useLocalSpaceMode ? localRotUp : rotUp);
        var right = GetAxisVector(m, useLocalSpaceMode ? localRotRight : rotRight);
        if (forward == Vector3.zero || up == Vector3.zero) return Quaternion.identity;
        // Orthogonalize: forward is authoritative, re-derive up from forward x right
        // so floating-point imprecision in the raw matrix never causes jitter.
        if (right != Vector3.zero)
        {
            var reorthogonalizedUp = Vector3.Cross(Vector3.Cross(forward, right), forward).normalized;
            if (reorthogonalizedUp != Vector3.zero)
                up = reorthogonalizedUp;
        }
        return Quaternion.LookRotation(forward, up);
    }

    private float GetAxis(HmdMatrix34_t m, TrackerAxis axis)
    {
        switch (axis)
        {
            case TrackerAxis.OpenVR_X: return m.m3;
            case TrackerAxis.OpenVR_NegX: return -m.m3;
            case TrackerAxis.OpenVR_Y: return m.m7;
            case TrackerAxis.OpenVR_NegY: return -m.m7;
            case TrackerAxis.OpenVR_Z: return m.m11;
            case TrackerAxis.OpenVR_NegZ: return -m.m11;
            default: return 0f;
        }
    }

    /// <summary>Raw OpenVR→Unity position — not affected by axis remapping dropdowns.</summary>
    private Vector3 ExtractRawPosition(HmdMatrix34_t m) =>
        new Vector3(m.m3, m.m7, -m.m11);

    /// <summary>Fixed OpenVR→Unity rotation — not affected by axis remapping dropdowns.</summary>
    private Quaternion ExtractRawRotation(HmdMatrix34_t m)
    {
        var forward = new Vector3(-m.m2, -m.m6,  m.m10);  // OpenVR -Z col → Unity forward
        var up      = new Vector3( m.m1,  m.m5, -m.m9);   // OpenVR  Y col → Unity up
        if (forward == Vector3.zero || up == Vector3.zero) return Quaternion.identity;
        return Quaternion.LookRotation(forward, up);
    }

    private Vector3 GetAxisVector(HmdMatrix34_t m, TrackerAxis axis)
    {
        switch (axis)
        {
            case TrackerAxis.OpenVR_NegZ: return new Vector3(-m.m2, -m.m6, m.m10);
            case TrackerAxis.OpenVR_Z: return new Vector3(m.m2, m.m6, -m.m10);
            case TrackerAxis.OpenVR_NegY: return new Vector3(-m.m1, -m.m5, m.m9);
            case TrackerAxis.OpenVR_Y: return new Vector3(m.m1, m.m5, -m.m9);
            case TrackerAxis.OpenVR_NegX: return new Vector3(-m.m0, -m.m4, m.m8);
            case TrackerAxis.OpenVR_X: return new Vector3(m.m0, m.m4, -m.m8);
            default: return Vector3.forward;
        }
    }

    void OnDestroy()
    {
        if (initialized) OpenVR.Shutdown();
    }

    // Allows manually editing world calib debug fields in the Inspector to take effect immediately.
    void OnValidate()
    {
        _worldCalibPos = worldCalibPositionDebug;
        _worldCalibRot = Quaternion.Euler(worldCalibRotationDebug);
    }

    // ── LateUpdate — capture rig delta after VRCameraPositioner ran ────
    void LateUpdate()
    {
        if (!_pendingCalibCapture) return;
        var rig = trackingSpaceSO != null ? trackingSpaceSO : null;
        if (rig == null) { _pendingCalibCapture = false; return; }
        _pendingCalibCapture = false;

        // Delta = how much the rig moved this calibration
        var deltaPos = rig.transform.position - _preCalibRigPos;
        var deltaRot = rig.transform.rotation * Quaternion.Inverse(_preCalibRigRot);

        // Accumulate (supports multiple re-calibrations without resetting your offset)
        _worldCalibPos = deltaRot * _worldCalibPos + deltaPos;
        _worldCalibRot = deltaRot * _worldCalibRot;

        // Mirror to inspector for visibility (read-only)
        worldCalibPositionDebug = _worldCalibPos;
        worldCalibRotationDebug = _worldCalibRot.eulerAngles;

        Debug.Log($"[Tracker] World calibration offset updated: " +
                  $"pos={_worldCalibPos}  rot={_worldCalibRot.eulerAngles}", this);
    }

    /// <summary>
    /// Back-solves and sets the world calibration so the tracker's next output
    /// exactly matches <paramref name="desiredWorldPos"/> / <paramref name="desiredWorldRot"/>.
    ///
    /// Call this AFTER the virtual model has been correctly positioned by hand calibration:
    ///   1. tracker sync-child world pose = correct tracker target position in world space
    ///   2. Pass that pose here → world calib is updated so tracking continues correctly.
    /// </summary>
    public void ApplyTrackerHandCalibration(Vector3 desiredWorldPos, Quaternion desiredWorldRot)
    {
        if (!_lastPreCalibValid)
        {
            Debug.LogWarning("[Tracker] ApplyTrackerHandCalibration: no tracker frame received yet — " +
                             "make sure the tracker is visible and Update() has run at least once.", this);
            return;
        }

        // Desired = newCalibRot * _lastPreCalibRawRot
        // → newCalibRot = desiredRot * Inverse(preCalibRawRot)
        Quaternion newCalibRot = desiredWorldRot * Quaternion.Inverse(_lastPreCalibRawRot);

        // Desired = newCalibRot * _lastPreCalibPos + newCalibPos
        // → newCalibPos = desiredPos - newCalibRot * preCalibPos
        Vector3 newCalibPos = desiredWorldPos - newCalibRot * _lastPreCalibPos;

        _worldCalibRot           = newCalibRot;
        _worldCalibPos           = newCalibPos;
        worldCalibPositionDebug  = newCalibPos;
        worldCalibRotationDebug  = newCalibRot.eulerAngles;

        Debug.Log($"[Tracker] ApplyTrackerHandCalibration:\n" +
                  $"  desiredPos={desiredWorldPos:F3}  desiredRot={desiredWorldRot.eulerAngles:F2}\n" +
                  $"  newCalibRot={newCalibRot.eulerAngles:F2}  newCalibPos={newCalibPos:F3}", this);
    }

    /// <summary>Resets the accumulated world calibration offset back to zero.</summary>
    [ContextMenu("Reset World Calibration Offset")]
    public void ResetWorldCalibOffset()
    {
        _worldCalibPos = Vector3.zero;
        _worldCalibRot = Quaternion.identity;
        worldCalibPositionDebug = Vector3.zero;
        worldCalibRotationDebug = Vector3.zero;
        Debug.Log("[Tracker] World calibration offset cleared.", this);
    }
}