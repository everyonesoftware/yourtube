using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.Storage.Pickers;
using YourTube.Playback;

namespace YourTube.App;

internal sealed partial class MainWindow
{
    private Button PlayerDownloadButton => C<Button>("PlayerDownloadButton");
    private Button PlayerDownloadOptionsButton => C<Button>("PlayerDownloadOptionsButton");
    private Flyout PlayerDownloadFlyout => (Flyout)FlyoutBase.GetAttachedFlyout(PlayerDownloadOptionsButton);
    private PlaybackMedia? downloadCatalog;
    private IReadOnlyList<MediaExportOption> downloadOptions = [];
    private MediaExportFormat preferredDownloadFormat = MediaExportFormat.Mp4;
    private int? preferredDownloadHeight;
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
        PlayerDownloadButton.Click += async (_, _) =>
        {
            if (playerDownloadOpen || downloadCatalog is not { } catalog || !OwnsDownloadCatalog(catalog)) return;
            var option = PreferredDownloadOption(playbackResolver.GetExportOptions(catalog));
            if (option is not null) await DownloadCurrentVideoAsync(catalog, option, DownloadFormatLabel(option.Format));
        };
        PlayerDownloadOptionsButton.Click += (_, _) =>
        {
            if (playerDownloadOpen || downloadRequest is not null || InteractivePlayer is null ||
                downloadCatalog is not { } catalog || !OwnsDownloadCatalog(catalog)) return;
            PlayerDownloadFlyout.ShowAt(PlayerDownloadOptionsButton, new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.TopEdgeAlignedRight
            });
        };
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

    private void UpdateDownloadButton()
    {
        var option = PreferredDownloadOption(downloadOptions);
        var button = PlayerDownloadButton;
        button.IsEnabled = option is not null && downloadCatalog?.ExpiresAt > DateTimeOffset.UtcNow &&
            downloadRequest is null && InteractivePlayer is not null;
        PlayerDownloadOptionsButton.IsEnabled = button.IsEnabled;
        var label = option is null ? "Download video" : $"Download {DownloadOptionLabel(option)}";
        if (AutomationProperties.GetName(button) != label)
        {
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, $"{label} · Use the arrow for format options");
        }
    }

    private MediaExportOption? PreferredDownloadOption(IReadOnlyList<MediaExportOption> options) =>
        options.FirstOrDefault(option => option.Format == preferredDownloadFormat && option.Height == preferredDownloadHeight) ??
        options.Where(option => option.Format == preferredDownloadFormat).MaxBy(option => option.Height) ??
        options.OrderBy(option => option.Format).ThenByDescending(option => option.Height).FirstOrDefault();

    private static string DownloadOptionLabel(MediaExportOption option) => option.Height is int height
        ? $"{DownloadFormatLabel(option.Format)} · {height}p" : $"{DownloadFormatLabel(option.Format)} audio";

    private void PopulateDownloadFormats()
    {
        var anchor = PlayerDownloadButton;
        double width = Math.Clamp((anchor.XamlRoot?.Size.Width ?? 320) - 36, 120, 244);
        var card = new StackPanel { Width = width, Spacing = 8, Margin = new Thickness(4) };
        var title = new TextBlock { Text = "Download options", FontSize = 15, TextWrapping = TextWrapping.Wrap,
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
        if (PreferredDownloadOption(options) is not { } initial)
        {
            card.Children.Add(new TextBlock { Text = "Download options are no longer available.", TextWrapping = TextWrapping.Wrap, IsTabStop = true });
            return;
        }
        var formats = options.Select(option => option.Format).Distinct().Order().ToArray();
        var format = new ComboBox { Header = "Format", HorizontalAlignment = HorizontalAlignment.Stretch };
        var resolution = new ComboBox { Header = "Resolution", HorizontalAlignment = HorizontalAlignment.Stretch };
        var save = new Button { Content = "Save as…", Style = (Style)root.Resources["DownloadPrimaryAction"], HorizontalAlignment = HorizontalAlignment.Stretch };
        card.Children.Add(format);
        card.Children.Add(resolution);
        card.Children.Add(save);

        foreach (var value in formats)
            format.Items.Add(new ComboBoxItem { Content = $"{DownloadFormatLabel(value)} · {(value is MediaExportFormat.Mp4 or MediaExportFormat.WebM ? "Video" : "Audio")}", Tag = value });
        format.SelectionChanged += (_, _) =>
        {
            var height = ((resolution.SelectedItem as ComboBoxItem)?.Tag as MediaExportOption)?.Height ?? initial.Height;
            resolution.Items.Clear();
            if ((format.SelectedItem as ComboBoxItem)?.Tag is not MediaExportFormat selected)
            {
                resolution.Visibility = Visibility.Collapsed;
                save.IsEnabled = false;
                return;
            }
            foreach (var option in options.Where(option => option.Format == selected && option.Height is not null).OrderByDescending(option => option.Height))
                resolution.Items.Add(new ComboBoxItem { Content = $"{option.Height}p", Tag = option });
            resolution.Visibility = ToVisibility(resolution.Items.Count > 0);
            if (resolution.Items.Count > 0)
                resolution.SelectedItem = resolution.Items.Cast<ComboBoxItem>()
                    .FirstOrDefault(item => ((MediaExportOption)item.Tag).Height == height) ?? resolution.Items[0];
            save.IsEnabled = true;
        };
        format.SelectedIndex = Array.IndexOf(formats, initial.Format);
        save.Click += async (_, _) =>
        {
            if (!playerDownloadOpen || !ReferenceEquals(PlayerDownloadFlyout.Content, panel) || !OwnsDownloadCatalog(catalog) ||
                (format.SelectedItem as ComboBoxItem)?.Tag is not MediaExportFormat selected) return;
            var option = (resolution.SelectedItem as ComboBoxItem)?.Tag as MediaExportOption ??
                options.FirstOrDefault(option => option.Format == selected && option.Height is null);
            if (option is null || option.Format != selected) return;
            await DownloadCurrentVideoAsync(catalog, option, DownloadFormatLabel(option.Format), rememberChoice: true);
        };
    }

    private async Task DownloadCurrentVideoAsync(PlaybackMedia catalog, MediaExportOption option, string formatLabel, bool rememberChoice = false)
    {
        if (downloadRequest is not null || InteractivePlayer is null || !OwnsDownloadCatalog(catalog) ||
            !playbackResolver.GetExportOptions(catalog).Contains(option)) return;
        using var request = downloadRequest = new CancellationTokenSource();
        var token = request.Token;
        var step = "Couldn’t open the Save As dialog. Try again.";
        if (rememberChoice)
        {
            preferredDownloadFormat = option.Format;
            preferredDownloadHeight = option.Height;
        }
        var launchButton = PlayerDownloadButton;
        var originalFocus = root.XamlRoot is { } xamlRoot ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xamlRoot) : null;
        var restoreKeyboardFocus = launchButton.FocusState == FocusState.Keyboard ||
            originalFocus is Control { FocusState: FocusState.Keyboard };
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
                step = "Couldn’t open the FFmpeg picker. Try again.";
                const string chooseFfmpeg = "Select a trusted ffmpeg.exe to create this format.";
                SetDownloadStatus(chooseFfmpeg);
                var picker = new FileOpenPicker(AppWindow.Id) { CommitButtonText = "Use FFmpeg" };
                picker.FileTypeFilter.Add(".exe");
                var choice = await picker.PickSingleFileAsync().AsTask(token);
                if (!OwnsDownloadCatalog(catalog) || token.IsCancellationRequested) return;
                if (choice is null) { SetDownloadStatus(""); return; }
                if (!IsFfmpegPath(choice.Path)) { SetDownloadStatus(chooseFfmpeg); return; }
                ffmpegPath = choice.Path;
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
            var message = error.Code switch
            {
                "destination_exists" => "That name is already in use. Choose a new filename; existing files are not replaced.",
                "invalid_destination" => "Choose an existing folder and a filename with the selected format’s extension.",
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
