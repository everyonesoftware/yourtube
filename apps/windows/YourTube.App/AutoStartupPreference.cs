using System.Globalization;
using YourTube.Playback;

namespace YourTube.App;

internal static class AutoStartupPreference
{
    public const int DefaultHeight = 720;
    public static IReadOnlyList<int> Choices { get; } = Array.AsReadOnly(new[] { 0, 144, 240, 360, 480, 720, 1080 });
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EveryoneSoftware", "YourTube", "startup-quality.txt");

    public static int Load(string path)
    {
        try
        {
            using var reader = File.OpenText(path);
            Span<char> value = stackalloc char[8];
            int length = reader.ReadBlock(value);
            if (int.TryParse(value[..length], NumberStyles.None, CultureInfo.InvariantCulture, out int height) && Choices.Contains(height))
                return height;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return DefaultHeight;
    }

    public static bool TrySave(string path, int height)
    {
        if (!Choices.Contains(height)) return false;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, height.ToString(CultureInfo.InvariantCulture));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static PlaybackQuality? Select(bool auto, IReadOnlyList<PlaybackQuality> catalog, IEnumerable<uint> nativeBitrates, int height = DefaultHeight)
    {
        if (!auto || height == 0) return null;
        var exposed = nativeBitrates.ToHashSet();
        return catalog.Where(q => q.Height > 0 && q.Bitrate > 0 && exposed.Contains(q.Bitrate))
            .GroupBy(q => q.Bitrate)
            .Where(group => group.Select(q => q.Height).Distinct().Count() == 1)
            .Select(group => group.First())
            .Where(q => q.Height <= height)
            .OrderByDescending(q => q.Height).ThenByDescending(q => q.Bitrate)
            .FirstOrDefault();
    }
}
