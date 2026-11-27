using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using YourTube.Playback;

namespace YourTube.App;

internal sealed partial class MainWindow
{
    private readonly PortableFfmpeg portableFfmpeg = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveryoneSoftware", "YourTube", "Tools", "FFmpeg"));
    private readonly CancellationTokenSource ffmpegLifetime = new();
    private bool ffmpegBusy;
    private PortableFfmpeg.Installation? ffmpegInstalled;
    private PortableFfmpeg.Release? ffmpegOffer;

    private void WireFfmpegSettings()
    {
        C<Button>("FfmpegCheck").Click += async (_, _) => await CheckFfmpegAsync(startup: false);
        C<Button>("FfmpegDownload").Click += async (_, _) => await DownloadFfmpegAsync();
        C<Border>("FfmpegPanel").SizeChanged += (_, args) => ArrangeFfmpegSettings(args.NewSize.Width);
    }

    private void ArrangeFfmpegSettings(double width)
    {
        bool compact = width < 570;
        var version = C<StackPanel>("FfmpegVersionFact");
        var actions = C<StackPanel>("FfmpegActions");
        Grid.SetRow(version, compact ? 1 : 0);
        Grid.SetColumn(version, compact ? 0 : 1);
        Grid.SetRow(actions, compact ? 1 : 0);
        Grid.SetColumn(actions, compact ? 0 : 1);
        C<Grid>("FfmpegFacts").ColumnSpacing = compact ? 0 : 12;
        C<Grid>("FfmpegResult").ColumnSpacing = compact ? 0 : 12;
        actions.Orientation = width < 350 ? Orientation.Vertical : Orientation.Horizontal;
        actions.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private async Task CheckFfmpegAsync(bool startup)
    {
        if (ffmpegBusy || closed) return;
        var trigger = C<Button>("FfmpegCheck");
        var keyboard = !startup && trigger.FocusState == FocusState.Keyboard;
        ffmpegBusy = true; // Reserve on the UI thread before yielding to the worker.
        ffmpegOffer = null;
        var token = ffmpegLifetime.Token;
        string status = "Not configured", detail = "Download a portable copy and set it up for YourTube.";
        try
        {
            RenderFfmpeg(startup ? "Looking for FFmpeg…" : "Checking for updates…", "");
            ffmpegInstalled = await Task.Run(() => portableFfmpeg.ReadAsync(token), token);
            if (closed || token.IsCancellationRequested || ffmpegInstalled is not { } installed) return;
            RenderFfmpeg("Checking for updates…", "Looking for a newer compatible build.");
            var release = await Task.Run(() => portableFfmpeg.FindAsync(token), token);
            if (closed || token.IsCancellationRequested) return;
            (status, detail) = PresentFfmpegRelease(installed, release);
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested)
                (status, detail) = ("Couldn’t check for updates", "Try checking again. Your configured copy was not changed.");
        }
        finally
        {
            ffmpegBusy = false;
            if (!closed && !token.IsCancellationRequested)
            {
                RenderFfmpeg(status, detail);
                RestoreFfmpegFocus(keyboard, trigger);
            }
        }
    }

    private (string Status, string Detail) PresentFfmpegRelease(PortableFfmpeg.Installation installed, PortableFfmpeg.Release release)
    {
        ffmpegOffer = release.Version > installed.Version ? release : null;
        if (ffmpegOffer is not null) return ("Update available", $"Latest compatible build: {release.Label}");
        return installed.Version == release.Version
            ? ("Up to date", $"Latest compatible build: {release.Label}")
            : ("No newer build available", $"Latest compatible build: {release.Label}");
    }

