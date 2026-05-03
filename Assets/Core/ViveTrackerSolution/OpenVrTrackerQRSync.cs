using UnityEngine;
using UnityEngine.Events;
using Valve.VR;

/// <summary>
/// Bridges Meta Quest QR scanning with the OpenVR tracker resync workflow.
///
/// IMPORTANT – USE A DEDICATED SimpleQRCalibrator INSTANCE:
///   This component MUST reference its OWN SimpleQRCalibrator, completely
///   separate from the one used for world / patient calibration.
///   Sharing the same calibrator would restart the world scan and move the
///   tracking space whenever the tracker is resynced.
///
///   Setup checklist:
///     1. Create a second SimpleQRCalibrator GameObject in the scene.
///     2. Set autoStart = FALSE on it.
///     3. Set payloadContainsFilter to a unique string in the TRACKER QR's text
///        (e.g. "TRACKER"), different from the patient/world QR.
///     4. Assign a dedicated empty-GO as its targetToPlace (the tracker anchor).
///     5. Point THIS component's qrCalibrator field at that second instance.
///     6. Print the tracker QR with that unique payload and tape it near the tracker.
///
/// Workflow:
///   1. Place the TRACKER QR physically on / next to the tracker's home position.
///   2. The user presses Sync (button / key / UI).
///   3. This component restarts its dedicated SimpleQRCalibrator scan.
///   4. Once the QR pose is finalized, trackerReader.syncReference is set to the
///      QR-derived world Transform and Resync() is called, locking the tracker
///      origin to that real-world position — fully independent of world calibration.
/// </summary>
public class OpenVrTrackerQRSync : MonoBehaviour
{
    // ── References ────────────────────────────────────────────
 

    [Tooltip("The OpenVrTrackerReader whose syncReference will be updated on sync.")]
    [SerializeField] private OpenVrTrackerReader trackerReader;


    [Tooltip("Keyboard fallback for editor / desktop testing.")]
    [SerializeField] private KeyCode syncKey = KeyCode.T;

    // ── Events ────────────────────────────────────────────────
    [Header("Events")]
    [Tooltip("Fired when the QR scan starts.")]
    public UnityEvent OnSyncStarted;

    [Tooltip("Fired when the QR scan finalized and the tracker was resynced.")]
    public UnityEvent OnSyncCompleted;

    [Tooltip("Fired when something went wrong (missing references, no targetToPlace, etc.).")]
    public UnityEvent OnSyncFailed;

    // ── Debug ─────────────────────────────────────────────────
    [Header("Debug")]
    [SerializeField] private bool isSyncing;

    // ── Internals ─────────────────────────────────────────────
    private bool _waitingForQR;

    // ── Lifecycle ─────────────────────────────────────────────
    private void OnEnable()
    {
       
    }

    private void OnDisable()
    {
       
        isSyncing = false;
    }


    // ── Public API ────────────────────────────────────────────

    /// <summary>
    /// Starts a QR scan. When the scan finalizes the tracker is automatically
    /// resynced relative to the QR-derived world pose.
    /// Safe to call from UI buttons or other scripts.
    /// </summary>
    public void SyncNow()
    {
        if (!ValidateRefs()) return;

        if (isSyncing)
        {
            Debug.Log("[TrackerQRSync] Previous scan still running — restarting.", this);
        }

        isSyncing = true;
        _waitingForQR = true;


        OnSyncStarted?.Invoke();
        Debug.Log("[TrackerQRSync] QR scan started — point the Quest camera at the QR code near the tracker.", this);
    }

 

    /// <summary>
    /// Cancels an in-progress sync scan without resyncing.
    /// </summary>
    public void CancelSync()
    {
        if (!isSyncing) return;

        _waitingForQR = false;
        isSyncing = false;
        Debug.Log("[TrackerQRSync] Sync cancelled.", this);
    }

    // ── Private ───────────────────────────────────────────────

    /// <summary>Called by SimpleQRCalibrator.OnFinalized.</summary>
    private void OnQRFinalized(Transform qrTransform)
    {
        // Only act on the scan WE started
        if (!_waitingForQR) return;

        _waitingForQR = false;
        isSyncing = false;

        ApplyTrackerResync(qrTransform);
    }

    /// <summary>
    /// Sets the tracker's syncReference to the QR-derived world Transform and
    /// calls Resync() to capture the tracker's raw SteamVR pose as the new origin.
    /// </summary>
    private void ApplyTrackerResync(Transform qrTransform)
    {
        if (trackerReader == null || qrTransform == null)
        {
            Debug.LogWarning("[TrackerQRSync] Cannot resync — trackerReader or qrTransform is null.", this);
            OnSyncFailed?.Invoke();
            return;
        }

        // The QR's world-space Transform becomes the tracker's physical reference point.
        // Resync() will record the tracker's current raw SteamVR pose at this moment,
        // so all subsequent tracker updates are expressed relative to this world pose.
        trackerReader.syncReference = qrTransform;
        trackerReader.Resync();

        OnSyncCompleted?.Invoke();
        Debug.Log($"[TrackerQRSync] ✓ Tracker resynced. " +
                  $"SyncReference → '{qrTransform.name}' " +
                  $"pos={qrTransform.position} rot={qrTransform.eulerAngles}", this);
    }

    private bool ValidateRefs()
    {

        Debug.LogWarning("[TrackerQRSync] Assign both qrCalibrator and trackerReader in the Inspector.", this);
        OnSyncFailed?.Invoke();
        return false;
    }
}
