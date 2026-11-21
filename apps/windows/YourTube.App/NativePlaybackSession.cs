using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;
using YourTube.Playback;

namespace YourTube.App;

// One owner for the adapter, player and element attachment. Always open and dispose on the UI thread.
internal sealed class NativePlaybackSession(NativePlaybackSource source, MediaPlayerElement element, PlaybackMedia? catalog = null) : IDisposable
{
    private readonly DispatcherQueue dispatcher = element.DispatcherQueue;
    private DispatcherQueueTimer? expiryTimer;
    private MediaPlayer? player;
    private const int FailedBit = 1;
    private const int RetiredBit = 2;
    private int state;
    private bool attached;
    private Action? restoreTransport;
    private bool IsRetired => (Volatile.Read(ref state) & RetiredBit) != 0;
    private readonly TaskCompletionSource<bool> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public PlaybackMedia Media => source.Media;
    public PlaybackMedia Catalog { get; } = catalog ?? source.Media;
    public bool IsOpened => opened.Task.IsCompletedSuccessfully && opened.Task.Result;
    public MediaPlayer Player => player ?? throw new InvalidOperationException("No active native player.");
    public MediaPlaybackItem Item { get; private set; } = null!;
    public bool HasFailed => source.HasFailed || (Volatile.Read(ref state) & FailedBit) != 0;
    public bool CallbackFailed => source.HasFailed;
    public int OutstandingDownloads => source.OutstandingDownloads;
    public int SuccessfulDownloads => source.SuccessfulDownloads;
    private bool playerClosedCleanly;
    public bool TeardownCompleted => playerClosedCleanly && source.TeardownCompleted;
    public event Action? Failed;

    public MediaPlayer Prepare(bool muted = false)
    {
        player = new MediaPlayer { AutoPlay = false, IsMuted = muted };
        return player;
    }

    public void Open(bool attach = true)
    {
        var current = Player;
        if (!attach) current.CommandManager.IsEnabled = false;
        source.Failed += ReportFailure;
        current.MediaFailed += MediaFailed;
        current.MediaOpened += MediaOpened;
        if (source.HasFailed) { ReportFailure(); return; }
        Item = new MediaPlaybackItem(source.Source);
        if (attach) Attach();
        current.Source = Item;
        expiryTimer = dispatcher.CreateTimer();
        expiryTimer.Interval = TimeSpan.FromSeconds(10);
        expiryTimer.Tick += (_, _) => { if (Media.ExpiresAt <= DateTimeOffset.UtcNow) ReportFailure(); };
        expiryTimer.Start();
    }

    private void Attach()
    {
        attached = true;
        element.SetMediaPlayer(Player);
    }

    private void MediaOpened(MediaPlayer sender, object args)
    {
        if (opened.TrySetResult(true))
            dispatcher.TryEnqueue(() => { if ((Volatile.Read(ref state) & RetiredBit) == 0 && !HasFailed) Ready?.Invoke(); });
    }

    public event Action? Ready;

