using YourTube.App;

internal static class ReachabilityPreferenceTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YourTube-reachability-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "nested", "reachability-checks.txt");
        try
        {
            StateTests.Verify(ReachabilityPreference.Load(path) == 1, "a fresh profile uses sequential reachability checks");
            foreach (var count in new[] { 2, 4, 8, 16, 1 })
            {
                StateTests.Verify(ReachabilityPreference.TrySave(path, count), $"can save a reachability limit of {count}");
                StateTests.Verify(ReachabilityPreference.Load(path) == count, $"a new read restores the saved limit of {count}");
            }
            StateTests.Verify(ReachabilityPreference.TrySave(path, 8), "a nondefault preference can be saved before validation");
            foreach (var count in new[] { 0, 3, 17 })
                StateTests.Verify(!ReachabilityPreference.TrySave(path, count) && ReachabilityPreference.Load(path) == 8,
                    $"reject unsupported selection {count} without replacing the saved preference");
            foreach (var contents in new[] { "", "NaN", "-1", "17", "3", "4\n8", "1600000000000" })
            {
                File.WriteAllText(path, contents);
                StateTests.Verify(ReachabilityPreference.Load(path) == 1, "invalid stored values fall back to sequential checks");
            }
            StateTests.Verify(!ReachabilityPreference.TrySave(Path.Combine(path, "blocked.txt"), 4), "a filesystem failure cannot report a saved preference");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("PASS: reachability check limits persist locally and fail safely.");
    }
}