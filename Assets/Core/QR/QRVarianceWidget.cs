using System;
using TMPro;
using UnityEngine;

public class QRVarianceWidget : MonoBehaviour
{
    [SerializeField] TMP_Text text;
    string _seriesKey; // ie "idx:7"
    string _payload = "(no payload)";
    Guid _uuid;

    // cached panels
    string liveBlock; // updated every accepted cycle
    string finalBlock; // shown after final pose is decided
    bool hasFinal;

    public void SetIdentity(string seriesKey, Guid uuid)
    {
        _seriesKey = seriesKey;
        _uuid = uuid;
        Redraw();
    }

    // Live update shown after each accepted burst.
    // <param name="payload">Readable payload string.</param>
    // <param name="uuid">UUID of current/last anchor instance.</param>
    // <param name="nSamples">Total accepted samples aggregated for this series.</param>
    // <param name="stats">Stats bundle (reference = first pose in the series).</param>
    // <param name="lastPose">Pose captured in the most recent cycle.</param>
    public void SetStats(
        string payload, Guid uuid, int nSamples,
        (Vector3 meanPos, Vector3 stdPos, float meanRadial, float stdRadial,
         float meanAngleDeg, float stdAngleDeg, float maxAngleDeg, float maxDistance, Pose reference) stats,
        Pose lastPose)
    {
        if (!string.IsNullOrEmpty(payload)) _payload = payload;
        _uuid = uuid;

        // show last pose, dispersion, and angles (no ShiftSpace any more)
        Vector3 stdMm = stats.stdPos * 1000f;

        string line0 = $"{_seriesKey}  •  {_payload}";
        string line1 = $"Std (mm): ({stdMm.x:0.000},{stdMm.y:0.000},{stdMm.z:0.000})";
        string line2 = $"Angles (°): mean={stats.meanAngleDeg:0.000}  std={stats.stdAngleDeg:0.000}  max={stats.maxAngleDeg:0.000}";

        liveBlock = $"{line0}\n{line1}\n{line2}";
        Redraw();
    }

    // Final update after calibration completes (best robust pose).
    public void SetFinal(Pose finalPose, Vector3 stdPosMm, int nSamples)
    {
        var e = finalPose.rotation.eulerAngles;
        string f1 = $"pos(m)=({finalPose.position.x:0.000},{finalPose.position.y:0.000},{finalPose.position.z:0.000})";
        string f2 = $"rotEuler(°)=({e.x:0.0},{e.y:0.0},{e.z:0.0})   n={nSamples}";
        string f3 = $"stdPos(mm)=({stdPosMm.x:0.000},{stdPosMm.y:0.000},{stdPosMm.z:0.000})";

        finalBlock = $"{f1}\n{f2}\n{f3}";
        hasFinal = true;
        Redraw();
    }

    void Redraw()
    {
        if (!text) return;

        if (!hasFinal)
            text.text = liveBlock ?? _seriesKey;
        else
        {
            // live + final (once decided)
            if (!string.IsNullOrEmpty(liveBlock))
                text.text = liveBlock + "\n" + finalBlock;
            else
                text.text = _seriesKey + "\n" + finalBlock;
        }

        text.ForceMeshUpdate();
    }
}
