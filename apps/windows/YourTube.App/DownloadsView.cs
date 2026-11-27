using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.System;
using YourTube.Playback;

namespace YourTube.App;

internal sealed partial class MainWindow
{
    private readonly DownloadJournal downloadJournal = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveryoneSoftware", "YourTube", "downloads"));
    private bool downloadsVisible;
    private bool settingsReturnToDownloads;
    private long downloadsVisit;
    private long downloadSnapshotGeneration;
    private Task? downloadRecovery;
    private DownloadSnapshot downloadSnapshot = new([], []);
    private bool downloadSnapshotLoading;
    private bool downloadSnapshotFailed;
    private bool hasDownloadSnapshot;
    private string downloadOutcome = "";
    private bool downloadActionBusy;
    private ExportJob? activeDownload;
    private ExportJob? visibleDownload;
    private ProgressBar? downloadMeter;
    private TextBlock? downloadAmount, downloadPhase;
    private Button? downloadCancelButton;

    private sealed class ExportJob(PlaybackMedia catalog, MediaExportOption option, string videoId)
    {
        private int commitAdmitted;
        public PlaybackMedia Catalog { get; } = catalog;
        public MediaExportOption Option { get; } = option;
        public string VideoId { get; } = videoId;
        public double Progress { get; set; }
        public bool CommitAdmitted => Volatile.Read(ref commitAdmitted) != 0;
        public void MarkCommitAdmitted() => Volatile.Write(ref commitAdmitted, 1);
    }

    private void WireDownloads()
    {
        C<Button>("DownloadsButton").Click += (_, _) => ShowDownloads();
        root.Loaded += (_, _) => downloadRecovery ??= InitializeDownloadsAsync();
    }

    private void ShowDownloads()
    {
        if (closed || drainingWindowWatch || downloadsVisible) return;
        ++downloadsVisit;
        downloadsVisible = true;
        settingsVisible = settingsReturnToDownloads = false;
        browserNotice = null;
        playback.Back();
        Render();
        if (activeDownload is null && !downloadSnapshotLoading) _ = RefreshDownloadsAsync();
    }

    private void RenderDownloadsRoute()
    {
        SetLibraryDestination(C<Button>("DownloadsButton"), "Downloads", enabled: true, active: downloadsVisible);
        C<ScrollViewer>("DownloadsPanel").Visibility = ToVisibility(downloadsVisible);
        if (downloadsVisible) RenderDownloads();
    }

    private async Task InitializeDownloadsAsync()
    {
        await RefreshDownloadsAsync();
        try
        {
            await Task.Run(downloadJournal.RecoverPending);
        }
        catch (Exception)
        {
            // Recovery is best-effort; unresolved records remain available for explicit retry.
        }
        finally
        {
            if (!closed && !drainingWindowWatch && activeDownload is null) await RefreshDownloadsAsync();
        }
    }

    private async Task RefreshDownloadsAsync()
    {
        long generation = ++downloadSnapshotGeneration;
        downloadSnapshotLoading = true;
        downloadSnapshotFailed = false;
        RenderDownloads();
        try
        {
            var snapshot = await Task.Run(downloadJournal.ReadLocal);
            if (closed || drainingWindowWatch || generation != downloadSnapshotGeneration) return;
            hasDownloadSnapshot = true;
            ApplyDownloadSnapshot(snapshot);
        }
        catch (Exception)
        {
            if (!closed && !drainingWindowWatch && generation == downloadSnapshotGeneration) downloadSnapshotFailed = true;
        }
        finally
        {
            if (!closed && !drainingWindowWatch && generation == downloadSnapshotGeneration)
            {
                downloadSnapshotLoading = false;
                RenderDownloads();
            }
        }
    }

    private void ApplyDownloadSnapshot(DownloadSnapshot snapshot)
    {
        if (!snapshot.Completed.SequenceEqual(downloadSnapshot.Completed))
        {
            var rows = C<StackPanel>("DownloadCompletedRows");
            rows.Children.Clear();
            foreach (var receipt in snapshot.Completed.OrderByDescending(item => item.SavedAt)) rows.Children.Add(CompletedRow(receipt));
        }
        if (!snapshot.Cleanup.SequenceEqual(downloadSnapshot.Cleanup))
        {
            var rows = C<StackPanel>("DownloadCleanupRows");
            rows.Children.Clear();
            foreach (var issue in snapshot.Cleanup) rows.Children.Add(CleanupRow(issue));
        }
        downloadSnapshot = snapshot;
    }

