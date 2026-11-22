namespace YourTube.App;

// Window-local preferences, applied before a replacement video can produce sound.
internal sealed class PlayerAudioState
{
    private double lastAudible = 0.25;
    internal double Volume { get; private set; } = 1;
    internal bool IsMuted { get; private set; }

    internal void SetVolume(double value)
    {
        Volume = Math.Clamp(value, 0, 1);
        if (Volume > 0)
        {
            lastAudible = Volume;
            IsMuted = false;
        }
    }

    internal void ToggleMute()
    {
        if (Volume == 0)
        {
            SetVolume(lastAudible);
            return;
        }
        IsMuted = !IsMuted;
    }
}
