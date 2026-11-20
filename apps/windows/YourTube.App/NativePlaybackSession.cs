using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;
using YourTube.Playback;

namespace YourTube.App;

// One owner for the adapter, player and element attachment. Always open and dispose on the UI thread.
internal sealed class NativePlaybackSession(NativePlaybackSource source, MediaPlayerElement element) : IDisposable
{
    private readonly DispatcherQueue dispatcher = element.DispatcherQueue;
    private DispatcherQueueTimer? expiryTimer;
    private MediaPlayer? player;
    private const int FailedBit = 1;
    private const int RetiredBit = 2;
    private int state;
    public PlaybackMedia Media => source.Media;
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

    public void Open()
    {
        var current = Player;
        source.Failed += ReportFailure;
        current.MediaFailed += MediaFailed;
        if (source.HasFailed) { ReportFailure(); return; }
        Item = new MediaPlaybackItem(source.Source);
        element.SetMediaPlayer(current);
        current.Source = Item;
        expiryTimer = dispatcher.CreateTimer();
        expiryTimer.Interval = TimeSpan.FromSeconds(10);
        expiryTimer.Tick += (_, _) => { if (Media.ExpiresAt <= DateTimeOffset.UtcNow) ReportFailure(); };
        expiryTimer.Start();
    }

    private void MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        // A callback failure already has its own notification, not an incidental MediaFailed.
        if (!source.HasFailed) ReportFailure();
    }

    private void ReportFailure()
    {
        if (Interlocked.CompareExchange(ref state, FailedBit, 0) != 0) return;
        dispatcher.TryEnqueue(() => { if ((Volatile.Read(ref state) & RetiredBit) == 0) Failed?.Invoke(); });
    }

    public void Dispose()
    {
        if ((Interlocked.Or(ref state, RetiredBit) & RetiredBit) != 0) return;
        source.Failed -= ReportFailure;
        Failed = null;
        source.StopDownloads();
        playerClosedCleanly = NativePlaybackSource.Attempt(() => expiryTimer?.Stop());
        expiryTimer = null;
        var current = player; player = null;
        if (current is not null)
        {
            playerClosedCleanly &= NativePlaybackSource.Attempt(() => current.MediaFailed -= MediaFailed)
                & NativePlaybackSource.Attempt(() => { if (current.PlaybackSession.CanPause) current.Pause(); })
                & NativePlaybackSource.Attempt(() => current.Source = null)
                & NativePlaybackSource.Attempt(() => element.SetMediaPlayer(null))
                & NativePlaybackSource.Attempt(current.Dispose);
        }
        source.Dispose();
    }
}
