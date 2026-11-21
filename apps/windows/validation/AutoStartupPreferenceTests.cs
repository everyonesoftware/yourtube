using YourTube.App;
using YourTube.Playback;

internal static class AutoStartupPreferenceTests
{
    public static void Run()
    {
        PlaybackQuality[] catalog = [new(1080, 5000), new(720, 3000), new(720, 3500), new(480, 2700), new(360, 2800)];
        Check(AutoStartupPreference.Select(true, catalog, [5000, 3000, 3500, 2700, 2800, 128]) == new PlaybackQuality(720, 3500), "prefer 720p rather than 1080p");
        Check(AutoStartupPreference.Select(true, catalog, [5000, 3000, 2700, 2800]) == new PlaybackQuality(720, 3000), "only hint a bitrate Windows exposes");
        Check(AutoStartupPreference.Select(true, catalog, [5000, 2700, 2800, 128]) == new PlaybackQuality(480, 2700), "fallback by nearest lower height rather than bitrate");
        Check(AutoStartupPreference.Select(true, catalog, [5000, 128]) is null, "leave Windows in control if no matching 720p or lower bitrate exists");
        Check(AutoStartupPreference.Select(false, catalog, [3500, 2700]) is null, "manual selection does not set a startup preference");
        Check(AutoStartupPreference.Select(true, [new(1080, 3000), new(720, 3000), new(480, 2000)], [3000, 2000]) == new PlaybackQuality(480, 2000), "a bitrate shared with another height does not unambiguously identify the rendition");
        Check(AutoStartupPreference.Select(true, catalog, [5000, 3500, 2700], 480) == new PlaybackQuality(480, 2700), "configured 480p takes effect");
        Check(AutoStartupPreference.Select(true, catalog, [5000, 3500], 1080) == new PlaybackQuality(1080, 5000), "configured 1080p takes effect");
        Check(AutoStartupPreference.Select(true, catalog, [5000, 3500], 0) is null, "Windows default sets no hint");
        Check(AutoStartupPreference.Select(false, catalog, [5000, 3500], 1080) is null, "manual overrides configured 1080p");
        var folder = Path.Combine(Path.GetTempPath(), "yourtube-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "startup-quality.txt");
        try
        {
            Check(AutoStartupPreference.Load(path) == 720, "fresh profile defaults to 720p");
            Check(AutoStartupPreference.TrySave(path, 480) && AutoStartupPreference.Load(path) == 480, "480p survives restart");
            Check(AutoStartupPreference.TrySave(path, 0) && AutoStartupPreference.Load(path) == 0, "Windows default survives restart");
            Check(!AutoStartupPreference.TrySave(path, 999) && AutoStartupPreference.Load(path) == 0, "invalid save leaves saved choice intact");
            File.WriteAllText(path, "invalid");
            Check(AutoStartupPreference.Load(path) == 720, "corrupt setting defaults to 720p");
            Check(!AutoStartupPreference.TrySave(Path.Combine(path, "blocked.txt"), 480), "write failure does not crash Settings");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        Console.WriteLine("PASS: Auto prefers an unambiguous native 720p bitrate, falls back down by height, and leaves manual alone.");
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException("FAIL: " + message);
    }
}
