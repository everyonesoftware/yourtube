using YourTube.Update;
namespace YourTube.App;

internal enum UpdatePhase { Idle, Checking, Current, Available, Downloading, Ready, Restarting, Error, NotInstalled }
internal sealed record UpdateState(UpdatePhase Phase, string Message, Version? Version = null, int Progress = 0, WindowsChangelog? Changelog = null, bool NotesLoading = false);

/// <summary>Panel-owned serialized state; independent of Core and Google credentials.</summary>
internal sealed class WindowsUpdateFlow : IDisposable
{
    private readonly IWindowsUpdateBackend backend;
    private readonly Action? beforeRestart;
    private readonly CancellationTokenSource lifetime = new();
    private int busy;
    private int startupCheckStarted;
    private object? activeDownload;
    private object? activeOffer;
    private bool disposed;
    private readonly object sync = new();
    private UpdateState state = new(UpdatePhase.Idle, "Not checked yet");
    public UpdateState State { get { lock (sync) return state; } }
    public event Action? Changed;
    public WindowsUpdateFlow(IWindowsUpdateBackend backend, Action? beforeRestart = null) { this.backend = backend; this.beforeRestart = beforeRestart; }
    private void Set(UpdateState next) { lock (sync) { if (disposed) return; state = next; Changed?.Invoke(); } }
    private bool Enter() { lock (sync) return !disposed && Interlocked.CompareExchange(ref busy, 1, 0) == 0; }
    public Task CheckOnStartupAsync() => Interlocked.Exchange(ref startupCheckStarted, 1) == 0
        ? CheckAsync(background: true) : Task.CompletedTask;

    public Task CheckAsync() => CheckAsync(background: false);

    private async Task CheckAsync(bool background)
    {
        if (!Enter()) return;
        try
        {
            if (State.Phase is UpdatePhase.Ready or UpdatePhase.Restarting) return;
            lock (sync)
            {
                activeOffer = null;
                Set(new(UpdatePhase.Checking, "Checking for updates…"));
            }
            // Reserve the same operation gate before scheduling startup work, including the lazy locator.
            var (installed, version) = await (background ? Task.Run(FindUpdateAsync) : FindUpdateAsync());
            if (!installed) Set(new(UpdatePhase.NotInstalled, "Install YourTube using its Windows installer to enable in-app updates."));
            else if (version is not null)
            {
                lock (sync)
                {
                    if (disposed) return;
                    activeOffer = new object();
                    Set(new(UpdatePhase.Available, "", version, NotesLoading: true));
                    _ = LoadNotesAsync(activeOffer);
                }
            }
            else Set(new(UpdatePhase.Current, "No newer Windows version is available."));
        }
        catch { Set(new(UpdatePhase.Error, "Could not check for updates. Try again later; GitHub may be unavailable or rate-limiting requests.")); }
        finally { Interlocked.Exchange(ref busy, 0); }
    }
    private async Task<(bool Installed, Version? Version)> FindUpdateAsync()
    {
        if (!backend.IsInstalled) return (false, null);
        var version = await backend.CheckAsync();
        return (true, version is not null && version > backend.CurrentVersion ? version : null);
    }
    public Task RetryNotesAsync()
    {
        lock (sync)
        {
            if (disposed || state.Phase != UpdatePhase.Available || state.NotesLoading || state.Changelog is not null || activeOffer is not { } offer)
                return Task.CompletedTask;
            Set(state with { NotesLoading = true });
            return LoadNotesAsync(offer);
        }
    }
    private async Task LoadNotesAsync(object offer)
    {
        WindowsChangelog? notes;
        try { notes = await backend.LoadChangelogAsync(lifetime.Token); }
        catch { notes = null; }
        lock (sync)
        {
            if (ReferenceEquals(activeOffer, offer) && state.Version is not null)
                Set(state with { Changelog = notes, NotesLoading = false });
        }
    }
    public async Task DownloadAsync()
    {
        if (!Enter()) return;
        try
        {
            var offer = State;
            if (offer.Phase != UpdatePhase.Available || offer.Version is not { } version) return;
            var downloadIdentity = new object();
            activeDownload = downloadIdentity;
            lock (sync) Set(state with { Phase = UpdatePhase.Downloading, Message = "Downloading update…" });
            await backend.DownloadAsync(version, value =>
            {
                lock (sync)
                {
                    if (ReferenceEquals(activeDownload, downloadIdentity) && state.Phase == UpdatePhase.Downloading)
                        Set(state with { Message = value >= 100 ? "Verifying update…" : "Downloading update…", Progress = Math.Clamp(value, 0, 100) });
                }
            }, lifetime.Token);
            lock (sync) Set(state with { Phase = UpdatePhase.Ready, Message = "Ready to install", Progress = 100 });
        }
        catch { Set(new(UpdatePhase.Error, "Download failed or was cancelled. Nothing was installed. Check for updates to retry.")); }
        finally { Interlocked.Exchange(ref busy, 0); }
    }
    public Task RestartAsync()
    {
        if (!Enter()) return Task.CompletedTask;
        var ready = State;
        try
        {
            if (ready.Phase != UpdatePhase.Ready || ready.Version is not { } version) return Task.CompletedTask;
            lock (sync) Set(state with { Phase = UpdatePhase.Restarting, Message = "Restarting…" });
            beforeRestart?.Invoke();
            if (!disposed) backend.ApplyAndRestart(version);
        }
        catch { lock (sync) Set(state with { Phase = UpdatePhase.Ready, Message = "Could not prepare or start the installer. Try Restart and install again." }); }
        finally { Interlocked.Exchange(ref busy, 0); }
        return Task.CompletedTask;
    }
    public void Dispose() { lock (sync) disposed = true; lifetime.Cancel(); }
}
