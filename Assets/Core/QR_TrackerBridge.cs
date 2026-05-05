using UnityEngine;
using UnityEngine.UI;

public class QR_TrackerBridge : MonoBehaviour
{
    [SerializeField] SimpleQRCalibrator qr;
    [SerializeField] AlignChildToViveTarget alignment;
    [SerializeField] Transform trackerSyncPoint;
    [SerializeField] Vector3 syncPointOffset;

    [SerializeField] GameObject ui;
    [SerializeField] Image uiProgress;

    bool uiFlag = false;

    (Vector3, Quaternion)? savedQRscan;

    private void Start()
    {
        qr.OnQrScanFinished.AddListener(QRFinished);
    }

    private void LateUpdate()
    {
        if (qr.Progress > 0f && qr.Progress < 1f)
        {
            if (uiFlag == false)
            {
                ui.gameObject.SetActive(true);
                uiFlag = true;
            }
            uiProgress.fillAmount = qr.Progress;
        }
        else if (qr.Progress == 0f && uiFlag == true)
        {
            ui.gameObject.SetActive(false);
            uiFlag = false;
        }
    }

    void QRFinished(Vector3 position, Quaternion rotation)
    {
        savedQRscan = (position, rotation);
        Vector3 pos = position + syncPointOffset;
        trackerSyncPoint.SetPositionAndRotation(pos, rotation);
        alignment.AlignNow();
    }

    [ContextMenu("Reset sync point")]
    void ResetSyncPoint()
    {
        if (savedQRscan == null)
            return;

        Vector3 pos = savedQRscan.Value.Item1 + syncPointOffset;
        trackerSyncPoint.SetPositionAndRotation(pos, savedQRscan.Value.Item2);
        alignment.AlignNow();
    }
}
