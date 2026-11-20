namespace YourTube.Update;

public interface IWindowsUpdateBackend
{
    bool IsInstalled { get; }
    Version CurrentVersion { get; }
    Task<WindowsChangelog?> LoadChangelogAsync(CancellationToken token) => Task.FromResult<WindowsChangelog?>(null);
    Task<Version?> CheckAsync();
    Task DownloadAsync(Version version, Action<int> progress, CancellationToken token);
    void ApplyAndRestart(Version version);
}

public interface IWindowsUpdateEngine
{
    bool IsInstalled { get; }
    Version CurrentVersion { get; }
    Task<UpdateOffer?> CheckAsync();
    Task DownloadAsync(Action<int> progress, CancellationToken token);
    void ApplyAndRestart();
}
public sealed record UpdateOffer(Version Version, string PackageId, bool IsDowngrade = false,
    Func<CancellationToken, Task<WindowsChangelog?>>? LoadChangelog = null);
public sealed record WindowsChangeCommit(string Sha, string Subject);
public sealed record WindowsChangelog(string BaseRevision, string HeadRevision, IReadOnlyList<string> Improvements, IReadOnlyList<string> Fixes, IReadOnlyList<WindowsChangeCommit> Commits);

/// <summary>State guard around the package engine. Caller must serialize operations.</summary>
public sealed class WindowsUpdateBackend : IWindowsUpdateBackend
{
    private readonly Lazy<IWindowsUpdateEngine> engine;
    public WindowsUpdateBackend(Func<IWindowsUpdateEngine>? factory = null, string? installedSourceSha = null) => engine = new(factory ?? (() => new VelopackUpdateEngine(new WindowsGithubSource(), installedSourceSha: installedSourceSha)));
    public Task<WindowsChangelog?> LoadChangelogAsync(CancellationToken token) =>
        offered?.LoadChangelog?.Invoke(token) ?? Task.FromResult<WindowsChangelog?>(null);
    public bool IsInstalled => engine.Value.IsInstalled;
    public Version CurrentVersion => engine.Value.CurrentVersion;
    public static Velopack.UpdateOptions CreateOptions() => new() { ExplicitChannel = WindowsGithubSource.Channel, AllowVersionDowngrade = false, MaximumDeltasBeforeFallback = -1 };
    private UpdateOffer? offered;
    private bool downloaded;
    public async Task<Version?> CheckAsync()
    {
        offered = null; downloaded = false;
        var offer = await engine.Value.CheckAsync();
        if (offer is not null && (offer.Version <= CurrentVersion || offer.IsDowngrade || offer.PackageId != WindowsGithubSource.PackageId))
            throw new InvalidDataException("Unsafe Windows update target.");
        offered = offer;
        return offered?.Version;
    }
    private void RequireOffer(Version version)
    {
        if (offered?.Version != version || version <= CurrentVersion) throw new InvalidOperationException("No matching newer Windows update.");
    }
    public async Task DownloadAsync(Version version, Action<int> progress, CancellationToken token)
    {
        downloaded = false;
        RequireOffer(version);
        await engine.Value.DownloadAsync(progress, token);
        token.ThrowIfCancellationRequested();
        downloaded = true;
    }
    public void ApplyAndRestart(Version version)
    {
        RequireOffer(version);
        if (!downloaded) throw new InvalidOperationException("Download the Windows update first.");
        engine.Value.ApplyAndRestart();
    }
}
internal sealed class VelopackUpdateEngine : IWindowsUpdateEngine
{
    private readonly WindowsGithubSource source;
    private readonly string? installedSourceSha;
    private readonly Velopack.UpdateManager manager;
    internal VelopackUpdateEngine(WindowsGithubSource source, Velopack.UpdateManager? manager = null, string? installedSourceSha = null)
    {
        this.source = source;
        this.installedSourceSha = installedSourceSha;
        this.manager = manager ?? new(source, WindowsUpdateBackend.CreateOptions());
    }
    private Velopack.UpdateInfo? offered;
    public bool IsInstalled => manager.IsInstalled;
    public Version CurrentVersion => Version.Parse(manager.CurrentVersion?.ToString() ?? "0.0.0");
    public async Task<UpdateOffer?> CheckAsync()
    {
        offered = null;
        offered = await manager.CheckForUpdatesAsync();
        if (offered is not { } update) return null;
        var target = update.TargetFullRelease;
        var version = Version.Parse(target.Version.ToString());
        var installed = CurrentVersion;
        return new(version, target.PackageId, update.IsDowngrade,
            update.IsDowngrade ? null : token => source.GetChangelogAsync(installed, installedSourceSha, target, token));
    }
    public Task DownloadAsync(Action<int> progress, CancellationToken token) => manager.DownloadUpdatesAsync(offered ?? throw new InvalidOperationException("No update offer."), progress, token);
    public void ApplyAndRestart() => manager.ApplyUpdatesAndRestart((offered ?? throw new InvalidOperationException("No update offer.")).TargetFullRelease, Array.Empty<string>());
}
