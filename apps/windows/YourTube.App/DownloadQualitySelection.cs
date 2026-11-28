using YourTube.Playback;

namespace YourTube.App;

internal sealed class DownloadQualitySelection(IReadOnlyDictionary<MediaExportFormat, (int? Height, int? Bitrate)> saved)
{
    private readonly Dictionary<MediaExportFormat, MediaExportOption> pending = [];

    public void Remember(MediaExportOption option) => pending[option.Format] = option;

    public int InitialIndex(MediaExportFormat format, MediaExportOption[] choices)
    {
        if (pending.TryGetValue(format, out var selected) && Array.IndexOf(choices, selected) is int pendingIndex and >= 0)
            return pendingIndex;
        int index = saved.TryGetValue(format, out var preference)
            ? Array.FindIndex(choices, choice => (choice.Height, choice.AudioBitrateKbps) == preference) : -1;
        return Math.Max(0, index);
    }
}
