using YourTube.Update;
namespace YourTube.App;

internal enum UpdatePhase { Idle, Checking, Current, Available, Downloading, Ready, Restarting, Error, NotInstalled }
internal sealed record UpdateState(UpdatePhase Phase, string Message, Version? Version = null, int Progress = 0);

/// <summary>Panel-owned serialized state; independent of Core and Google credentials.</summary>
internal sealed class WindowsUpdateFlow : IDisposable
{
    private readonly IWindowsUpdateBackend backend;
    private readonly Action? beforeRestart;
    private readonly CancellationTokenSource lifetime = new();
    private int busy;
    private object? activeDownload;
    private bool disposed;
    private readonly object sync = new();
    private UpdateState state = new(UpdatePhase.Idle, "Not checked yet");
    public UpdateState State { get { lock (sync) return state; } }
    public event Action? Changed;
    public WindowsUpdateFlow(IWindowsUpdateBackend backend, Action? beforeRestart = null) { this.backend = backend; this.beforeRestart = beforeRestart; }
    private void Set(UpdateState next) { lock (sync) { if (disposed) return; state = next; Changed?.Invoke(); } }
    private bool Enter() { lock (sync) return !disposed && Interlocked.CompareExchange(ref busy, 1, 0) == 0; }
    public async Task CheckAsync()
    {
        if (!Enter()) return;
        try
        {
            if (State.Phase is UpdatePhase.Ready or UpdatePhase.Restarting) return;
            if (!backend.IsInstalled) { Set(new(UpdatePhase.NotInstalled, "Install YourTube using its Windows installer to enable in-app updates.")); return; }
            Set(new(UpdatePhase.Checking, "Checking for updates…"));
            var version = await backend.CheckAsync();
            Set(version is not null && version > backend.CurrentVersion ? new(UpdatePhase.Available, "", version) : new(UpdatePhase.Current, "No newer Windows version is available."));
        }
        catch { Set(new(UpdatePhase.Error, "Could not check for updates. Try again later; GitHub may be unavailable or rate-limiting requests.")); }
        finally { Interlocked.Exchange(ref busy, 0); }
    }
    public async Task DownloadAsync()
    {
        if (!Enter()) return;
        try
        {
            if (State.Phase != UpdatePhase.Available || State.Version is not { } version) return;
            var downloadIdentity = new object();
            activeDownload = downloadIdentity;
            Set(new(UpdatePhase.Downloading, "Downloading update…", version));
            await backend.DownloadAsync(version, value =>
            {
                lock (sync)
                {
                    if (ReferenceEquals(activeDownload, downloadIdentity) && state.Phase == UpdatePhase.Downloading)
                        Set(new(UpdatePhase.Downloading, value >= 100 ? "Verifying update…" : "Downloading update…", version, Math.Clamp(value, 0, 100)));
                }
            }, lifetime.Token);
            Set(new(UpdatePhase.Ready, "Ready to install", version, 100));
        }
        catch { Set(new(UpdatePhase.Error, "Download failed or was cancelled. Nothing was installed. Check for updates to retry.")); }
        finally { Interlocked.Exchange(ref busy, 0); }
    }
    public Task RestartAsync()
    {
        if (!Enter()) return Task.CompletedTask;
        var version = State.Version;
        try
        {
            if (State.Phase != UpdatePhase.Ready || version is null) return Task.CompletedTask;
            Set(new(UpdatePhase.Restarting, "Restarting…", version, 100));
            beforeRestart?.Invoke();
            if (!disposed) backend.ApplyAndRestart(version);
        }
        catch { Set(new(UpdatePhase.Ready, "Could not prepare or start the installer. Try Restart and install again.", version, 100)); }
        finally { Interlocked.Exchange(ref busy, 0); }
        return Task.CompletedTask;
    }
    public void Dispose() { lock (sync) disposed = true; lifetime.Cancel(); }
}
