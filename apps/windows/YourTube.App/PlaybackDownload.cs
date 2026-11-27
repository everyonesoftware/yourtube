using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using YourTube.Playback;

namespace YourTube.App;

internal sealed partial class MainWindow
{
    private Button PlayerDownloadButton => C<Button>("PlayerDownloadButton");
    private Flyout PlayerDownloadFlyout => (Flyout)PlayerDownloadButton.Flyout;
    private PlaybackMedia? downloadCatalog;
    private IReadOnlyList<MediaExportOption> downloadOptions = [];
    private readonly Dictionary<MediaExportFormat, (int? Height, int? Bitrate)> downloadPreferences = [];
    private CancellationTokenSource? downloadRequest;
    private Task? downloadTransfer;
    private bool playerDownloadOpen;
    private bool downloadPicking;
    private bool DownloadCommitting => activeDownload?.CommitAdmitted == true;

    private PlaybackMedia? CurrentDownloadCatalog => !closed && !drainingWindowWatch && !settingsVisible && !downloadsVisible && playback.Selected is not null &&
        playback.Active is { IsOpened: true, HasFailed: false } active ? active.Catalog : null;

    private bool OwnsDownloadCatalog(PlaybackMedia catalog) => ReferenceEquals(downloadCatalog, catalog) && ReferenceEquals(CurrentDownloadCatalog, catalog);

    private void WirePlaybackDownload()
    {
        PlayerDownloadFlyout.Opening += (_, _) => PopulateDownloadFormats();
        PlayerDownloadFlyout.Opened += (_, _) => { playerDownloadOpen = true; UpdatePlayerControlsVisibility(); };
        PlayerDownloadFlyout.Closed += (_, _) =>
        {
            playerDownloadOpen = false;
            PlayerDownloadFlyout.Content = null;
            UpdatePlayerControlsVisibility();
        };
        C<Button>("PlayerDownloadCancel").Click += (_, _) => CancelDownload();
    }

    private bool CanCancelDownload => !closed && !drainingWindowWatch && activeDownload is not null && !DownloadCommitting &&
        downloadRequest is { IsCancellationRequested: false };

    private void CancelDownload()
    {
        if (!CanCancelDownload || downloadRequest is not { } request) return;
        request.Cancel();
        if (activeDownload is { } job && OwnsDownloadCatalog(job.Catalog))
            SetDownloadStatus("Canceling download…", transferring: true);
        RenderActiveDownload();
    }

    private void RefreshPlaybackDownloadContext()
    {
        var catalog = CurrentDownloadCatalog;
        if (!ReferenceEquals(downloadCatalog, catalog))
        {
            RetirePlaybackDownload();
            downloadCatalog = catalog;
            if (catalog is not null) downloadOptions = playbackResolver.GetExportOptions(catalog);
        }
        if (playback.QualityBusy) PlayerDownloadFlyout.Hide();
        UpdateDownloadButton();
    }

    private void RetirePlaybackDownload(bool closing = false)
    {
        downloadCatalog = null;
        downloadOptions = [];
        PlayerDownloadFlyout.Hide();
        // The picker belongs to Theater. An admitted file export belongs to the window.
        if (closing || activeDownload is null) downloadRequest?.Cancel();
        if (!closed) SetDownloadStatus("");
    }

    private void UpdateDownloadButton() => PlayerDownloadButton.IsEnabled =
        downloadOptions.Count > 0 && downloadCatalog?.ExpiresAt > DateTimeOffset.UtcNow &&
        downloadRequest is null && InteractivePlayer is not null;

    private static string DownloadQualityLabel(MediaExportOption option) => option.Height is int height ? $"{height}p" :
        option.AudioBitrateKbps is int bitrate ? $"{bitrate} kbps" :
        option.Format == MediaExportFormat.M4a ? "Original" : "High (VBR)";

