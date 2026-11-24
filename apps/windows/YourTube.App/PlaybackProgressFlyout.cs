using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using YourTube.Playback;

namespace YourTube.App;

internal sealed partial class MainWindow
{
    private static readonly string[] StartupLabels = ["Find video streams", "Prepare stream data", "Set up the player", "Load initial audio & video", "Start playback"];
    private static readonly string[] StartupNotes = [
        "Looking for compatible streams.", "Preparing the audio and video streams for Windows.",
        "Opening the source in the Windows player.", "Waiting for Windows to open the audio and video.",
        "Waiting for playback to begin advancing."
    ];
    private readonly TextBlock progressTotal = new() { FontSize = 12 };
    private readonly TextBlock progressSummary = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock progressNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button progressRetry = new() { Content = "Retry playback", Visibility = Visibility.Collapsed };
    private readonly List<StartupRow> progressRows = [];
    private DispatcherQueueTimer? progressClock;
    private bool playerProgressOpen;
    private Flyout PlayerProgressFlyout => (Flyout)C<Button>("PlayerProgressButton").Flyout;

    private void WireStartupEnd(NativePlaybackSession session) => session.Ended += () =>
    {
        if (closed || !ReferenceEquals(playback.Active, session)) return;
        playback.StartupTiming?.Mark(PlaybackTimingStage.NativePlaybackEnded);
        if (playerProgressOpen) RenderPlaybackProgress();
    };

    private void WirePlaybackProgress()
    {
        var feed = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = "Playback progress", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        header.Children.Add(title);
        progressTotal.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(progressTotal, 1);
        header.Children.Add(progressTotal);
        feed.Children.Add(header);
        AutomationProperties.SetLiveSetting(progressSummary, AutomationLiveSetting.Polite);
        feed.Children.Add(progressSummary);
        var rows = new StackPanel { Spacing = 2 };
        foreach (var label in StartupLabels)
        {
            var row = new StartupRow(label);
            progressRows.Add(row);
            rows.Children.Add(row.View);
        }
        feed.Children.Add(rows);
        feed.Children.Add(progressNote);
        feed.Children.Add(progressRetry);
        var scroll = new ScrollViewer { Content = feed, Width = 300, MaxHeight = 330, IsTabStop = true,
            HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(scroll, "Playback progress");
        var flyout = PlayerProgressFlyout;
        flyout.Content = scroll;
        progressClock = DispatcherQueue.CreateTimer();
        progressClock.Interval = TimeSpan.FromMilliseconds(200);
        progressClock.Tick += (_, _) => RenderPlaybackProgress();
        void SizeToWindow()
        {
            if (root.XamlRoot is not { } xamlRoot) return;
            scroll.Width = Math.Clamp(xamlRoot.Size.Width - 48, 120, 300);
            scroll.MaxHeight = Math.Clamp(xamlRoot.Size.Height - 90, 80, 330);
        }
        root.SizeChanged += (_, _) => { if (playerProgressOpen) SizeToWindow(); };
        flyout.Opening += (_, _) =>
        {
            playerProgressOpen = true;
            SizeToWindow();
            RenderPlaybackProgress();
            UpdatePlayerControlsVisibility();
        };
        flyout.Closed += (_, _) =>
        {
            playerProgressOpen = false;
            progressClock.Stop();
            if (!closed && playback.Selected is not null && !settingsVisible)
                lastPlayerActivity = Environment.TickCount64;
            UpdatePlayerControlsVisibility();
        };
        progressRetry.Click += async (_, _) =>
        {
            if (!playerProgressOpen || closed || settingsVisible || playback.Error is null) return;
            SyncPlaybackContext();
            await playback.RetryAsync();
        };
    }

    private void RenderPlaybackProgress()
    {
        if (closed || !playerProgressOpen || playback.StartupTiming?.ReadStartup() is not { } state) return;
        bool started = state.Completed == PlaybackStartupSnapshot.StepCount;
        SetText(progressSummary, state.Failed ? "Couldn’t start playback" : started ? "Playback started" :
            state.EndedUnconfirmed ? "Video ended" : $"{state.Completed} of {StartupLabels.Length} steps complete");
        SetText(progressTotal, Seconds(state.ElapsedMs) + (started ? " to start" : " elapsed"));
        bool canRetry = playback.Error is not null && !playback.Busy;
        SetText(progressNote, state.Failed ? playback.Error ?? "Try loading this video again." : started
            ? (canRetry ? "Playback started, then stopped. You can retry." : "Timing is for this startup attempt.")
            : state.EndedUnconfirmed ? "The video ended before startup timing could be confirmed." : StartupNotes[state.Completed]);
        if (!canRetry && progressRetry.FocusState != FocusState.Unfocused)
            ((ScrollViewer)PlayerProgressFlyout.Content).Focus(FocusState.Programmatic);
        progressRetry.Visibility = ToVisibility(canRetry);
        for (int i = 0; i < progressRows.Count; i++)
            progressRows[i].Render(i, state);
        if (state.IsFinal) progressClock?.Stop();
        else if (progressClock is { IsRunning: false }) progressClock.Start();
    }

    private static string Seconds(long milliseconds) => $"{milliseconds / 1000d:0.0} s";
    private static void SetText(TextBlock block, string text) { if (block.Text != text) block.Text = text; }

    private sealed class StartupRow
    {
        private static readonly SolidColorBrush ActiveBackground = new(Microsoft.UI.ColorHelper.FromArgb(255, 52, 69, 57));
        private static readonly SolidColorBrush FailedBackground = new(Microsoft.UI.ColorHelper.FromArgb(255, 69, 51, 46));
        private readonly string label;
        private readonly FontIcon mark = new() { FontSize = 11 };
        private readonly ProgressRing ring = new() { Width = 15, Height = 15, IsActive = false, Visibility = Visibility.Collapsed };
        private readonly TextBlock time = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        private string? status;
        public Grid View { get; } = new() { MinHeight = 34, ColumnSpacing = 8, Padding = new Thickness(5, 3, 5, 3), CornerRadius = new CornerRadius(4) };

        public StartupRow(string label)
        {
            this.label = label;
            View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            View.ColumnDefinitions.Add(new ColumnDefinition());
            View.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Grid { VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
            icon.Children.Add(mark);
            icon.Children.Add(ring);
            View.Children.Add(icon);
            var text = new TextBlock { Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            View.Children.Add(text);
            Grid.SetColumn(time, 2);
            View.Children.Add(time);
        }

        public void Render(int index, PlaybackStartupSnapshot snapshot)
        {
            bool active = index == snapshot.Completed && !snapshot.IsFinal;
            bool failed = index == snapshot.Completed && snapshot.Failed;
            bool done = index < snapshot.Completed;
            string next = active ? "In progress" : failed ? "Failed" : done ? "Complete" :
                snapshot.EndedUnconfirmed && index == snapshot.Completed ? "Not confirmed" : "Waiting";
            if (status != next)
            {
                status = next;
                AutomationProperties.SetName(View, $"{label}: {next}");
                View.Background = active ? ActiveBackground : failed ? FailedBackground : null;
                mark.Glyph = failed ? "\uE783" : done ? "\uE73E" : "\uEA3A";
                mark.Visibility = ToVisibility(!active);
                ring.Visibility = ToVisibility(active);
                ring.IsActive = active;
            }
            long? duration = snapshot.StepDurations[index];
            SetText(time, active && duration is long running ? Seconds(running) + " so far" :
                failed ? "Failed" : done && duration is long complete ? Seconds(complete) : next);
        }
    }
}