    public async Task PrepareHandoffAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Prepare(muted: true);
        Open(attach: false);
        if (!await opened.Task.WaitAsync(token) || HasFailed)
            throw new InvalidOperationException("The quality source did not open.");
    }

    private async Task SeekForHandoffAsync(TimeSpan target, CancellationToken token)
    {
        var session = Player.PlaybackSession;
        if (target > TimeSpan.FromMilliseconds(250))
        {
            if (!session.CanSeek) throw new InvalidOperationException("The quality source cannot seek.");
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void SeekCompleted(MediaPlaybackSession sender, object args) => completed.TrySetResult();
            session.SeekCompleted += SeekCompleted;
            try
            {
                session.Position = target;
                var result = await Task.WhenAny(completed.Task, failed.Task).WaitAsync(token);
                if (result != completed.Task || HasFailed || (session.Position - target).Duration() > TimeSpan.FromSeconds(1))
                    throw new InvalidOperationException("The quality source did not reach the playback position.");
            }
            finally { session.SeekCompleted -= SeekCompleted; }
        }
    }

    public async Task ConfirmHandoffAsync(NativePlaybackSession previous, CancellationToken token, Action? accept = null)
    {
        CheckHandoff();
        var old = previous.Player;
        var replacement = Player;
        var oldSession = old.PlaybackSession;
        var session = replacement.PlaybackSession;
        if (!ReferenceEquals(element.MediaPlayer, old)) throw new PlaybackController<NativePlaybackSession>.QualityRollbackException();
        var commandsEnabled = old.CommandManager.IsEnabled;
        var transport = element.TransportControls;
        var transportEnabled = transport?.IsEnabled ?? false;
        var resume = oldSession.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Buffering;
        bool pauseRequested = false, attaching = false;
        try
        {
            if (transport is not null)
            {
                previous.restoreTransport = () => transport.IsEnabled = transportEnabled;
                transport.IsEnabled = false;
            }
            old.CommandManager.IsEnabled = false;
            if (resume)
            {
                pauseRequested = true;
                old.Pause();
                await UntilAsync(() => oldSession.PlaybackState == MediaPlaybackState.Paused);
            }

            // Sample only after the running clock is stopped, at any playback rate.
            var target = oldSession.Position;
            await SeekForHandoffAsync(target, token);
            CheckHandoff();
            if ((oldSession.Position - target).Duration() > TimeSpan.FromSeconds(1))
                throw new InvalidOperationException("Playback position changed during quality handoff.");

            replacement.Volume = old.Volume;
            replacement.IsMuted = old.IsMuted;
            session.PlaybackRate = oldSession.PlaybackRate;
            attaching = true;
            Attach();
            CheckHandoff();
            var start = session.Position;
            if (!ReferenceEquals(element.MediaPlayer, replacement) || (start - target).Duration() > TimeSpan.FromSeconds(1))
                throw new InvalidOperationException("The quality player did not attach at the playback position.");
            if (resume)
            {
                replacement.Play();
                await UntilAsync(() => session.PlaybackState == MediaPlaybackState.Playing &&
                    session.Position >= start + TimeSpan.FromMilliseconds(100));
            }
            else
            {
                await UntilAsync(() => session.PlaybackState == MediaPlaybackState.Paused &&
                    (session.Position - target).Duration() <= TimeSpan.FromSeconds(1));
            }
            if (!ReferenceEquals(element.MediaPlayer, replacement)) throw new InvalidOperationException("The quality player did not attach.");
            replacement.CommandManager.IsEnabled = commandsEnabled;
            previous.RestoreTransport();
            CheckHandoff();
            accept?.Invoke();
        }
        catch
        {
            // Navigation disposes the previous owner before canceling this operation. In that case,
            // its disposal already restored the controls; never reattach an obsolete player.
            if (!previous.IsRetired)
            {
                var restored = true;
                if (attaching)
                {
                    var silenced = NativePlaybackSource.Attempt(() => replacement.IsMuted = true);
                    restored &= NativePlaybackSource.Attempt(replacement.Pause) || silenced;
                    restored &= NativePlaybackSource.Attempt(() =>
                    {
                        if (!ReferenceEquals(element.MediaPlayer, old)) previous.Attach();
                        if (!ReferenceEquals(element.MediaPlayer, old)) throw new InvalidOperationException();
                    });
                }
                if (restored && pauseRequested)
                {
                    try
                    {
                        var recoveryStart = oldSession.Position;
                        old.Play();
                        // The failed switch's deadline may already be canceled. Verify recovery
                        // separately, but stop inspecting the old owner immediately on navigation.
                        var recoveryClock = System.Diagnostics.Stopwatch.StartNew();
                        while (!previous.IsRetired && !previous.HasFailed && ReferenceEquals(element.MediaPlayer, old))
                        {
                            if (oldSession.PlaybackState == MediaPlaybackState.Playing &&
                                oldSession.Position >= recoveryStart + TimeSpan.FromMilliseconds(100)) break;
                            if (recoveryClock.Elapsed >= TimeSpan.FromSeconds(3)) { restored = false; break; }
                            await Task.WhenAny(previous.failed.Task, Task.Delay(25));
                        }
                    }
                    catch { restored = false; }
                }
                if (previous.IsRetired) throw;
                restored &= ReferenceEquals(element.MediaPlayer, old);
                restored &= NativePlaybackSource.Attempt(() => old.CommandManager.IsEnabled = commandsEnabled);
                restored &= NativePlaybackSource.Attempt(previous.RestoreTransport);
                if (!restored || previous.HasFailed) throw new PlaybackController<NativePlaybackSession>.QualityRollbackException();
            }
            throw;
        }

        void CheckHandoff()
        {
            token.ThrowIfCancellationRequested();
            if (IsRetired || previous.IsRetired || HasFailed || previous.HasFailed)
                throw new InvalidOperationException("The quality handoff could not complete.");
        }

        async Task UntilAsync(Func<bool> ready)
        {
            while (true)
            {
                CheckHandoff();
                if (ready()) return;
                await Task.WhenAny(failed.Task, previous.failed.Task, Task.Delay(25, token));
            }
        }
    }

    private void RestoreTransport()
    {
        restoreTransport?.Invoke();
        restoreTransport = null;
    }

    private void MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        // A callback failure already has its own notification, not an incidental MediaFailed.
        if (!source.HasFailed) ReportFailure();
    }

    private void ReportFailure()
    {
        if (Interlocked.CompareExchange(ref state, FailedBit, 0) != 0) return;
        opened.TrySetResult(false);
        failed.TrySetResult();
        dispatcher.TryEnqueue(() => { if ((Volatile.Read(ref state) & RetiredBit) == 0) Failed?.Invoke(); });
    }

    public void Dispose()
    {
        if ((Interlocked.Or(ref state, RetiredBit) & RetiredBit) != 0) return;
        source.Failed -= ReportFailure;
        Failed = null;
        Ready = null;
        opened.TrySetResult(false);
        failed.TrySetResult();
        source.StopDownloads();
        playerClosedCleanly = NativePlaybackSource.Attempt(RestoreTransport)
            & NativePlaybackSource.Attempt(() => expiryTimer?.Stop());
        expiryTimer = null;
        var current = player; player = null;
        if (current is not null)
        {
            playerClosedCleanly &= NativePlaybackSource.Attempt(() => current.MediaFailed -= MediaFailed)
                & NativePlaybackSource.Attempt(() => current.MediaOpened -= MediaOpened)
                & NativePlaybackSource.Attempt(() => { if (current.PlaybackSession.CanPause) current.Pause(); })
                & NativePlaybackSource.Attempt(() => current.Source = null)
                & NativePlaybackSource.Attempt(() => { if (attached && ReferenceEquals(element.MediaPlayer, current)) element.SetMediaPlayer(null); })
                & NativePlaybackSource.Attempt(current.Dispose);
        }
        source.Dispose();
    }
}
