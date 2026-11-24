using System.Globalization;

namespace YourTube.App;

internal static class ReachabilityPreference
{
    public const int DefaultCount = 1;
    public static IReadOnlyList<int> Choices { get; } = Array.AsReadOnly(new[] { 1, 2, 4, 8, 16 });
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EveryoneSoftware", "YourTube", "reachability-checks.txt");

    public static int Load(string path)
    {
        try
        {
            using var reader = File.OpenText(path);
            Span<char> value = stackalloc char[3]; // More than two characters cannot be a saved choice.
            int length = reader.ReadBlock(value);
            if (length < value.Length && int.TryParse(value[..length], NumberStyles.None, CultureInfo.InvariantCulture, out int count) &&
                Choices.Contains(count)) return count;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return DefaultCount;
    }

    public static bool TrySave(string path, int count)
    {
        if (!Choices.Contains(count)) return false;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, count.ToString(CultureInfo.InvariantCulture));
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
}