    private void RenderDownloads()
    {
        if (closed || !downloadsVisible) return;
        int pending = activeDownload is null ? 0 : 1;
        int issues = downloadSnapshot.Cleanup.Count;
        int completed = downloadSnapshot.Completed.Count;
        bool empty = pending + issues + completed == 0;
        C<TextBlock>("DownloadsSummary").Text = empty ?
            downloadSnapshotLoading || !hasDownloadSnapshot && !downloadSnapshotFailed ? "Loading downloads…" : "Files saved from the player appear here." :
            string.Join(" · ", new[] { pending > 0 ? "1 in progress" : null, issues > 0 ? $"{issues} needs attention" : null, $"{completed} completed" }.OfType<string>());
        C<Border>("DownloadsEmpty").Visibility = ToVisibility(empty && hasDownloadSnapshot && !downloadSnapshotLoading && !downloadSnapshotFailed);
        C<StackPanel>("DownloadActiveGroup").Visibility = ToVisibility(pending != 0);
        C<StackPanel>("DownloadCleanupGroup").Visibility = ToVisibility(issues != 0);
        C<StackPanel>("DownloadCompletedGroup").Visibility = ToVisibility(completed != 0);
        C<TextBlock>("DownloadCleanupHeading").Text = $"Needs attention  ·  {issues}";
        C<TextBlock>("DownloadCompletedHeading").Text = $"Completed  ·  {completed}";
        SetDownloadsFeedback(downloadSnapshotFailed ? "Couldn’t read saved downloads. Leave Downloads and return to try again." : downloadOutcome);
        RenderActiveDownload();
    }

