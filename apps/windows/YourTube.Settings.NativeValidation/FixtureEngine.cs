using YourTube.Update;

namespace YourTube.Settings.NativeValidation;

// Only the package engine is substituted. The panel, state flow, and offer guards are production code.
internal sealed class FixtureEngine : IWindowsUpdateEngine
{
    public const string BaseSha = "1234567890123456789012345678901234567890";
    public const string HeadSha = "fedcba9876543210fedcba9876543210fedcba98";
    public static readonly WindowsChangeCommit[] Commits =
    [
        new("abcd1234567890abcdef1234567890abcdef1234", "feat(windows): improve fixture navigation"),
        new("123abcd4567890abcdef1234567890abcdef1234", "fix(windows): fix fixture focus"),
        new(HeadSha, "docs: fixture-only subject retained in details")
    ];
    public int Checks { get; private set; }
    public int NotesLoads { get; private set; }
    public int Downloads { get; private set; }
    public int Applies { get; private set; }
    public bool IsInstalled => true;
    public Version CurrentVersion => new(0, 1, 3);
    public Task<UpdateOffer?> CheckAsync()
    {
        Checks++;
        return Task.FromResult<UpdateOffer?>(new(new Version(0, 1, 9), WindowsGithubSource.PackageId,
            LoadChangelog: token =>
            {
                token.ThrowIfCancellationRequested();
                NotesLoads++;
                return Task.FromResult<WindowsChangelog?>(new(BaseSha, HeadSha,
                    ["Improve fixture navigation"], ["Fix fixture focus"], Commits));
            }));
    }
    public Task DownloadAsync(Action<int> progress, CancellationToken token)
    {
        Downloads++;
        throw new InvalidOperationException();
    }
    public void ApplyAndRestart()
    {
        Applies++;
        throw new InvalidOperationException();
    }
}