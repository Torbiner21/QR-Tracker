using UnityEngine;
//using SurgicalScience.TraumaVR.Features;

[DisallowMultipleComponent]
public class QRScanAudioFeedback : MonoBehaviour
{
    public AudioSource source;
    public AudioClip validScanClip;
    public AudioClip seriesCompleteClip;

    void Awake()
    {
        if (!source)
        {
            source = GetComponent<AudioSource>();
            if (source) source.playOnAwake = false;
        }
    }

    public void PlayOnValidScan(int currentValidCount)
    {
        //if (PauseManager.IsGamePaused) return;
        if (!validScanClip || !source) return;
        source.PlayOneShot(validScanClip);
    }

    public void PlayOnSeriesComplete()
    {
        //if (PauseManager.IsGamePaused) return;
        if (!seriesCompleteClip || !source) return;
        source.PlayOneShot(seriesCompleteClip);
    }
}