    private void RenderActiveDownload()
    {
        if (closed || !downloadsVisible) return;
        if (!ReferenceEquals(visibleDownload, activeDownload))
        {
            visibleDownload = activeDownload;
            var host = C<ContentControl>("DownloadActiveRow");
            host.Content = null;
            downloadMeter = null;
            downloadAmount = downloadPhase = null;
            downloadCancelButton = null;
            if (activeDownload is { } job)
            {
                var details = Details(job.Catalog.Title, ExportDetails(job.Option.Format, job.Option.Height) + (job.Option.Height is null ? "" : " · Video + audio"));
                downloadMeter = new ProgressBar { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 3, 0, 0) };
                AutomationProperties.SetName(downloadMeter, $"Download progress: {job.Catalog.Title}");
                details.Children.Add(downloadMeter);
                details.Children.Add(downloadAmount = RowText("", muted: true));
                details.Children.Add(downloadPhase = RowText("", muted: true));
                downloadCancelButton = DownloadAction("Cancel", $"Cancel download: {job.Catalog.Title}");
                downloadCancelButton.Click += (_, _) => { if (downloadsVisible && ReferenceEquals(activeDownload, job)) CancelDownload(); };
                var preview = DownloadPreview(job.Option.Format);
                host.Content = DownloadCard(preview, details, downloadCancelButton);
                _ = SetDownloadThumbnailAsync(preview, job);
            }
        }
        if (activeDownload is not { } active || downloadMeter is null || downloadAmount is null || downloadPhase is null || downloadCancelButton is null) return;
        double fraction = Math.Clamp(active.Progress / 0.95, 0, 1);
        bool finishing = active.Progress >= 0.95;
        bool canceling = !DownloadCommitting && downloadRequest?.IsCancellationRequested == true;
        downloadMeter.IsIndeterminate = finishing || canceling;
        downloadMeter.Value = fraction * 100;
        downloadAmount.Text = $"{SizeLabel((long)(active.Option.SourceBytes * fraction))} of {SizeLabel(active.Option.SourceBytes)} transferred";
        downloadPhase.Text = DownloadCommitting ? "Saving…" : canceling ? "Canceling…" : finishing ? "Finishing…" : $"{fraction:P0}";
        if (!CanCancelDownload && downloadCancelButton.IsEnabled && IsDownloadActionFocused(downloadCancelButton))
            C<Button>("DownloadsButton").Focus(FocusState.Keyboard);
        downloadCancelButton.IsEnabled = CanCancelDownload;
    }

    private async Task SetDownloadThumbnailAsync(Border preview, ExportJob job)
    {
        try
        {
            if (downloadRequest is not { } request) return;
            var bytes = await libraryThumbnails.LoadAsync(job.VideoId, request.Token);
            if (closed || !ReferenceEquals(job, activeDownload) || bytes is null) return;
            var bitmap = await DecodeImageAsync(bytes, 208);
            if (closed || !ReferenceEquals(job, activeDownload)) return;
            preview.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        }
        catch (OperationCanceledException) { }
        catch (System.Runtime.InteropServices.COMException) { } // Keep the media glyph when the preview fails.
    }

    private Border CompletedRow(CompletedDownload receipt)
    {
        var details = Details(receipt.Title, $"{ExportDetails(receipt.Format, receipt.Height)} · {SizeLabel(receipt.Bytes)} · {SavedLabel(receipt.SavedAt)}");
        var open = DownloadAction("Open", $"Open download: {receipt.Title}");
        open.Style = (Style)root.Resources["DownloadPrimaryAction"];
        open.Click += async (_, _) => await LaunchDownloadAsync(receipt, showFolder: false);
        var folder = DownloadAction(new FontIcon { Glyph = "\uE8B7", FontSize = 15 }, $"Show in folder: {receipt.Title}");
        ToolTipService.SetToolTip(folder, "Show in folder");
        folder.Click += async (_, _) => await LaunchDownloadAsync(receipt, showFolder: true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(open);
        actions.Children.Add(folder);
        var row = DownloadCard(DownloadPreview(receipt.Format), details, actions);
        row.Margin = new Thickness(0, 0, 0, 4);
        return row;
    }

    private Border CleanupRow(DownloadCleanup issue)
    {
        var details = Details("Couldn’t finish cleaning up a download", $"{issue.Title} · The incomplete file couldn’t be verified or removed.");
        var retry = DownloadAction("Retry cleanup", $"Retry cleanup: {issue.Title}");
        retry.Click += async (_, _) => await RetryDownloadCleanupAsync(issue, retry);
        var glyph = new FontIcon { Glyph = "\uE7BA", Width = 32, Height = 32, FontSize = 19 };
        AutomationProperties.SetAccessibilityView(glyph, AccessibilityView.Raw);
        return DownloadCard(glyph, details, retry);
    }

    private bool IsDownloadActionFocused(Button? button) => downloadsVisible && button is not null &&
        root.XamlRoot is { } xamlRoot && ReferenceEquals(FocusManager.GetFocusedElement(xamlRoot), button);

    private bool IsDownloadsVisit(long visit) => !closed && !drainingWindowWatch && downloadsVisible && downloadsVisit == visit;

    private async Task RetryDownloadCleanupAsync(DownloadCleanup issue, Button source)
    {
        long visit = downloadsVisit;
        if (!IsDownloadsVisit(visit) || downloadActionBusy || !downloadSnapshot.Cleanup.Contains(issue)) return;
        downloadActionBusy = true;
        try
        {
            SetDownloadsFeedback("Checking incomplete download…");
            var result = await Task.Run(() => downloadJournal.RetryCleanup(issue.Id));
            if (closed) return;
            downloadOutcome = result switch
            {
                CleanupResult.Removed => "Cleanup finished.",
                CleanupResult.IdentityMismatch => "The file no longer matches this download. No file was removed.",
                _ => "Cleanup couldn’t finish. Check that the save drive is available, then retry."
            };
            if (result == CleanupResult.Removed && IsDownloadsVisit(visit) && IsDownloadActionFocused(source))
                C<Button>("DownloadsButton").Focus(FocusState.Keyboard);
            if (activeDownload is null) await RefreshDownloadsAsync();
            else RenderDownloads();
        }
        catch (Exception)
        {
            if (IsDownloadsVisit(visit)) SetDownloadsFeedback("Couldn’t retry cleanup. No file was confirmed removed.");
        }
        finally { downloadActionBusy = false; }
    }

    private async Task LaunchDownloadAsync(CompletedDownload receipt, bool showFolder)
    {
        long visit = downloadsVisit;
        if (!IsDownloadsVisit(visit) || downloadActionBusy || !downloadSnapshot.Completed.Contains(receipt)) return;
        downloadActionBusy = true;
        try
        {
            using var lease = await Task.Run(() => downloadJournal.AcquireCompleted(receipt.Id));
            if (!IsDownloadsVisit(visit) || !downloadSnapshot.Completed.Contains(receipt)) return;
            if (lease is null)
            {
                SetDownloadsFeedback("This file is missing, unavailable, or no longer matches the saved download.");
                return;
            }
            var file = await StorageFile.GetFileFromPathAsync(lease.Path);
            if (!IsDownloadsVisit(visit) || !downloadSnapshot.Completed.Contains(receipt)) return;
            bool launched;
            if (!showFolder) launched = await Launcher.LaunchFileAsync(file);
            else
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(lease.Path)!);
                if (!IsDownloadsVisit(visit) || !downloadSnapshot.Completed.Contains(receipt)) return;
                var options = new FolderLauncherOptions();
                WinRT.Interop.InitializeWithWindow.Initialize(options, WinRT.Interop.WindowNative.GetWindowHandle(this));
                options.ItemsToSelect.Add(file);
                launched = await Launcher.LaunchFolderAsync(folder, options);
            }
            if (IsDownloadsVisit(visit) && downloadSnapshot.Completed.Contains(receipt))
                SetDownloadsFeedback(launched ? "" : "Windows couldn’t open the saved download.");
        }
        catch (Exception)
        {
            if (IsDownloadsVisit(visit) && downloadSnapshot.Completed.Contains(receipt))
                SetDownloadsFeedback("Windows couldn’t open the saved download. Check that it’s still available.");
        }
        finally { downloadActionBusy = false; }
    }

    private void SetDownloadsFeedback(string text)
    {
        var status = C<TextBlock>("DownloadsFeedback");
        if (status.Text == text) return;
        status.Text = text;
        if (text.Length != 0) FrameworkElementAutomationPeer.FromElement(status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static string ExportDetails(MediaExportFormat format, int? height) =>
        $"{DownloadFormatLabel(format)} · {(height is int value ? $"{value}p" : "Audio only")}";

    private static string SizeLabel(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.##} GB" :
        bytes >= 1_000_000 ? $"{bytes / 1_000_000d:0.#} MB" : bytes >= 1_000 ? $"{bytes / 1_000d:0.#} KB" : $"{bytes:N0} B";

    private static string SavedLabel(DateTimeOffset saved)
    {
        var local = saved.ToLocalTime();
        return local.Date == DateTime.Today ? "Saved today" : local.Date == DateTime.Today.AddDays(-1) ? "Saved yesterday" : $"Saved {local:d}";
    }

    private TextBlock RowText(string value, bool muted)
    {
        var text = new TextBlock
        {
            Text = value, TextWrapping = TextWrapping.Wrap, FontSize = muted ? 12 : 14,
            FontWeight = muted ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold
        };
        if (muted) text.Foreground = (Brush)root.Resources["Muted"];
        return text;
    }

    private StackPanel Details(string title, string subtitle)
    {
        var details = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(RowText(title, muted: false));
        details.Children.Add(RowText(subtitle, muted: true));
        return details;
    }

    private static Button DownloadAction(object content, string name)
    {
        var button = new Button { Content = content, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static Border DownloadPreview(MediaExportFormat format)
    {
        var preview = new Border { Width = 80, Height = 45, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon { Glyph = format is MediaExportFormat.M4a or MediaExportFormat.Mp3 ? "\uE189" : "\uE768", FontSize = 17 } };
        AutomationProperties.SetAccessibilityView(preview, AccessibilityView.Raw);
        return preview;
    }

    private Border DownloadCard(FrameworkElement preview, StackPanel details, FrameworkElement actions)
    {
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 7 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(details, 1);
        grid.Children.Add(preview);
        grid.Children.Add(details);
        grid.Children.Add(actions);
        ArrangeActions(true);
        grid.SizeChanged += (_, args) => ArrangeActions(args.NewSize.Width < 440);
        return new Border
        {
            Child = grid, Padding = new Thickness(10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Background = (Brush)C<Border>("DownloadsEmpty").Background, BorderBrush = C<Border>("DownloadsEmpty").BorderBrush
        };

        void ArrangeActions(bool compact)
        {
            Grid.SetColumn(actions, compact ? 1 : 2);
            Grid.SetRow(actions, compact ? 1 : 0);
        }
    }
}