    private async Task DownloadFfmpegAsync()
    {
        if (ffmpegBusy || closed || !settingsVisible || (ffmpegInstalled is not null && ffmpegOffer is null)) return;
        var keyboard = C<Button>("FfmpegDownload").FocusState == FocusState.Keyboard;
        bool updating = ffmpegInstalled is not null;
        var offer = ffmpegOffer;
        var token = ffmpegLifetime.Token;
        ffmpegBusy = true;
        string status = updating ? "Couldn’t update FFmpeg" : "Couldn’t configure FFmpeg";
        string detail = updating ? "Your configured copy was not changed. Try downloading the update again." :
            "No portable copy was configured. Try again.";
        try
        {
            RenderFfmpeg(updating ? "Updating FFmpeg…" : "Setting up FFmpeg…", offer is null ? "Finding the latest compatible build." : "Your configured copy stays in place until the update is ready.");
            var progress = C<ProgressBar>("FfmpegProgress");
            var progressText = C<TextBlock>("FfmpegProgressText");
            C<StackPanel>("FfmpegProgressArea").Visibility = Visibility.Visible;
            progress.IsIndeterminate = true;
            progressText.Text = offer is null ? "Looking up the release…" : "Downloading FFmpeg…";
            bool finished = false;
            try
            {
                var report = new Progress<(bool Verifying, double? Fraction)>(value =>
                {
                    if (closed || token.IsCancellationRequested || finished) return;
                    progressText.Text = value.Verifying ? "Verifying and configuring FFmpeg…" : "Downloading FFmpeg…";
                    progress.IsIndeterminate = value.Verifying || value.Fraction is null;
                    progress.Value = (value.Fraction ?? 0) * 100;
                });
                ffmpegInstalled = await Task.Run(async () =>
                {
                    var release = offer ?? await portableFfmpeg.FindAsync(token).ConfigureAwait(false);
                    return await portableFfmpeg.InstallAsync(release, (verifying, fraction) =>
                        ((IProgress<(bool, double?)>)report).Report((verifying, fraction)), token).ConfigureAwait(false);
                }, token);
                ffmpegOffer = null;
                status = "Up to date";
                detail = updating ? "Update complete. YourTube is using the new build." : "Setup complete. YourTube is using this portable copy.";
            }
            finally { finished = true; }
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // Another window may have configured a copy while we checked. Reconcile rather than reporting a stale path.
            try
            {
                ffmpegInstalled = await Task.Run(() => portableFfmpeg.ReadAsync(token), token);
                if (ffmpegInstalled is { } installed)
                {
                    if (offer is not null && installed.Version >= offer.Version) (status, detail) = PresentFfmpegRelease(installed, offer);
                    else if (!updating) (status, detail) = ("FFmpeg configured", "YourTube is using this portable copy.");
                }
                else ffmpegOffer = null;
            }
            catch (Exception) { /* Keep the last known state if the readback itself fails. */ }
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        finally
        {
            ffmpegBusy = false;
            if (!closed && !token.IsCancellationRequested)
            {
                C<StackPanel>("FfmpegProgressArea").Visibility = Visibility.Collapsed;
                RenderFfmpeg(status, detail);
                var download = C<Button>("FfmpegDownload");
                RestoreFfmpegFocus(keyboard, download.Visibility == Visibility.Visible ? download : C<Button>("FfmpegCheck"));
            }
        }
    }

    private void RestoreFfmpegFocus(bool keyboard, Button button)
    {
        if (!keyboard || !settingsVisible || button.Visibility != Visibility.Visible || root.XamlRoot is not { } xamlRoot) return;
        // Do not steal focus from a different control the user moved to during the check.
        var focused = FocusManager.GetFocusedElement(xamlRoot);
        if (focused is null || ReferenceEquals(focused, C<Button>("SettingsButton")) ||
            ReferenceEquals(focused, C<Button>("FfmpegCheck")) || ReferenceEquals(focused, C<Button>("FfmpegDownload")))
            button.Focus(FocusState.Keyboard);
    }

    private void RenderFfmpeg(string status, string detail)
    {
        var hasCopy = ffmpegInstalled is not null;
        C<TextBlock>("FfmpegExecutable").Text = ffmpegInstalled?.Path ?? (ffmpegBusy ? "Not configured yet" : "Not configured");
        C<TextBlock>("FfmpegVersion").Text = ffmpegInstalled?.Version.ToString(3) ?? "";
        C<StackPanel>("FfmpegVersionFact").Visibility = hasCopy ? Visibility.Visible : Visibility.Collapsed;
        C<FontIcon>("FfmpegStatusIcon").Glyph = status == "Up to date" ? "\uE73E" : status == "Update available" ? "\uE7BA" : "\uE946";
        C<TextBlock>("FfmpegStatus").Text = status;
        C<TextBlock>("FfmpegDetail").Text = detail;
        var check = C<Button>("FfmpegCheck");
        check.Visibility = hasCopy ? Visibility.Visible : Visibility.Collapsed;
        check.IsEnabled = !ffmpegBusy;
        var download = C<Button>("FfmpegDownload");
        download.Visibility = !hasCopy || ffmpegOffer is not null ? Visibility.Visible : Visibility.Collapsed;
        download.Content = hasCopy ? "Download update" : "Download and configure";
        download.IsEnabled = !ffmpegBusy;
    }
}
