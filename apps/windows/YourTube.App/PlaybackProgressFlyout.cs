using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using YourTube.Playback;

namespace YourTube.App;

internal sealed partial class MainWindow
{
    private static readonly Version AppVersion = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
    private static readonly string[] StartupNotes = [
        "Looking for compatible streams.", "Preparing the audio and video streams for Windows.",
        "Opening the source in the Windows player.", "Waiting for Windows to open the audio and video.",
        "Waiting for playback to begin advancing."
    ];
    private readonly TextBlock progressTotal = new() { FontSize = 12 };
    private readonly TextBlock progressSummary = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock progressNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly TextBlock reachabilityDetails = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(29, 0, 5, 4) };
    private readonly Button progressRetry = new() { Content = "Retry playback", Visibility = Visibility.Collapsed };
    private readonly Button progressCopy = new() { Content = "Copy startup summary", IsEnabled = false };
    private readonly TextBlock copyFeedback = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly List<StartupRow> progressRows = [];
    private readonly List<StartupRow> discoveryRows = [];
    private readonly ToggleButton discoveryToggle = new() { Padding = new Thickness(0), BorderThickness = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private PlaybackTiming? displayedStartup;
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
        var feed = new StackPanel { Spacing = 6 };
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
        var rows = new StackPanel { Spacing = 1 };
        foreach (var label in PlaybackProgressSummary.StartupLabels)
        {
            bool disclosure = progressRows.Count == 0;
            var row = new StartupRow(label, disclosure: disclosure);
            progressRows.Add(row);
            if (!disclosure) { rows.Children.Add(row.View); continue; }
            discoveryToggle.Content = row.View;
            discoveryToggle.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            AutomationProperties.SetName(discoveryToggle, label);
            AutomationProperties.SetAccessibilityView(row.View, AccessibilityView.Raw);
            rows.Children.Add(discoveryToggle);
            var details = new StackPanel();
            foreach (var detailLabel in PlaybackProgressSummary.DiscoveryLabels)
            {
                var child = new StartupRow(detailLabel, detail: true);
                discoveryRows.Add(child);
                details.Children.Add(child.View);
                if (discoveryRows.Count - 1 == (int)StreamDiscoveryStep.Reachability)
                    details.Children.Add(reachabilityDetails);
            }
            var branch = new Border { Child = details, Margin = new Thickness(17, 0, 0, 4),
                Padding = new Thickness(8, 0, 0, 0), BorderThickness = new Thickness(1, 0, 0, 0),
                BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(100, 144, 144, 144)) };
            void ShowDiscovery(bool expanded)
            {
                branch.Visibility = ToVisibility(expanded);
                row.SetExpanded(expanded);
            }
            discoveryToggle.Checked += (_, _) => ShowDiscovery(true);
            discoveryToggle.Unchecked += (_, _) => ShowDiscovery(false);
            ShowDiscovery(false);
            rows.Children.Add(branch);
        }
        feed.Children.Add(rows);
        feed.Children.Add(progressNote);
        feed.Children.Add(progressRetry);
        feed.Children.Add(progressCopy);
        AutomationProperties.SetLiveSetting(copyFeedback, AutomationLiveSetting.Polite);
        feed.Children.Add(copyFeedback);
        var scroll = new ScrollViewer { Content = feed, Width = 300, IsTabStop = true,
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
            scroll.MaxHeight = Math.Max(80, xamlRoot.Size.Height - 90);
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
            progressCopy.IsEnabled = false;
            SetCopyFeedback(string.Empty);
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
        progressCopy.Click += (_, _) => CopyPlaybackSummary();
    }

    private void CopyPlaybackSummary()
    {
        if (closed || !playerProgressOpen || settingsVisible || playback.Selected is null ||
            playback.StartupTiming is not { } timing || !ReferenceEquals(timing, displayedStartup) ||
            timing.ReadStartup() is not { } state) return;
        var package = new DataPackage();
        package.SetText(PlaybackProgressSummary.Format(state, startupSettings, AppVersion));
        bool copied;
        try
        {
            copied = Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
        }
        catch (Exception)
        {
            copied = false;
        }
        SetCopyFeedback(copied ? "Summary copied." : "Couldn’t copy the summary. Try again.");
    }

    private void SetCopyFeedback(string message)
    {
        SetText(copyFeedback, message);
        copyFeedback.Visibility = ToVisibility(message.Length > 0);
        if (message.Length > 0 && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            FrameworkElementAutomationPeer.CreatePeerForElement(copyFeedback)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void RenderPlaybackProgress()
    {
        var timing = playback.StartupTiming;
        if (closed || !playerProgressOpen || timing?.ReadStartup() is not { } state) return;
        if (!ReferenceEquals(displayedStartup, timing))
        {
            displayedStartup = timing;
            discoveryToggle.IsChecked = false;
            SetCopyFeedback(string.Empty);
        }
        progressCopy.IsEnabled = true;
        bool started = state.Completed == PlaybackStartupSnapshot.StepCount;
        SetText(progressSummary, state.Failed ? "Couldn’t start playback" : started ? "Playback started" :
            state.EndedUnconfirmed ? "Video ended" : $"{state.Completed} of {PlaybackStartupSnapshot.StepCount} steps complete");
        SetText(progressTotal, Seconds(state.ElapsedMs) + (started ? " to start" : " elapsed"));
        bool canRetry = playback.Error is not null && !playback.Busy;
        string note = state.Failed ? playback.Error ?? "Try loading this video again." : started
            ? (canRetry ? "Playback started, then stopped. You can retry." : string.Empty)
            : state.EndedUnconfirmed ? "The video ended before startup timing could be confirmed." : StartupNotes[state.Completed];
        SetText(progressNote, note);
        progressNote.Visibility = ToVisibility(note.Length > 0);
        if (!canRetry && progressRetry.FocusState != FocusState.Unfocused)
            ((ScrollViewer)PlayerProgressFlyout.Content).Focus(FocusState.Programmatic);
        progressRetry.Visibility = ToVisibility(canRetry);
        for (int i = 0; i < progressRows.Count; i++)
            progressRows[i].Render(i, state);
        if (AutomationProperties.GetItemStatus(discoveryToggle) != progressRows[0].AccessibleStatus)
            AutomationProperties.SetItemStatus(discoveryToggle, progressRows[0].AccessibleStatus);
        for (int i = 0; i < discoveryRows.Count; i++)
        {
            var duration = state.Discovery.Durations[i];
            bool current = state.Discovery.Current == (StreamDiscoveryStep)i;
            var checks = i == (int)StreamDiscoveryStep.Reachability && duration.HasValue ? state.Discovery.ReachabilityProgress : null;
            discoveryRows[i].RenderStatus(current && !state.IsFinal, current && state.Failed,
                duration.HasValue && !current, state.IsFinal && !state.Failed && current, duration,
                state.Completed > 0 ? "Not needed" : state.IsFinal ? "Not run" : "Waiting", checks);
        }
        SetText(reachabilityDetails, string.Join('\n', PlaybackProgressSummary.ReachabilityLines(state.Discovery, startupSettings, includeCount: false)));
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
        private readonly bool detail;
        private readonly FontIcon? chevron;
        private readonly FontIcon mark = new() { FontSize = 11 };
        private readonly ProgressRing ring = new() { Width = 15, Height = 15, IsActive = false, Visibility = Visibility.Collapsed };
        private readonly TextBlock text;
        private readonly TextBlock time = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        private string? status;
        public Grid View { get; } = new() { MinHeight = 30, ColumnSpacing = 8, Padding = new Thickness(5, 3, 5, 3), CornerRadius = new CornerRadius(4) };

        public string AccessibleStatus => status ?? "Waiting";

        public StartupRow(string label, bool disclosure = false, bool detail = false)
        {
            this.label = label;
            this.detail = detail;
            if (detail) View.MinHeight = 22;
            View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            View.ColumnDefinitions.Add(new ColumnDefinition());
            View.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Grid { VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
            icon.Children.Add(mark);
            icon.Children.Add(ring);
            View.Children.Add(icon);
            text = new TextBlock { Text = label, FontSize = detail ? 11 : 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            View.Children.Add(text);
            if (disclosure)
            {
                View.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                chevron = new FontIcon { FontSize = 9 };
                AutomationProperties.SetAccessibilityView(chevron, AccessibilityView.Raw);
                Grid.SetColumn(chevron, 2);
                View.Children.Add(chevron);
            }
            Grid.SetColumn(time, disclosure ? 3 : 2);
            View.Children.Add(time);
        }

        public void SetExpanded(bool expanded) { if (chevron is not null) chevron.Glyph = expanded ? "\uE70D" : "\uE76C"; }

        public void Render(int index, PlaybackStartupSnapshot snapshot)
        {
            bool current = index == snapshot.Completed;
            RenderStatus(current && !snapshot.IsFinal, current && snapshot.Failed, index < snapshot.Completed,
                current && snapshot.EndedUnconfirmed, snapshot.StepDurations[index]);
        }

        public void RenderStatus(bool active, bool failed, bool done, bool unconfirmed, long? duration, string waiting = "Waiting", StreamCheckProgress? checks = null)
        {
            string next = active ? "In progress" : failed ? "Failed" : done ? "Complete" :
                unconfirmed ? "Not confirmed" : waiting;
            SetText(text, checks is { } count ? $"{label} ({count.Checked}/{count.Total})" : label);
            string name = checks is { } progress ? $"{label}: {progress.Checked} of {progress.Total} streams checked. {next}" : $"{label}: {next}";
            if (AutomationProperties.GetName(View) != name) AutomationProperties.SetName(View, name);
            if (status != next)
            {
                status = next;
                View.Background = active ? ActiveBackground : failed ? FailedBackground : null;
                mark.Glyph = failed ? "\uE783" : done ? "\uE73E" : "\uEA3A";
                mark.Visibility = ToVisibility(!active);
                ring.Visibility = ToVisibility(active);
                ring.IsActive = active;
            }
            SetText(time, active && duration is long running ? Seconds(running) + " so far" :
                failed ? detail && duration is long stopped ? $"{Seconds(stopped)} · Failed" : "Failed" :
                done && duration is long complete ? detail && complete < 100 ? "<0.1 s" : Seconds(complete) : next);
        }
    }
}