    private void PopulateDownloadFormats()
    {
        var anchor = PlayerDownloadButton;
        double width = Math.Clamp((anchor.XamlRoot?.Size.Width ?? 320) - 46, 120, 254);
        var card = new StackPanel { Width = width, Spacing = 8, Margin = new Thickness(4) };
        var title = new TextBlock { Text = "Download formats", FontSize = 15, TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        card.Children.Add(title);
        var panel = new ScrollViewer
        {
            MaxHeight = Math.Clamp((anchor.XamlRoot?.Size.Height ?? 320) - 64, 96, 300), Content = card, IsTabStop = false,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        PlayerDownloadFlyout.Content = panel;

        if (downloadCatalog is not { } catalog || !OwnsDownloadCatalog(catalog) || InteractivePlayer is null || downloadRequest is not null)
        {
            card.Children.Add(new TextBlock { Text = "Download options are no longer available.", TextWrapping = TextWrapping.Wrap, IsTabStop = true });
            return;
        }

        var options = playbackResolver.GetExportOptions(catalog);
        if (options.Count == 0)
        {
            card.Children.Add(new TextBlock { Text = "Download options are no longer available.", TextWrapping = TextWrapping.Wrap, IsTabStop = true });
            return;
        }
        AddSection("Video + audio", "\uE714", MediaExportFormat.Mp4, MediaExportFormat.WebM);
        AddSection("Audio only", "\uE8D6", MediaExportFormat.M4a, MediaExportFormat.Mp3);

        void AddSection(string label, string glyph, params MediaExportFormat[] formats)
        {
            if (!options.Any(option => formats.Contains(option.Format))) return;
            if (card.Children.Count > 1)
                card.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 59, 69, 61)) });
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var icon = new FontIcon { Glyph = glyph, FontSize = 13, Foreground = (Brush)root.Resources["AccentFillColorDefaultBrush"] };
            AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
            header.Children.Add(icon);
            var heading = new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)root.Resources["Muted"],
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
            header.Children.Add(heading);
            card.Children.Add(header);

            foreach (var fileFormat in formats)
            {
                var choices = options.Where(choice => choice.Format == fileFormat).OrderByDescending(choice => choice.Height).ToArray();
                if (choices.Length == 0) continue;
                var formatLabel = DownloadFormatLabel(fileFormat);
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
                var save = new Button { Content = formatLabel, HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(5, 0, 0, 5) };
                var quality = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0,
                    CornerRadius = new CornerRadius(0, 5, 5, 0), MaxDropDownHeight = 220 };
                AutomationProperties.SetName(quality, $"{formatLabel} {(choices[0].Height is not null ? "resolution" : "audio quality")}");
                foreach (var choice in choices)
                    quality.Items.Add(new ComboBoxItem { Content = DownloadQualityLabel(choice), Tag = choice });
                quality.SelectionChanged += (_, _) =>
                {
                    if (quality.SelectedItem is not ComboBoxItem { Tag: MediaExportOption choice }) return;
                    var action = $"Save {formatLabel} · {DownloadQualityLabel(choice)} as…";
                    AutomationProperties.SetName(save, action);
                    ToolTipService.SetToolTip(save, action);
                };
                int remembered = downloadPreferences.TryGetValue(fileFormat, out var preference)
                    ? Array.FindIndex(choices, choice => (choice.Height, choice.AudioBitrateKbps) == preference) : -1;
                quality.SelectedIndex = Math.Max(0, remembered);
                save.Click += async (_, _) =>
                {
                    if (!playerDownloadOpen || !ReferenceEquals(PlayerDownloadFlyout.Content, panel) ||
                        quality.SelectedItem is not ComboBoxItem { Tag: MediaExportOption option } || option.Format != fileFormat) return;
                    await DownloadCurrentVideoAsync(catalog, option, formatLabel);
                };
                row.Children.Add(save);
                Grid.SetColumn(quality, 1);
                row.Children.Add(quality);
                card.Children.Add(row);
            }
        }
    }

    private ((string VideoId, MediaExportFormat Format, int? Height, int? AudioBitrateKbps) Choice, string Path)? ffmpegFallback;

    private async Task DownloadCurrentVideoAsync(PlaybackMedia catalog, MediaExportOption option, string formatLabel)
    {
        if (downloadRequest is not null || InteractivePlayer is null || !OwnsDownloadCatalog(catalog) ||
            !playbackResolver.GetExportOptions(catalog).Contains(option)) return;
        using var request = downloadRequest = new CancellationTokenSource();
        var token = request.Token;
        var step = "Couldn’t open the Save As dialog. Try again.";
        var exportChoice = (catalog.VideoId, option.Format, option.Height, option.AudioBitrateKbps);
        downloadPreferences[option.Format] = (option.Height, option.AudioBitrateKbps);
        var launchButton = PlayerDownloadButton;
        var originalFocus = root.XamlRoot is { } xamlRoot ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xamlRoot) : null;
        var restoreKeyboardFocus = launchButton.FocusState == FocusState.Keyboard ||
            originalFocus is Control { FocusState: FocusState.Keyboard };
        PortableFfmpeg.Lease? managedFfmpeg = null;
        try
        {
            downloadPicking = true;
            PlayerDownloadFlyout.Hide();
            UpdateDownloadButton();
            SetDownloadStatus("Choose where to save the download.");
            await Task.Yield();
            if (!OwnsDownloadCatalog(catalog) || token.IsCancellationRequested) return;
            var destination = DownloadSaveDialog.Pick(WinRT.Interop.WindowNative.GetWindowHandle(this),
                DownloadFileName(catalog.Title), formatLabel, option.Extension, token);
            if (!OwnsDownloadCatalog(catalog) || token.IsCancellationRequested) return;
            if (destination is null) { SetDownloadStatus(""); return; }
            if (!string.Equals(Path.GetExtension(destination), option.Extension, StringComparison.OrdinalIgnoreCase))
            {
                SetDownloadStatus($"Choose a filename ending in {option.Extension}.");
                return;
            }
            step = "Couldn’t check the selected destination. Try another location.";
            var destinationExists = await Task.Run(() => File.Exists(destination) || Directory.Exists(destination), token);
            if (!OwnsDownloadCatalog(catalog) || token.IsCancellationRequested) return;
            if (destinationExists)
            {
                SetDownloadStatus("That name is already in use. Choose a new filename; existing files are not replaced.");
                return;
            }

            string? ffmpegPath = null;
            if (option.Format != MediaExportFormat.M4a)
            {
                step = "Couldn’t load FFmpeg. Check Settings → FFmpeg and try again.";
                managedFfmpeg = await Task.Run(() => portableFfmpeg.OpenAsync(token), token);
                if (!OwnsDownloadCatalog(catalog) || token.IsCancellationRequested) return;
                ffmpegPath = managedFfmpeg?.Installation.Path;
                if (ffmpegPath is null || ffmpegFallback == (exportChoice, ffmpegPath))
                {
                    bool usingFallback = managedFfmpeg is not null;
                    managedFfmpeg?.Dispose();
                    managedFfmpeg = null;
                    step = "Couldn’t open the FFmpeg picker. Try again.";
                    string chooseFfmpeg = usingFallback
                        ? "Select a different trusted ffmpeg.exe for this export."
                        : "Configure a portable copy in Settings → FFmpeg, or select a trusted ffmpeg.exe for this export.";
                    SetDownloadStatus(chooseFfmpeg);
                    var picker = new FileOpenPicker(AppWindow.Id) { CommitButtonText = "Use FFmpeg" };
                    picker.FileTypeFilter.Add(".exe");
                    var choice = await picker.PickSingleFileAsync().AsTask(token);
                    if (!OwnsDownloadCatalog(catalog) || token.IsCancellationRequested) return;
                    if (choice is null) { SetDownloadStatus(""); return; }
                    if (!IsFfmpegPath(choice.Path)) { SetDownloadStatus(chooseFfmpeg); return; }
                    ffmpegPath = choice.Path;
                }
            }

            if (!OwnsDownloadCatalog(catalog) || token.IsCancellationRequested) return;
            downloadPicking = false;
            activeDownload = new(catalog, option, playback.Selected!.VideoId);
            // A concurrent journal read can see staging for this job; only the active row should represent it.
            ++downloadSnapshotGeneration;
            downloadSnapshotLoading = false;
            var job = activeDownload;
            downloadOutcome = "";
            RenderDownloads();
            step = "Couldn’t save the download. Check the destination and try again.";
            SetDownloadStatus($"Downloading {formatLabel}…", transferring: true);
            var bar = C<ProgressBar>("PlayerDownloadProgress");
            bar.IsIndeterminate = true;
            bar.Value = 0;
            IProgress<double> uiProgress = new Progress<double>(value =>
            {
                if (!ReferenceEquals(downloadRequest, request) || closed || drainingWindowWatch || !double.IsFinite(value)) return;
                job.Progress = Math.Max(job.Progress, Math.Clamp(value, 0, 1));
                RenderActiveDownload();
                if (!OwnsDownloadCatalog(catalog) || (token.IsCancellationRequested && !DownloadCommitting)) return;
                bar.IsIndeterminate = value >= 0.95;
                bar.Value = Math.Clamp(job.Progress / 0.95, 0, 1) * 100;
                if (value >= 0.95)
                {
                    var message = DownloadCommitting ? "Saving download…" : "Finishing download…";
                    if (C<TextBlock>("PlayerDownloadStatus").Text != message)
                        SetDownloadStatus(message, transferring: true);
                }
            });
            var progress = new ExportProgress(job, uiProgress);
            downloadTransfer = Task.Run(() => playbackResolver.ExportAsync(catalog, option, destination, ffmpegPath, progress, token, downloadJournal), token);
            await downloadTransfer;
            downloadOutcome = "Download saved.";
            if (OwnsDownloadCatalog(catalog)) SetDownloadStatus("Download saved.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (activeDownload is not null) downloadOutcome = "Download canceled. No file saved.";
            if (OwnsDownloadCatalog(catalog)) SetDownloadStatus("Download canceled.");
        }
        catch (PlaybackException error)
        {
            if (error.Code == "ffmpeg_failed" && managedFfmpeg is not null)
                ffmpegFallback = (exportChoice, managedFfmpeg.Installation.Path);
            var message = error.Code switch
            {
                "destination_exists" => "That name is already in use. Choose a new filename; existing files are not replaced.",
                "invalid_destination" => "Choose an existing folder and a filename with the selected format’s extension.",
                "ffmpeg_failed" when managedFfmpeg is not null => "The portable FFmpeg copy couldn’t create this format. Try the export again to pick a different trusted ffmpeg.exe, or choose M4A.",
                "ffmpeg_required" or "ffmpeg_failed" => "FFmpeg couldn’t create this format. Try another trusted ffmpeg.exe or choose M4A.",
                "expired" => "This stream has expired. Reopen the video before downloading.",
                "export_cleanup_failed" => "Couldn’t remove the incomplete download. See Downloads for cleanup options.",
                _ => step
            };
            if (activeDownload is not null) downloadOutcome = message;
            if (OwnsDownloadCatalog(catalog)) SetDownloadStatus(message);
        }
        catch (Exception)
        {
            if (activeDownload is not null) downloadOutcome = step;
            if (OwnsDownloadCatalog(catalog)) SetDownloadStatus(step);
        }
        finally
        {
            managedFfmpeg?.Dispose();
            downloadPicking = false;
            bool transferred = activeDownload is not null;
            var restoreDownloadFocus = transferred && IsDownloadActionFocused(downloadCancelButton);
            activeDownload = null;
            downloadTransfer = null;
            downloadRequest = null;
            if (!closed && !drainingWindowWatch)
            {
                if (transferred)
                {
                    if (restoreDownloadFocus) C<Button>("DownloadsButton").Focus(FocusState.Keyboard);
                    _ = RefreshDownloadsAsync();
                }
                UpdateDownloadButton();
                var focused = root.XamlRoot is { } currentRoot ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(currentRoot) : null;
                if (OwnsDownloadCatalog(catalog) && launchButton.IsEnabled && restoreKeyboardFocus &&
                    (focused is null || ReferenceEquals(focused, C<Button>("PlayerDownloadCancel")) || (!transferred && ReferenceEquals(focused, originalFocus))))
                    launchButton.Focus(FocusState.Keyboard);
                UpdatePlayerControlsVisibility();
            }
        }
    }

    private sealed class ExportProgress(ExportJob job, IProgress<double> uiProgress) : IProgress<double>
    {
        public void Report(double value)
        {
            // Commit admission is relayed before dispatch so a queued click can no longer cancel.
            if (double.IsFinite(value) && value >= 0.99) job.MarkCommitAdmitted();
            uiProgress.Report(value);
        }
    }

    private void SetDownloadStatus(string message, bool transferring = false)
    {
        var status = C<TextBlock>("PlayerDownloadStatus");
        status.Text = message;
        C<StackPanel>("PlayerDownloadFeedback").Visibility = ToVisibility(message.Length > 0);
        var cancel = C<Button>("PlayerDownloadCancel");
        cancel.Visibility = ToVisibility(transferring);
        cancel.IsEnabled = transferring && CanCancelDownload;
        C<ProgressBar>("PlayerDownloadProgress").Visibility = ToVisibility(transferring);
        if (message.Length > 0)
        {
            lastPlayerActivity = Environment.TickCount64;
            FrameworkElementAutomationPeer.FromElement(status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        UpdatePlayerControlsVisibility();
    }

    private static bool IsFfmpegPath(string path) => Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetFileName(path), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase);

    private static string DownloadFileName(string title)
    {
        // Prefix to avoid DOS device names; remove path, shell and bidi control characters.
        var safe = new string(title.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ||
            "<>:\"/\\|?*".Contains(c) ? ' ' : c).ToArray()).Trim(' ', '.');
        if (safe.Length > 120) safe = safe[..(char.IsHighSurrogate(safe[119]) ? 119 : 120)].TrimEnd(' ', '.');
        return safe.Length == 0 ? "YourTube video" : "YourTube - " + safe;
    }

    private static string DownloadFormatLabel(MediaExportFormat format) => format switch
    {
        MediaExportFormat.Mp4 => "MP4",
        MediaExportFormat.WebM => "WebM",
        MediaExportFormat.M4a => "M4A",
        MediaExportFormat.Mp3 => "MP3",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}
