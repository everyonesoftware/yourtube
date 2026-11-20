using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Playback;
using YourTube.App;
using YourTube.Playback;

namespace YourTube.Playback.NativeValidation;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new ProofApplication();
        });
    }
}

public sealed partial class ProofApplication : Application, Microsoft.UI.Xaml.Markup.IXamlMetadataProvider
{
    private readonly Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider metadata = new();
    private Window? window;
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(Type type) => metadata.GetXamlType(type);
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(string name) => metadata.GetXamlType(name);
    public Microsoft.UI.Xaml.Markup.XmlnsDefinition[] GetXmlnsDefinitions() => metadata.GetXmlnsDefinitions();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        Resources["SystemAccentColor"] = Windows.UI.Color.FromArgb(255, 192, 215, 174);
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 28, 33, 31)) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var title = new TextBlock { Text = "YourTube · Native playback acceptance", FontSize = 22, Margin = new Thickness(22, 16, 22, 16), Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 237, 240, 233)) };
        var element = new MediaPlayerElement { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(element, 1);
        root.Children.Add(title);root.Children.Add(element);
        window = new Window { Title = "YourTube native playback proof", Content = root };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 760));
        root.Loaded += StartProof;
        window.Activate();

        async void StartProof(object sender, RoutedEventArgs args)
        {
            root.Loaded -= StartProof;
            await RunAsync(element, title);
        }
    }

    private async Task RunAsync(MediaPlayerElement element, TextBlock title)
    {
        var evidence = Environment.GetEnvironmentVariable("YOURTUBE_PLAYBACK_EVIDENCE");
        var sourceSha = Environment.GetEnvironmentVariable("YOURTUBE_PLAYBACK_SOURCE");
        if (string.IsNullOrWhiteSpace(evidence) || string.IsNullOrWhiteSpace(sourceSha) || sourceSha.Length != 40)
        {
            Environment.ExitCode = 2;window?.Close();return;
        }
        var results = new List<object>();
        var mediaById = new Dictionary<string, PlaybackMedia>();
        object? lifecycle = null;
        var success = false;
        string? activeVideo = null;
        var stage = "startup";
        try
        {
            using var resolver = new PlaybackResolver();
            foreach (var id in new[] { "jNQXAC9IVRw", "aqz-KE-bpKQ", "YE7VzlLtp-4" })
            {
                activeVideo = id;
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var token = timeout.Token;
                title.Text = "YourTube · Resolving public video " + id;
                stage = "resolve";
                var media = await resolver.ResolveAsync(id, token);
                mediaById.Add(id, media);
                var counted = new CountingResolver(resolver);
                stage = "create-source";
                using var native = new NativePlaybackSession(await NativePlaybackSource.CreateAsync(counted, media, token), element);
                stage = "open-media";
                var player = native.Prepare(muted: true);
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                player.MediaOpened += (_, _) => opened.TrySetResult();
                native.Open();
                await Until(() => opened.Task.IsCompletedSuccessfully, native, token);
                var item = native.Item;
                stage = "tracks";
                var session = player.PlaybackSession;
                await Until(() => session.NaturalVideoWidth > 0 && session.NaturalVideoHeight > 0 &&
                    item.AudioTracks.Count > 0 && item.VideoTracks.Count > 0, native, token);
                var duration = session.NaturalDuration.TotalSeconds;
                var width = session.NaturalVideoWidth;
                var height = session.NaturalVideoHeight;
                var audioTracks = item.AudioTracks.Count;
                var videoTracks = item.VideoTracks.Count;
                Require(duration > 12 && width > 0 && height > 0 && audioTracks > 0 && videoTracks > 0);
                stage = "advance";
                var initialPosition = session.Position.TotalSeconds;
                player.Play();
                await Until(() => session.PlaybackState == MediaPlaybackState.Playing && session.Position.TotalSeconds >= initialPosition + 1.2, native, token);
                stage = "pause";
                player.Pause();
                await Until(() => session.PlaybackState == MediaPlaybackState.Paused, native, token);
                var pauseStart = session.Position.TotalSeconds;
                await Task.Delay(600, token);
                var positionBefore = session.Position.TotalSeconds;
                var pausedAdvance = Math.Abs(positionBefore - pauseStart);
                Require(session.PlaybackState == MediaPlaybackState.Paused && pausedAdvance <= 0.35);
                stage = "seek";
                var target = Math.Min(8, duration / 2);
                Require(session.CanSeek && target >= positionBefore + 2);
                var seek = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void SeekCompleted(MediaPlaybackSession sender, object args) => seek.TrySetResult();
                session.SeekCompleted += SeekCompleted;
                try
                {
                    session.Position = TimeSpan.FromSeconds(target);
                    await Until(() => seek.Task.IsCompletedSuccessfully, native, token);
                }
                finally { session.SeekCompleted -= SeekCompleted; }
                await Until(() => session.PlaybackState == MediaPlaybackState.Paused, native, token);
                var landing = session.Position.TotalSeconds;
                Require(Math.Abs(landing - target) <= 0.75);
                await Task.Delay(600, token);
                var beforeResume = session.Position.TotalSeconds;
                var pausedAtLanding = session.PlaybackState == MediaPlaybackState.Paused;
                Require(!native.HasFailed && pausedAtLanding && Math.Abs(beforeResume - target) <= 0.75 && Math.Abs(beforeResume - landing) <= 0.35);
                player.Play();
                await Until(() => session.PlaybackState == MediaPlaybackState.Playing && session.Position.TotalSeconds >= Math.Max(target, beforeResume) + 1, native, token);
                var positionAfter = session.Position.TotalSeconds;
                stage = "render";
                title.Text = media.Title + " · Native audio/video · " + height + "p";
                Write(evidence, "current.json", new { videoId = id, stage = "playing", sourceSha });
                // Keep this actual decoded video visible for the PowerShell desktop screenshot.
                await Task.Delay(2500, token);
                Require(!native.HasFailed && counted.Reads > 0 && counted.Bytes > 0);
                stage = "teardown";
                native.Dispose();
                await Until(() => native.OutstandingDownloads == 0, native, token);
                Require(native.TeardownCompleted);
                results.Add(new { videoId = id, status = "passed", videoWidth = width, videoHeight = height, audioTracks, videoTracks, durationSeconds = duration, initialPosition, positionBeforeSeek = positionBefore, seekTarget = target, seekCompleted = seek.Task.IsCompletedSuccessfully, positionAtSeekLanding = landing, pausedAtSeekLanding = pausedAtLanding, positionBeforeResume = beforeResume, positionAfterSeek = positionAfter, pausedAdvance, resourceReads = counted.Reads, resourceBytes = counted.Bytes, disposed = native.TeardownCompleted });
                Write(evidence, "current.json", new { videoId = id, stage = "disposed", sourceSha });
            }
            activeVideo = null;
            stage = "native-lifecycle";
            title.Text = "YourTube · Checking native callback failure and replacement";
            lifecycle = await NativeLifecycleProof.RunAsync(element, resolver, mediaById, nextStage => stage = nextStage);
            success = true;
        }
        catch
        {
            // Never serialize exceptions, signed URLs, library responses or user/account data.
            title.Text = "Native playback acceptance failed.";
            results.Add(new { videoId = activeVideo, status = "failed", stage });
        }
        finally
        {
            Write(evidence, "proof.json", new { schemaVersion = 2, sourceSha, status = success ? "passed" : "failed", native = true, adapter = "NativePlaybackSource", authenticated = false, results, lifecycle });
            Environment.ExitCode = success ? 0 : 1;
            window?.Close();
        }
    }

    internal static async Task Until(Func<bool> predicate, NativePlaybackSession? native, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (native?.HasFailed == true) throw new InvalidOperationException("Native media failed.");
            if (predicate()) return;
            await Task.Delay(100, deadline.Token);
        }
    }
    internal static void Require(bool value) { if (!value) throw new InvalidOperationException("Native acceptance assertion failed."); }
    private static void Write(string directory, string name, object value)
    {
        var target = Path.Combine(directory, name);
        File.WriteAllText(target + ".new", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(target + ".new", target, true);
    }
}

internal sealed class CountingResolver(IPlaybackResolver inner) : IPlaybackResolver
{
    private long bytes;
    private int reads;
    public long Bytes => Interlocked.Read(ref bytes);
    public int Reads => Volatile.Read(ref reads);
    public Task<PlaybackMedia> ResolveAsync(string videoId, CancellationToken cancellationToken = default) => inner.ResolveAsync(videoId, cancellationToken);
    public async Task<PlaybackResource> ReadResourceAsync(PlaybackMedia media, Uri uri, ulong? offset, ulong? length, CancellationToken cancellationToken = default)
    {
        var resource = await inner.ReadResourceAsync(media, uri, offset, length, cancellationToken);
        Interlocked.Add(ref bytes, resource.Bytes.LongLength);Interlocked.Increment(ref reads);
        return resource;
    }
}
