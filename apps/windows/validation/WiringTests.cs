using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class WiringTests
{
    public static void Run(string root)
    {
        PlaybackProgressSummaryTests.Run();
        var app = Path.Combine(root, "apps/windows/YourTube.App");
        StateTests.Verify(File.Exists(Path.Combine(app, "Shell.xaml")), "native shell resource exists");
        var xml = XDocument.Load(Path.Combine(app, "Shell.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var names = xml.Root!.DescendantsAndSelf().Where(e => e.Attribute(x + "Name") is not null).ToDictionary(e => e.Attribute(x + "Name")!.Value, e => e.Name.LocalName);
        var code = File.ReadAllText(Path.Combine(app, "MainWindow.cs"));
        var libraryCode = File.ReadAllText(Path.Combine(app, "LibraryView.cs"));
        var searchCode = File.ReadAllText(Path.Combine(app, "SearchView.cs"));
        var seekCode = File.ReadAllText(Path.Combine(app, "SeekPreview.cs"));
        StateTests.Verify(names.TryGetValue("TheaterPlayer", out var playerType) && playerType == "MediaPlayerElement", "Theater uses a genuine native MediaPlayerElement");
        XElement Named(string name) => xml.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
        StateTests.Verify(names.TryGetValue("DownloadsButton", out var downloadsButtonType) && downloadsButtonType == "Button" &&
            Named("DownloadsButton").Parent == Named("LibraryNavigation") &&
            Named("DownloadsButton").ElementsAfterSelf().FirstOrDefault() == Named("SettingsButton") &&
            (string?)Named("DownloadsButton").Attribute("IsEnabled") != "False" &&
            Named("DownloadsPanel").Parent == Named("WorkspaceContent") &&
            (string?)Named("DownloadsPanel").Attribute("Visibility") == "Collapsed" &&
            (string?)Named("DownloadsPanel").Attribute("HorizontalScrollMode") == "Disabled" &&
            code.Contains("WireDownloads();") && code.Contains("RenderDownloadsRoute();") &&
            Regex.IsMatch(code, @"HistoryPanel""\)\.Visibility = ToVisibility\([^;]*!downloadsVisible"),
            "Downloads is a local native destination in the responsive rail, separate from the feed and available before sign-in");
        var player = Named("TheaterPlayer");
        var theater = Named("TheaterPanel");
        var stage = Named("PlayerStage");
        StateTests.Verify((string?)player.Attribute("Stretch") == "Uniform" &&
            (string?)theater.Attribute("HorizontalScrollMode") == "Disabled" &&
            code.Contains("PlayerStageLayout.FitHeight(viewportWidth, viewportHeight)") &&
            code.Contains("theater.SizeChanged += (_, args) => ResizePlayerStage(args.NewSize.Width, args.NewSize.Height);"),
            "the windowed stage fits the scroll viewport in both dimensions without distorting the picture");
        StateTests.Verify(stage.Parent == Named("TheaterDetails").Parent &&
            stage.ElementsAfterSelf().Contains(Named("TheaterDetails")), "details remain below the video in the same scroller");
        StateTests.Verify(names.ContainsKey("PlayerBrowserNotice") && Named("PlayerBrowserNotice").Parent == Named("TheaterDetails") &&
            (string?)Named("PlayerBrowserNotice").Attribute("AutomationProperties.LiveSetting") == "Polite" &&
            Regex.IsMatch(code, @"if \(video is not null\) ShowPlayerBrowserNotice\(message\);\s*else") &&
            Regex.IsMatch(code, @"if \(!ReferenceEquals\(selected, watchSelection\)\)\s*\{\s*ShowPlayerBrowserNotice\(null\);"),
            "a failed Open YouTube launch is announced beside the action and clears when the video changes");
        StateTests.Verify(!names.ContainsKey("TheaterHeader") && !names.ContainsKey("PlayerBackButton") &&
            !names.ContainsKey("PlayerSettingsPageButton"), "Theater has no separate navigation or app Settings row");
        var overlay = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "PlayerOverlay");
        StateTests.Verify(overlay is not null && player.Parent == overlay.Parent && (string?)player.Attribute("AreTransportControlsEnabled") == "False",
            "one custom native transport overlay shares the video stage instead of a below-video strip");
        var close = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "PlayerCloseButton");
        var transport = Named("PlayerTransport");
        var progress = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "PlayerProgressButton");
        StateTests.Verify(overlay?.Name.LocalName == "Grid" && overlay.Attribute("Background") is null &&
            close?.Name.LocalName == "Button" && close.Ancestors().Contains(overlay) && transport.Parent == overlay &&
            transport.ElementsAfterSelf().Any(e => close.Ancestors().Contains(e)) && (string?)transport.Attribute("VerticalAlignment") == "Bottom" &&
            (string?)close.Parent?.Attribute("HorizontalAlignment") == "Right" && (string?)close.Parent?.Attribute("VerticalAlignment") == "Top" &&
            (string?)close.Attribute("AutomationProperties.Name") == "Close video",
            "the top-right close action and bottom transport share a fading overlay that leaves the video surface tappable");
        StateTests.Verify(progress?.Name.LocalName == "Button" && progress.Parent == close!.Parent && progress.ElementsAfterSelf().FirstOrDefault() == close &&
            (string?)progress.Attribute("AutomationProperties.Name") == "Playback progress" &&
            progress.Descendants().Any(e => e.Name.LocalName == "Flyout" && ((string?)e.Attribute("Placement"))?.StartsWith("Bottom", StringComparison.Ordinal) == true),
            "a native playback progress flyout opens below the hamburger immediately left of the close button");
        var ns = xml.Root!.Name.Namespace;
        var download = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "PlayerDownloadButton");
        StateTests.Verify(download?.Name.LocalName == "Button" && download.Ancestors().Contains(transport) &&
            download.ElementsAfterSelf().FirstOrDefault() == Named("PlayerSettingsGear") &&
            (string?)download.Attribute("AutomationProperties.Name") == "Download video" &&
            download.Descendants(ns + "Flyout").Any(e => (string?)e.Attribute("Placement") == "TopEdgeAlignedRight" &&
                (string?)e.Attribute("FlyoutPresenterStyle") == "{StaticResource DownloadFormatsPresenter}"),
            "a native Download flyout is anchored immediately left of the player settings gear");
        var downloadCode = File.ReadAllText(Path.Combine(app, "PlaybackDownload.cs"));
        StateTests.Verify(downloadCode.Contains("Text = \"Download formats\"") && downloadCode.Contains("new ScrollViewer") &&
            downloadCode.Contains("Video + audio") && downloadCode.Contains("Audio only") &&
            downloadCode.Contains("Grid.SetColumn(detail, 1)") && downloadCode.Contains("Grid.SetColumn(button, index % columns)") &&
            downloadCode.Contains("Height is int height ? $\"{height}p\"") && downloadCode.Contains("Original audio") &&
            downloadCode.Contains("DownloadCurrentVideoAsync(catalog, option, format)"),
            "download formats are direct actions in a bounded card with labeled sections and separate format/details");
        StateTests.Verify(code.Contains("WirePlaybackDownload();") && code.Contains("playerMenuOpen || playerProgressOpen || playerDownloadOpen") &&
            code.Contains("RefreshPlaybackDownloadContext();") &&
            downloadCode.Contains("playbackResolver.GetExportOptions(catalog)") && !downloadCode.Contains("ResolveAsync("),
            "the anchored menu keeps transport visible, dismisses before fullscreen Escape, and reads only the current catalog");
        var saveCode = File.ReadAllText(Path.Combine(app, "DownloadSaveDialog.cs"));
        StateTests.Verify(downloadCode.Contains("DownloadSaveDialog.Pick(") &&
            saveCode.Contains("[new FileTypeFilter(formatLabel, \"*\" + extension)]") &&
            saveCode.Contains("NoTestFileCreate") &&
            downloadCode.Contains("new FileOpenPicker(AppWindow.Id)") && downloadCode.Contains("IsFfmpegPath(") &&
            downloadCode.Contains("OwnsDownloadCatalog(catalog)") && downloadCode.Contains("playbackResolver.ExportAsync(catalog, option, destination, ffmpegPath, progress, token, downloadJournal)"),
            "only an explicitly selected destination and, for converted formats, ffmpeg are passed to the current catalog's export");
        var downloadsCode = File.ReadAllText(Path.Combine(app, "DownloadsView.cs"));
        StateTests.Verify(Regex.IsMatch(downloadCode, @"if \(double\.IsFinite\(value\) && value >= 0\.99\) job\.MarkCommitAdmitted\(\);\s+uiProgress\.Report\(value\);") &&
            downloadCode.Contains("DownloadCommitting ? \"Saving download…\" : \"Finishing download…\"") &&
            downloadCode.Contains("if (!CanCancelDownload || downloadRequest is not { } request) return;") &&
            downloadCode.Contains("cancel.IsEnabled = transferring && CanCancelDownload;") &&
            downloadsCode.Contains("downloadCancelButton.IsEnabled = CanCancelDownload;"),
            "conversion stays cancelable; publication disables Cancel, including a queued Cancel click");
        StateTests.Verify(Regex.IsMatch(downloadCode, @"if \(closing \|\| activeDownload is null\) downloadRequest\?\.Cancel\(\);") &&
            Regex.IsMatch(downloadCode, @"if \(!OwnsDownloadCatalog\(catalog\) \|\| token.IsCancellationRequested\) return;\s*downloadPicking = false;\s*activeDownload =") &&
            Regex.IsMatch(downloadCode, @"new Progress<double>\(value =>\s*\{\s*if \(!ReferenceEquals\(downloadRequest, request\)[^;]+return;") &&
            downloadCode.Contains("RefreshDownloadsAsync()"),
            "player changes retire selection only, admitted exports report by request identity, and completion refreshes durable rows");
        StateTests.Verify(new[] { "DownloadActiveGroup", "DownloadCleanupGroup", "DownloadCompletedGroup", "DownloadsEmpty" }
            .All(name => Named(name).Ancestors().Contains(Named("DownloadsPanel"))) &&
            (string?)Named("DownloadsFeedback").Attribute("AutomationProperties.LiveSetting") == "Polite" &&
            downloadsCode.Contains("root.Loaded += (_, _) => downloadRecovery ??= InitializeDownloadsAsync()") &&
            Regex.IsMatch(downloadsCode, @"await RefreshDownloadsAsync\(\);\s*try\s*\{\s*await Task\.Run\(downloadJournal\.RecoverPending\);") &&
            downloadsCode.Contains("var snapshot = await Task.Run(downloadJournal.ReadLocal);") &&
            !downloadsCode.Contains("downloadJournal.Read()") &&
            !downloadsCode.Contains("File.Delete(") && !downloadsCode.Contains("Directory.EnumerateFiles("),
            "native Downloads groups use the stored journal and recovery is off the UI thread and limited to exact obligations");
        StateTests.Verify(!downloadCode.Contains("downloadRecovery") && !downloadCode.Contains("Preparing downloads…"),
            "pending startup recovery does not gate admission after the user picks all export inputs");
        StateTests.Verify(Regex.IsMatch(downloadCode, @"activeDownload = new\([^;]+;\s*(?://[^\r\n]*\s+)?\+\+downloadSnapshotGeneration;\s*downloadSnapshotLoading = false;") &&
            Regex.IsMatch(downloadsCode, @"if \(closed \|\| drainingWindowWatch \|\| generation != downloadSnapshotGeneration\) return;\s*hasDownloadSnapshot = true;\s*ApplyDownloadSnapshot\(snapshot\);") &&
            Regex.IsMatch(downloadsCode, @"finally\s*\{\s*if \(!closed && !drainingWindowWatch && activeDownload is null\) await RefreshDownloadsAsync\(\);"),
            "admission retires stale snapshots, and startup recovery requests a fresh local read once idle without replacing a live export row");
        var launchDownload = Regex.Match(downloadsCode, @"private async Task LaunchDownloadAsync\([^\r\n]*\)[\s\S]*?\r?\n    \}").Value;
        StateTests.Verify(Regex.IsMatch(launchDownload,
            @"try\s*\{\s*using var lease = await Task\.Run\(\(\) => downloadJournal\.AcquireCompleted\(receipt\.Id\)\);\s*if \(!IsDownloadsVisit\(visit\) \|\| !downloadSnapshot\.Completed.Contains\(receipt\)\) return;") &&
            Regex.IsMatch(launchDownload, @"StorageFile\.GetFileFromPathAsync\(lease\.Path\);\s*if \(!IsDownloadsVisit\(visit\) \|\| !downloadSnapshot\.Completed\.Contains\(receipt\)\) return;") &&
            Regex.IsMatch(launchDownload, @"else\s*\{\s*var folder = await StorageFolder\.GetFolderFromPathAsync\(Path\.GetDirectoryName\(lease\.Path\)!\);\s*if \(!IsDownloadsVisit\(visit\) \|\| !downloadSnapshot\.Completed\.Contains\(receipt\)\) return;[^}]*options\.ItemsToSelect\.Add\(file\);\s*launched = await Launcher\.LaunchFolderAsync\(folder, options\);") &&
            launchDownload.Contains("await Launcher.LaunchFileAsync(file)") && !launchDownload.Contains("ResolveCompletedPath"),
            "completed row actions hold the verified file lease through both awaited launches and recheck receipt and visit after storage awaits");
        var actionThemes = close!.Parent!.Element(ns + "StackPanel.Resources")?.Element(ns + "ResourceDictionary")?
            .Element(ns + "ResourceDictionary.ThemeDictionaries");
        string? ActionColor(string theme, string key) => (string?)actionThemes?.Elements(ns + "ResourceDictionary")
            .SingleOrDefault(e => (string?)e.Attribute(x + "Key") == theme)?.Elements(ns + "SolidColorBrush")
            .SingleOrDefault(e => (string?)e.Attribute(x + "Key") == key)?.Attribute("Color");
        foreach (var state in new[] { "PointerOver", "Pressed" })
        {
            StateTests.Verify(ActionColor("Default", "ButtonForeground" + state) == "White" &&
                Regex.IsMatch(ActionColor("Default", "ButtonBackground" + state) ?? "", @"^#[0-4][0-9A-F][0-4][0-9A-F][0-4][0-9A-F]$"),
                $"top-right player buttons retain white glyphs on an opaque dark fill over bright video in {state}");
            StateTests.Verify(ActionColor("HighContrast", "ButtonForeground" + state) == "{ThemeResource SystemColorHighlightTextColor}" &&
                ActionColor("HighContrast", "ButtonBackground" + state) == "{ThemeResource SystemColorHighlightColor}",
                $"top-right player buttons honor system high-contrast colors in {state}");
        }
        var progressCode = File.ReadAllText(Path.Combine(app, "PlaybackProgressFlyout.cs"));
        StateTests.Verify(progressCode.Contains("details.Children.Add(reachabilityDetails);") &&
            progressCode.Contains("PlaybackProgressSummary.ReachabilityLines(state.Discovery, startupSettings, includeCount: false)"),
            "reachability diagnostics stay inside Find video streams and use the sanitized formatter");
        StateTests.Verify(Regex.IsMatch(progressCode, @"progressCopy\.Click \+= \(_, _\) => CopyPlaybackSummary\(\);") &&
            Regex.IsMatch(progressCode, @"void CopyPlaybackSummary\(\)\s*\{\s*if \(closed \|\| !playerProgressOpen \|\| settingsVisible[^;]+return;") &&
            progressCode.Contains("package.SetText(PlaybackProgressSummary.Format(state, startupSettings, AppVersion));") &&
            Regex.Matches(progressCode, @"\bClipboard\.").Count == 1 &&
            progressCode.Contains("IsAllowedInHistory = false, IsRoamable = false") &&
            progressCode.Contains("AutomationProperties.SetLiveSetting(copyFeedback, AutomationLiveSetting.Polite)") &&
            progressCode.Contains("AutomationEvents.LiveRegionChanged"),
            "explicit activation copies an allowlisted summary of the current attempt with accessible clipboard feedback");
        var reachabilityChooser = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "ReachabilityChecksChooser");
        StateTests.Verify(reachabilityChooser?.Name.LocalName == "ComboBox" &&
            (string?)reachabilityChooser.Attribute("Header") == "Parallel reachability checks" &&
            reachabilityChooser.Parent == Named("StartupQualityChooser").Parent &&
            (string?)Named("ReachabilityChecksStatus").Attribute("AutomationProperties.LiveSetting") == "Polite",
            "Playback Settings has a native reachability chooser and announces a save failure");
        StateTests.Verify(code.Contains("InitializeReachabilityChecks();") &&
            code.Contains("reachabilityChecks = ReachabilityPreference.Load(path);") &&
            Regex.IsMatch(code, @"if \(ReachabilityPreference\.TrySave\(path, count\)\)\s*\{\s*reachabilityChecks = count;") &&
            code.Contains("saved == reachabilityChecks"), "the chooser restores the saved selection and rolls back a failed save");
        StateTests.Verify(Regex.IsMatch(code, @"StartStartupTiming\(\)\s*\{\s*startupSettings = new\(startupQuality, reachabilityChecks\);\s*playbackResolver\.MaxConcurrentStreamChecks = startupSettings\.ReachabilityLimit;\s*return PlaybackTiming\.StartForStartup\(\);") &&
            code.Contains("StartStartupTiming);") && Regex.Matches(code, @"playbackResolver\.MaxConcurrentStreamChecks\s*=").Count == 1 &&
            code.Contains("CreateAsync(playbackResolver, media, ct, timing, startupSettings.AutoHeight)") &&
            Regex.IsMatch(progressCode, @"displayedStartup = timing;\s*discoveryToggle\.IsChecked = false;\s*SetCopyFeedback\(string\.Empty\);"),
            "each new attempt, including Retry, captures its settings and clears the previous disclosure and clipboard feedback");
        StateTests.Verify(!progressCode.Contains("Timing is for this startup attempt", StringComparison.Ordinal) &&
            progressCode.Contains("progressNote.Visibility = ToVisibility(note.Length > 0);") &&
            progressCode.Contains("Playback started, then stopped. You can retry."),
            "successful startup has no persistent guidance; retry guidance remains available");
        StateTests.Verify(!Regex.IsMatch(progressCode, @"MaxHeight\s*=\s*\d") &&
            progressCode.Contains("scroll.MaxHeight = Math.Max(80, xamlRoot.Size.Height - 90);") &&
            progressCode.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Auto"),
            "the expanded checklist can use the available window height with scrolling only as an overflow fallback");
        var startupSample = Regex.Match(code, @"ObserveStartupClock\(!playback\.QualityBusy[^;]*;");
        StateTests.Verify(startupSample.Success && !startupSample.Value.Contains("watchProgress", StringComparison.Ordinal),
            "startup clock observation must not wait indefinitely for the History seek-completion gate");
        StateTests.Verify(code.Contains("overlay.Visibility = ToVisibility(theaterActive);") &&
            code.Contains("C<StackPanel>(\"PlayerTransport\").Visibility = ToVisibility(ready);") &&
            Regex.IsMatch(code, @"playerClock\?\.Stop\(\);\s*UpdatePlayerControlsVisibility\(\);\s*return;"),
            "the close action remains available while playback loads or has failed, independently of transport readiness");
        StateTests.Verify(code.Contains("C<Button>(\"PlayerCloseButton\").Click += (_, _) => CloseVideo();") &&
            Regex.IsMatch(code, @"void CloseVideo\(\)\s*\{[^}]*playback\.Back\(\);\s*RestoreLibraryFocus\(origin\);"),
            "close returns to the feed and restores focus to the original card");
        StateTests.Verify(code.Contains("stage.PointerEntered +=") && code.Contains("UIElement.PointerMovedEvent") &&
            code.Contains("stage.PointerExited +=") && code.Contains("overlay.OpacityTransition") &&
            Regex.IsMatch(code, @"bool show = PlayerControlsVisibility\.ShouldShow\(playerRunning, lastPlayerActivity, Environment\.TickCount64, interacting\);") &&
            code.Contains("overlay.IsHitTestVisible = show;"),
            "routed activity drives the same transport fade in both modes without leaving an invisible mouse hit target");
        var videoHitArea = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "PlayerVideoHitArea");
        StateTests.Verify(videoHitArea is not null && videoHitArea.Parent == player.Parent &&
            (string?)videoHitArea.Attribute("Background") == "Transparent" && videoHitArea.ElementsBeforeSelf().Contains(player) &&
            videoHitArea.ElementsAfterSelf().Contains(overlay!), "video clicks have a dedicated hit target behind the transport controls");
        var feedback = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "PlayerTapFeedback");
        StateTests.Verify(feedback is not null && feedback.Parent == player.Parent &&
            feedback.ElementsBeforeSelf().Contains(videoHitArea!) && feedback.ElementsAfterSelf().Contains(overlay!) &&
            (string?)feedback.Attribute("HorizontalAlignment") == "Center" && (string?)feedback.Attribute("VerticalAlignment") == "Center" &&
            (string?)feedback.Attribute("Opacity") == "0" && (string?)feedback.Attribute("IsHitTestVisible") == "False" &&
            (string?)feedback.Attribute("AutomationProperties.AccessibilityView") == "Raw" && Named("PlayerTapIcon").Ancestors().Contains(feedback),
            "play/pause feedback starts hidden, centered above the video, and never intercepts input");
        StateTests.Verify(Regex.IsMatch(code, @"C<Border>\(""PlayerVideoHitArea""\)\.Tapped\s*\+=\s*\([^\r\n]*\)\s*=>\s*\{\s*if \(args\.PointerDeviceType != Microsoft\.UI\.Input\.PointerDeviceType\.Mouse && !C<Grid>\(""PlayerOverlay""\)\.IsHitTestVisible\)\s*\{\s*lastPlayerActivity = Environment\.TickCount64;\s*UpdatePlayerControlsVisibility\(\);\s*\}\s*else TogglePlayerPlayback\(showFeedback: true\);\s*args\.Handled = true;"),
            "touch and pen taps reveal hidden controls without pausing playback; other video taps still toggle playback");
        StateTests.Verify(
            code.Contains("C<Button>(\"PlayerPlay\").Click += (_, _) => TogglePlayerPlayback();") &&
            Regex.IsMatch(code, @"void TogglePlayerPlayback\(bool showFeedback = false\)\s*\{\s*if \(InteractivePlayer is") &&
            code.Contains("if (accepted && showFeedback && ReferenceEquals(active, InteractivePlayer)) ShowPlayerTapFeedback(pause);"),
            "only video taps that send a command to the current player show feedback; the button shares the guarded toggle");
        StateTests.Verify(Regex.IsMatch(code, @"if \(playback\.QualityBusy\)\s*\{[^}]*playerTapFade\?\.Stop\(\);"),
            "feedback resets when quality switching starts, before a candidate can attach");
        var fullscreenChange = Regex.Match(code, @"private void SetFullscreen\(bool enabled, bool restoreFocus\)[\s\S]*?\r?\n    \}").Value;
        StateTests.Verify(player.Ancestors().Contains(theater) && fullscreenChange.Length > 0 &&
            !Regex.IsMatch(fullscreenChange, @"\.Children\.(Add|Remove|Clear|Insert|RemoveAt)\("),
            "entering and exiting fullscreen keeps the live media and overlay attached to the same Theater tree");
        foreach (var (name, type) in new[] { ("PlayerSeek", "Slider"), ("PlayerPlay", "Button"), ("PlayerMute", "Button"),
            ("PlayerVolume", "Slider"), ("PlayerSettingsGear", "Button"), ("PlayerFullscreen", "Button") })
            StateTests.Verify(Named(name).Name.LocalName == type && Named(name).Ancestors().Contains(transport), "native action inside the bottom transport: " + name);
        StateTests.Verify(!names.ContainsKey("PlayerQualityButton") && code.Contains("new MenuFlyoutSubItem") && code.Contains("Text = \"Quality"), "quality is under the on-video gear, not a separate selector");
        var hitArea = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "PlayerSeekHitArea");
        var slider = Named("PlayerSeek");
        StateTests.Verify(hitArea is not null && hitArea.Parent == slider.Parent && hitArea.ElementsBeforeSelf().Contains(slider) &&
            (string?)slider.Attribute("IsHitTestVisible") == "False", "pointer scrubs do not reach the native slider template before capture");
        StateTests.Verify(!names.ContainsKey("PlayerChapterMarks") && names.ContainsKey("PlayerChapterTrack") &&
            Named("PlayerChapterTrack").Parent == slider.Parent &&
            (string?)Named("PlayerChapterTrack").Attribute("IsHitTestVisible") == "False" &&
            (string?)Named("PlayerChapterTrack").Attribute("AutomationProperties.AccessibilityView") == "Raw" &&
            (string?)Named("PlayerChapterTrack").Attribute("Visibility") == "Collapsed" &&
            names.ContainsKey("PlayerSeekPreview") && Named("PlayerSeekPreview").Ancestors().Contains(overlay!) &&
            (string?)Named("PlayerSeekPreview").Attribute("IsHitTestVisible") == "False" &&
            (string?)Named("PlayerSeekPreview").Attribute("Visibility") == "Collapsed",
            "a segmented chapter track replaces tick marks without intercepting input or altering the hidden preview");
        StateTests.Verify(seekCode.Contains("ChapterTrackLayout.Create(seekChapters, slider.Maximum, width)") &&
            (string?)slider.Attribute("Opacity") == "0" && (string?)slider.Attribute("UseSystemFocusVisuals") == "False" &&
            !seekCode.Contains("slider.Opacity") &&
            Regex.IsMatch(code, @"seek\.ValueChanged \+= [^\r\n]*\s*\{\s*UpdateChapterProgress\(args\.NewValue\);\s*if \(updatingTransport"),
            "seek always uses the custom visuals while the native Slider still controls keyboard and automation input");
        StateTests.Verify(code.Contains("hitArea.PointerEntered +=") && code.Contains("hitArea.PointerExited +=") &&
            code.Contains("else PreviewSeekHover(args);") && code.Contains("SetSeekChapters(description, catalog.Duration);") &&
            code.Contains("SetSeekChapters(catalog?.Description, catalog?.Duration ?? TimeSpan.Zero);"),
            "hover previews without seeking, and both initial and deferred descriptions supply the active chapter list");
        StateTests.Verify(seekCode.Contains("ShowSeekPreview(seek.Maximum * fraction, fraction, mouseHover: true);") &&
            seekCode.Contains("UpdateChapterHover(chapter, seekPreviewMouseHover);") &&
            seekCode.Contains("chapterSegments.Count == 1 ? 0") &&
            seekCode.Contains("chapterSegments.Count > 1 ? VideoChapters.At(seekChapters, seekPreviewSeconds) : null;") &&
            seekCode.Contains("chapter is null ? timestamp :") &&
            Regex.IsMatch(seekCode, @"private void HideSeekPreview\(\)\s*\{\s*if \(closed\) return;\s*seekPreviewMouseHover = false;\s*UpdateChapterHover\(null\);"),
            "mouse hover enlarges the chapter or single continuous segment, without inventing chapter titles");
        StateTests.Verify(seekCode.Contains("double seconds = slider.Maximum * seekPreviewFraction;") &&
            seekCode.Contains("ShowSeekPreview(seconds, seekPreviewFraction, mouseHover: true);") &&
            seekCode.Contains("SetHeight(index, ChapterHeight * 1.2);") &&
            seekCode.Contains("remaining.Height = played.Height = height;"),
            "rebuilding at a new duration remaps stationary hover and both parts of that chapter grow by 20 percent");
        StateTests.Verify(Regex.IsMatch(seekCode, @"if \(layout\.Length == 0\)\s*\{[^}]*HideSeekPreview\(\);[^}]*return;"),
            "an unavailable seek track clears stale hover rather than exposing the native visuals");
        StateTests.Verify(Regex.IsMatch(seekCode, @"var layout = C<StackPanel>\(""PlayerTransport""\)\.Visibility == Visibility\.Visible\s*\? ChapterTrackLayout\.Create") &&
            seekCode.Contains("if (!slider.IsEnabled) HideSeekPreview();") && seekCode.Contains("if (!seek.IsEnabled || width <= 0"),
            "visible transport keeps the custom track during quality changes, but disabled seeking cannot show a hover preview");
        StateTests.Verify(names.ContainsKey("PlayerSeekThumbnail") && Named("PlayerSeekThumbnail").Ancestors().Contains(Named("PlayerSeekPreview")) &&
            (string?)Named("PlayerSeekThumbnail").Attribute("Visibility") == "Collapsed" &&
            code.Contains("ResetSeekStoryboard(catalog);") && seekCode.Contains("UpdateSeekThumbnail();") &&
            seekCode.Contains("playbackResolver.GetStoryboardSheetAsync(") && seekCode.Contains("OwnsSeekStoryboard(state)") &&
            seekCode.Contains("state.Board?.FrameAt(seekPreviewSeconds)"),
            "only the current seek target gets a storyboard frame, in an optional image slot inside the text preview");
        StateTests.Verify(code.Contains("hitArea.CapturePointer(args.Pointer)") && code.Contains("hitArea.PointerMoved +=") &&
            code.Contains("hitArea.PointerReleased +=") && code.Contains("if (commit && active is not null"), "captured drag previews on the hit surface and commits only on release");
        foreach (var edge in new[] { "PointerCaptureLost", "PointerCanceled" })
            StateTests.Verify(code.Split('\n').Single(line => line.Contains($"hitArea.{edge} +=")).Contains("commit: false"), "interrupted scrub cancels: " + edge);
        var scrub = Regex.Match(code, @"private void PreviewScrub\([^\r\n]*\)[\s\S]*?\r?\n    \}").Value;
        var sizing = Regex.Match(code, @"stage\.SizeChanged \+= [\s\S]*?(?=C<Button>\(""PlayerPlay""\))").Value;
        StateTests.Verify(code.Contains("if (!canSeek) CancelScrub();") &&
            Regex.IsMatch(scrub, @"!seek\.IsEnabled[^\r\n]*ReferenceEquals\(seekOwner, InteractivePlayer\)[\s\S]*?CancelScrub\(\);[\s\S]*?return;[\s\S]*?seek\.Value ="),
            "losing seekability or playback ownership invalidates a captured drag before it can preview or commit");
        StateTests.Verify(sizing.Contains("HideSeekPreview();"), "height-only stage resize invalidates the old preview geometry");
        StateTests.Verify(Regex.IsMatch(code, @"active\.Prepare\(\);\s+ApplyPlayerAudio\(active\);\s+active\.Open\(\);") &&
            code.Contains("playerAudio.ToggleMute()"),
            "new videos inherit window volume and mute before they can play");
        StateTests.Verify(code.Contains("ChangeQualityAsync(active, height") && code.Contains("playbackResolver.ForQuality(active.Catalog, height)"), "native quality menu drives the validated manifest switch");
        StateTests.Verify(!names.ContainsKey("PlayerOverlayNext") && !names.ContainsKey("PlayerNextButton") &&
            !code.Contains("playback.NextAsync()"), "Theater has no dedicated Next button; the Up next shelf remains available");
        foreach (var action in new[] { "PlayerRetryButton", "PlayerOpenYouTubeButton" })
            StateTests.Verify(code.Contains($"C<Button>(\"{action}\").Click +="), "native player action wired: " + action);
        StateTests.Verify(code.Contains("playback.Selected is not null && playback.Queue.Any(v => ReferenceEquals(v, video))"), "hidden or stale Up next activation is rejected before shared membership gate");
        StateTests.Verify(code.Contains("playback.Selected is not null || controller is not"), "hidden browse rows cannot activate over Theater");
        StateTests.Verify(code.Contains("await OpenVideoAsync(video)") && code.Contains("playback.SelectAsync(video)"), "feed and queue activation share native membership boundary");
        StateTests.Verify(names.TryGetValue("ChannelsList", out var channelType) && channelType == "ListView", "native subscribed channel list exists alongside the video feed");
        foreach (var button in new[] { "HistoryButton", "SubscriptionsButton" })
            StateTests.Verify(code.Contains($"C<Button>(\"{button}\").Click +="), "native browse navigation wired: " + button);
        StateTests.Verify(code.Contains("controller.NavigateAsync(view)") && code.Contains("controller.Channels") && code.Contains("RenderChannelRow"), "subscriptions navigation and native channel rows use real controller state");
        var channelRow = code[code.IndexOf("private void RenderChannelRow", StringComparison.Ordinal)..code.IndexOf("private void RenderHistoryRow", StringComparison.Ordinal)];
        StateTests.Verify(channelRow.Contains("Grid.SetColumn(label, 1)") && channelRow.Contains("channel?.AvatarUri") &&
            channelRow.Contains("libraryThumbnails.LoadAvatarAsync(uri, request.Token)"), "channel art precedes its label and uses the bounded public avatar loader");
        StateTests.Verify(channelRow.Contains("channel?.HasNewContent == true") && channelRow.Contains("Child = row") &&
            channelRow.Contains("Background = hasNewVideos ?") && !channelRow.Contains("Text = \"New content\"") &&
            channelRow.Contains("{name}, new videos"), "new videos highlight the whole channel row without a visible badge and remain announced");
        StateTests.Verify(channelRow.Contains("channelImages.Remove(container, out var previous)") &&
            channelRow.Split("!OwnsChannelImage(container, request)", StringSplitOptions.None).Length == 3 &&
            code.Split("CancelChannelImages();", StringSplitOptions.None).Length >= 3,
            "channel art is canceled on recycling, snapshot replacement and shutdown and checked before and after decode");
        var services = File.ReadAllText(Path.Combine(app, "WindowsServices.cs"));
        StateTests.Verify(services.Contains("=> client.GetSubscriptionsAsync(session, account, continuation, cancellationToken)"), "native subscription API forwards to the real core client");
        StateTests.Verify(code.Contains("controller.View == BrowseView.Subscriptions") && code.Contains("!settingsVisible") && code.Contains("controller.Videos.Any(v => ReferenceEquals(v, video))"), "explicit video activation is bound to the visible current feed");
        StateTests.Verify(code.Contains("new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }"), "OAuth transport refuses redirects and does not retain cookies");
        var historySwitch = Named("WatchHistorySync");
        var settingsHost = Named("SettingsHost");
        var settingsSections = Named("SettingsPanel").Element(ns + "StackPanel");
        var settingsHeading = settingsSections?.Elements(ns + "TextBlock").Single(e => (string?)e.Attribute("Text") == "Settings");
        StateTests.Verify(settingsHeading?.ElementsAfterSelf().FirstOrDefault() == settingsHost &&
            settingsHost.ElementsAfterSelf().Any(section => historySwitch.Ancestors().Contains(section)) &&
            code.Contains("C<ContentControl>(\"SettingsHost\").Content = updatesPanel.View;"),
            "the current version and updates panel is the first section under the Settings heading, before Playback");
        StateTests.Verify(historySwitch.Ancestors().Contains(Named("SettingsPanel")) &&
            (string?)historySwitch.Attribute("Header") == "Update YouTube History" && (string?)historySwitch.Attribute("IsOn") == "True",
            "Update YouTube History starts on in Playback settings");
        StateTests.Verify(code.Contains("!C<ToggleSwitch>(\"WatchHistorySync\").IsOn") && services.Contains("=> client.RecordWatchStartAsync("),
            "the switch still controls authenticated watch reporting");
        StateTests.Verify(code.Contains("watchProgress.Observe(") && code.Contains("TryRecordWatchProgressAsync(") &&
            services.Contains("=> client.RecordWatchProgressAsync("),
            "advancing native clock reports watch progress through the authenticated account client");
        var userSeek = Regex.Match(code, @"private void SetUserPlaybackPosition\([^\r\n]*\)[\s\S]*?\r?\n    \}").Value;
        StateTests.Verify(code.Contains("SetUserPlaybackPosition(active, view, TimeSpan.Zero);") &&
            code.Contains("SetUserPlaybackPosition(active, view, TimeSpan.FromSeconds(") &&
            Regex.Matches(code, @"\bview\.Position\s*=(?!=)").Count == 1 &&
            Regex.IsMatch(userSeek, @"view\.SeekCompleted \+= SeekCompleted;[\s\S]*?watchProgress\.BeginSeek\(\);\s+view\.Position = target;") &&
            userSeek.Contains("ReferenceEquals(playback.Active, active)) watchProgress.CompleteSeek(active);") &&
            code.Contains("BindWatchSeekSource(source);") && code.Contains("BindWatchSeekSource(null);"),
            "scrub, keyboard seek and replay gate watch samples before setting Position and accept completion only from the current source");
        var closeDrain = Regex.Match(code, @"private async Task FinishWindowCloseAsync\([^\r\n]*\)[\s\S]*?\r?\n    \}").Value;
        StateTests.Verify(code.Contains("new CancellationTokenSource(TimeSpan.FromMilliseconds(1500))") &&
            Regex.IsMatch(closeDrain, @"pending\.WaitAsync\(budget\.Token\)[\s\S]*?SendPendingWatchProgress\(final: true\)[\s\S]*?remaining\.WaitAsync\(budget\.Token\)") &&
            code.Contains("watchProgress.DrainAsync(final,"),
            "close waits for an in-flight checkpoint and a final multi-range drain under the same 1.5 second budget");
        var closing = Regex.Match(code, @"private void OnWindowClosing\([^\r\n]*\)[\s\S]*?\r?\n    \}").Value;
        StateTests.Verify(Regex.IsMatch(closing, @"var transfer = downloadTransfer;\s+RetirePlaybackDownload\(closing: true\);[\s\S]*?FinishWindowCloseAsync\(budget, transfer\)") &&
            Regex.IsMatch(code, @"Closed \+= \(_, _\) =>\s*\{\s*closed = true;[\s\S]*?RetirePlaybackDownload\(closing: true\);[\s\S]*?playbackResolver.Dispose\(\);") &&
            Regex.IsMatch(closeDrain, @"var exportDrain = DrainClosingExportAsync\(transfer\);[\s\S]*?await exportDrain;[\s\S]*?if \(!closed\) Close\(\);") &&
            Regex.IsMatch(code, @"private static async Task DrainClosingExportAsync\(Task\? transfer\)[\s\S]*?transfer.WaitAsync\(TimeSpan.FromSeconds\(5\)\)[\s\S]*?catch \(TimeoutException\)"),
            "close cancels the captured export immediately and gives it an independent, bounded cleanup wait");
        StateTests.Verify(Regex.IsMatch(code, @"if \(compareWatchLookups\)\s*\{[^}]*\.IsOn = false;[^}]*Compare YouTube History lookups \(diagnostic\)"),
            "the diagnostic replacement still starts off");
        StateTests.Verify(code.Contains("root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;") &&
            code.Contains("AddFullscreenKey(Windows.System.VirtualKey.F11, toggle: true);") &&
            code.Contains("AddFullscreenKey(Windows.System.VirtualKey.Escape, toggle: false);"),
            "the window hides automatic shortcut popups without losing fullscreen keys");
        StateTests.Verify(code.Contains("var watchIdentity = controller is { Phase: SessionPhase.SignedIn } ? controller.SelectedAccount : null;") &&
            code.Contains("if (!ReferenceEquals(watchStatusIdentity, watchIdentity))") &&
            code.ReplaceLineEndings("\n").Contains("watchStatusIdentity = watchIdentity;\n            ResetWatchStatus();"),
            "watch status resets on account changes or sign-out, not when the same account opens Settings");
        foreach (Match lookup in Regex.Matches(code + libraryCode + searchCode + seekCode + downloadCode + downloadsCode, "C<(?<type>\\w+)>\\(\"(?<name>[^\"]+)\"\\)"))
            StateTests.Verify(names.TryGetValue(lookup.Groups["name"].Value, out var type) && type == lookup.Groups["type"].Value, "typed native control lookup matches XML: " + lookup.Groups["name"].Value);
        var library = Named("HistoryList");
        StateTests.Verify(Regex.IsMatch(libraryCode, @"var progress = .*BrowseView\.Subscriptions.*video\.PercentWatched") &&
            libraryCode.Contains("if (progress is > 0)") && libraryCode.Contains("% watched"),
            "subscription cards show known positive progress and include the percentage in their accessible name");
        var homeButton = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "HomeButton");
        StateTests.Verify(homeButton?.Name.LocalName == "Button" && homeButton.Parent == Named("LibraryNavigation") &&
            code.Contains("C<Button>(\"HomeButton\").Click += async (_, _) => await NavigateAsync(BrowseView.Home)"),
            "native Home is reachable through the shared browse navigation");
        StateTests.Verify(Named("LibraryNavigation").Parent is { } railScroll && railScroll.Name.LocalName == "ScrollViewer" &&
            libraryCode.Contains("railScroll.HorizontalScrollMode = compact ? ScrollMode.Enabled : ScrollMode.Disabled") &&
            libraryCode.Contains("railScroll.HorizontalScrollBarVisibility = compact ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled"),
            "the narrow navigation rail keeps all four destinations reachable instead of clipping trailing buttons");
        var home = Named("HomeShelvesList");
        StateTests.Verify(home.Name.LocalName == "ListView" && home.Parent == library.Parent &&
            (string?)home.Attribute("Grid.Row") == (string?)library.Attribute("Grid.Row") &&
            (string?)home.Attribute("Visibility") == "Collapsed" &&
            code.Contains("C<ListView>(\"HomeShelvesList\").Visibility = ToVisibility(home && !search.Active)") &&
            code.Contains("C<GridView>(\"HistoryList\").Visibility = ToVisibility(!home && !search.Active)"),
            "Home and the flat feed are mutually exclusive in the same finite viewport below shared feed status");
        StateTests.Verify(Named("BrowseSearch").Parent == Named("BrowseHeader") &&
            Named("ViewHeading").Ancestors().Contains(Named("BrowseHeader")) &&
            Named("RefreshButton").Parent == Named("BrowseHeader") &&
            (string?)Named("BrowseSearch").Attribute("Grid.Column") == "1" &&
            (string?)Named("RefreshButton").Attribute("Grid.Column") == "2" &&
            Named("SearchResultsPanel").Parent == library.Parent &&
            (string?)Named("SearchResultsList").Attribute("ItemTemplate") == (string?)library.Attribute("ItemTemplate"),
            "search is beside the browse title and reuses native cards in the finite feed viewport");
        StateTests.Verify(!names.ContainsKey("HomeLoadMoreButton") &&
            Named("HomeRetryButton").Ancestors().Any(e => e.Parent == home && e.Name.LocalName == "ListView.Footer") &&
            code.Contains("ToVisibility(home && controller.AppendFailed)"),
            "Home pagination needs no button unless the next-page request failed");
        StateTests.Verify(libraryCode.Contains("found.ViewChanged += ViewChanged") &&
            libraryCode.Contains("candidate.ScrollableHeight - candidate.VerticalOffset <= candidate.ViewportHeight / 2") &&
            libraryCode.Contains("AppendFailed: false") && libraryCode.Contains("await current.LoadHistoryAsync(append: true)"),
            "scrolling near the end of Home requests its continuation but does not automatically retry failures");
        StateTests.Verify(libraryCode.Contains("feed.LayoutUpdated +=") && libraryCode.Contains("candidate.ScrollableHeight == 0") &&
            libraryCode.Contains("QueuePage(underfilled: true)"), "a laid-out Home feed too short to scroll continues even without mouse or keyboard input");
        StateTests.Verify(code.Contains("ToVisibility(browsing && busy && !current.Appending)") &&
            Regex.Matches(code, @"controller is (?:not )?\{ CanPlayFeed: true \}").Count >= 2 &&
            libraryCode.Contains("CanPlayFeed: true, View: BrowseView.Home"),
            "a pending automatic Home request cannot expose a sign-out Cancel button or block playback of existing cards");
        var shelfTemplate = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Key") == "HomeShelfTemplate");
        var shelfCards = shelfTemplate?.Descendants().SingleOrDefault(e => e.Name.LocalName == "GridView");
        StateTests.Verify(shelfCards is not null && (string?)home.Attribute("ItemTemplate") == "{StaticResource HomeShelfTemplate}" &&
            (string?)shelfCards.Attribute("ItemTemplate") == (string?)library.Attribute("ItemTemplate") &&
            (string?)shelfCards.Attribute("IsItemClickEnabled") == "True" && (string?)shelfCards.Attribute("ScrollViewer.HorizontalScrollMode") == "Enabled" &&
            (string?)shelfCards.Attribute("ScrollViewer.VerticalScrollMode") == "Disabled" && shelfCards.Descendants().Any(e =>
                e.Name.LocalName == "ItemsWrapGrid" && (string?)e.Attribute("Orientation") == "Vertical" && (string?)e.Attribute("MaximumRowsOrColumns") == "1"),
            "each Home shelf renders shared native cards in a single horizontal scroll row");
        StateTests.Verify((string?)shelfCards?.Attribute("ScrollViewer.HorizontalScrollBarVisibility") == "Hidden" &&
            libraryCode.Contains("arrows.Bind(feed, () => homeScroller)") && libraryCode.Contains("shelfArrows.SetTitle(shelf?.Title)") &&
            !libraryCode.Contains("ForwardHomeShelfWheel"),
            "Home shelves hide the scrollbar and use shared arrows and wheel routing, relabeled on recycle");
        StateTests.Verify((string?)Named("PlayerQueue").Attribute("ScrollViewer.HorizontalScrollBarVisibility") == "Hidden" &&
            code.Contains("playerQueueArrows.Bind(C<ListView>(\"PlayerQueue\"),") &&
            code.Contains("playerQueueArrows.SetTitle(playback.Queue.Count == 0 ? null : \"Up next\")"),
            "Theater's Up next shelf uses the shared controls and hides arrows for an empty queue");
        StateTests.Verify(code.Contains("RenderHomeShelves(signedIn && home ? controller.HomeShelves : [])") &&
            libraryCode.Contains("header.Text = shelf.Title") && libraryCode.Contains("feed.ItemsSource = shelf.Videos") &&
            libraryCode.Contains("homeShelves.Add(shelves[i])") && Regex.IsMatch(libraryCode, @"if \(!keepsPrefix\)\s*\{[^}]*homeShelves\.Clear\(\)"),
            "Home displays the ordered controller shelves and preserves existing rows on append");
        StateTests.Verify(libraryCode.Contains("current.HomeShelves.Any(candidate => ReferenceEquals(candidate, shelf))") &&
            libraryCode.Contains("shelf.Videos.Any(candidate => ReferenceEquals(candidate, video))") &&
            libraryCode.Contains("await OpenVideoAsync(video)") && libraryCode.Contains("RestoreHomeFocus(origin)"),
            "only current shelf cards open the native player, and Back targets the originating shelf");
        StateTests.Verify(Named("SettingsButton").Ancestors().Contains(Named("LibraryNavigation")), "Settings belongs to the same rail as the browse feeds");
        StateTests.Verify(libraryCode.Contains("else button.ClearValue(Control.ForegroundProperty);") && !libraryCode.Contains("button.Foreground = active ?"),
            "inactive navigation destinations restore the theme foreground instead of overriding it with a null brush");
        var titleBar = Named("TitleBar");
        var brand = Named("AppBrand");
        StateTests.Verify(!names.ContainsKey("ShellHeader") && brand.Parent == titleBar &&
            (string?)brand.Attribute("HorizontalAlignment") == "Left" &&
            brand.Descendants().Any(e => e.Name.LocalName == "FontIcon") &&
            brand.Elements().Any(e => (string?)e.Attribute("Text") == "YourTube"),
            "the app has no separate top bar; its brand is left aligned in the native title bar");
        var accountFooter = Named("ShellFooter");
        var accountButton = Named("AccountButton");
        StateTests.Verify(accountFooter.Parent == xml.Root && (string?)accountFooter.Attribute("Grid.Row") == "2" &&
            accountButton.Parent == accountFooter && (string?)accountButton.Attribute("HorizontalAlignment") == "Left" &&
            (string?)accountButton.Descendants().Single(e => e.Name.LocalName == "MenuFlyout").Attribute("Placement") == "TopEdgeAlignedLeft" &&
            code.Contains("C<Grid>(\"ShellFooter\").Visibility = ToVisibility(!showTheater);") &&
            !fullscreenChange.Contains("ShellFooter") &&
            fullscreenChange.Contains("Grid.SetRow(theater, enabled ? 0 : 1);") &&
            (string?)theater.Attribute("Grid.Row") == "1" && !names.ContainsKey("AccountChooser"),
            "the account menu opens upward from the bottom left outside Theater and stays hidden during fullscreen transitions");
        StateTests.Verify(names.TryGetValue("AccountAvatar", out var avatarType) && avatarType == "Ellipse" &&
            Named("AccountAvatar").Ancestors().Contains(accountButton) &&
            (string?)Named("AccountAvatar").Attribute("Visibility") == "Collapsed" &&
            code.Contains("RenderAccountAvatar(signedIn ? controller.SelectedAccount : null)") &&
            code.Contains("ReferenceEquals(account, avatarAccount)") &&
            code.Contains("avatarRequest?.Cancel()"), "the account picture starts hidden and follows the selected identity without stale completions");
        StateTests.Verify(!names.ContainsKey("LoadMoreButton") && libraryCode.Contains("WireFeedPaging(feed, home: false)") &&
            Named("FeedRetryButton").Ancestors().Any(e => e.Parent == library && e.Name.LocalName == "GridView.Footer") &&
            code.Contains("ToVisibility(!home && controller.AppendFailed)"),
            "Subscriptions and History page automatically through their grid and show a footer action only when paging is paused");
        StateTests.Verify(Named("FeedStatus").Ancestors().Contains(Named("LibraryContent")) && (string?)Named("ShellStatus").Attribute("Visibility") == "Collapsed", "feed feedback is inline and the shell has no permanent status footer");
        StateTests.Verify(library.Name.LocalName == "GridView" && library.Descendants().Any(e =>
            e.Name.LocalName == "ItemsWrapGrid" && (string?)e.Attribute("Orientation") == "Horizontal") &&
            (string?)library.Attribute("IsItemClickEnabled") == "True" && names["SettingsHost"] == "ContentControl",
            "Library uses a native virtualized wrapping grid with item activation and retains Settings");
        StateTests.Verify(Named("ChannelsList").Ancestors().Contains(Named("SubscriptionRail")) &&
            Named("SubscriptionRail").Parent == library.Parent &&
            (string?)library.Attribute("Grid.Column") == "1" &&
            (string?)Named("ChannelsList").Attribute("SelectionMode") == "Single" &&
            !code.Contains("Latest videos from your channels") &&
            Named("SubscriptionsButton").Ancestors().Contains(Named("LibraryNavigation")) &&
            Named("HistoryButton").Ancestors().Contains(Named("LibraryNavigation")),
            "Subscriptions uses a selectable inline rail left of the existing video grid without the old subtitle");
        StateTests.Verify(code.Contains("libraryOrigin = video;") && code.Contains("RestoreLibraryFocus(origin)") &&
            libraryCode.Contains("ReferenceEquals(video, origin)") && libraryCode.Contains("feed.ScrollIntoView(origin)"),
            "Back restores the originating Library card, only if it still belongs to the active feed");
        StateTests.Verify(xml.Descendants().All(e => e.Attribute(x + "Class") is null && !e.Attributes().Any(a => a.Value.Contains("{x:Bind"))), "runtime XAML needs no generated code");
        StateTests.Verify(!xml.ToString().Contains("WebView"), "no embedded login browser");
        foreach (var button in new[] { "SignInButton", "OpenActivationButton", "CancelButton", "RefreshButton", "FeedRetryButton", "SettingsButton" })
            StateTests.Verify(code.Contains($"C<Button>(\"{button}\").Click +="), "native action wired: " + button);
        StateTests.Verify(code.Contains("await current.SelectAccountAsync(account)") && code.Contains("current.SignOut()"), "account menu uses the existing identity and sign-out boundaries");
        StateTests.Verify(Regex.IsMatch(code, @"Content = root;\s*var captionBackground = [^;]+;\s*titleBar = new NativeTitleBar\(this, C<Border>\(""TitleBar""\),") &&
            fullscreenChange.Contains("if (!enabled) titleBar.RefreshInsets();") && code.Contains("titleBar.Dispose();") &&
            !code.Contains("AppWindow.TitleBar.") && !code.Contains("ExtendsContentIntoTitleBar"),
            "the shared native title bar owns caption styling and insets; fullscreen exit and close keep their lifecycle wiring");
        StateTests.Verify(code.Contains("SettingsHost\").Content = updatesPanel.View") && code.Contains("updatesPanel.Dispose()"), "Settings updater integration and disposal");
        StateTests.Verify(Regex.IsMatch(code, @"root\.Loaded\s*\+=\s*async[^;]*if \(initialized \|\| closed\) return;\s*initialized\s*=\s*true;\s*_\s*=\s*updatesPanel\.CheckOnStartupAsync\(\);[^;]*;\s*await InitializeServicesAsync\(\);"),
            "startup discovery is scheduled once without awaiting it or depending on account restoration");
        var dot = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "SettingsUpdateDot");
        StateTests.Verify(dot is not null && dot.Ancestors().Contains(Named("SettingsButton")) &&
            (string?)dot.Attribute("Visibility") == "Collapsed" && (string?)dot.Attribute("IsHitTestVisible") == "False" &&
            (string?)dot.Attribute("AutomationProperties.AccessibilityView") == "Raw", "Settings has a hidden, decorative update dot");
        StateTests.Verify(code.Contains("updatesPanel.UpdateAvailabilityChanged += RenderSettingsButton;") &&
            code.Contains("!settingsVisible && updatesPanel.HasUpdate") && code.Contains("Settings, update available"),
            "Settings badge follows updater state and announces the update through the existing button");

        var program = File.ReadAllText(Path.Combine(app, "Program.cs"));
        StateTests.Verify(program.IndexOf("SetAutoApplyOnStartup(false)", StringComparison.Ordinal) < program.IndexOf("InitializeComWrappers", StringComparison.Ordinal), "explicit Velopack lifecycle runs before WinUI");
        var project = XDocument.Load(Path.Combine(app, "YourTube.App.csproj"));
        StateTests.Verify(project.Descendants("WindowsAppSDKSelfContained").Single().Value == "true" && project.Descendants("SelfContained").Single().Value == "true", "native self-contained defaults preserved");
        StateTests.Verify(!project.Descendants("AssemblyVersion").Any() && !project.Descendants("FileVersion").Any(), "CI Version derives installed assembly/file version without frozen overrides");
        StateTests.Verify(project.Descendants("EmbeddedResource").Any(e => (string?)e.Attribute("LogicalName") == "YourTube.App.Shell.xaml"), "runtime shell logical resource name");
        StateTests.Verify(project.Descendants("ProjectReference").Any(e => (string?)e.Attribute("Include") == "../YourTube.Playback/YourTube.Playback.csproj"), "native app references the genuine credential-isolated resolver");
        var notice = project.Descendants("Content").Where(e => (string?)e.Attribute("Link") == "THIRD-PARTY-NOTICES.md").ToArray();
        StateTests.Verify(notice.Length == 1 && notice[0].Attribute("CopyToOutputDirectory")?.Value == "PreserveNewest" && notice[0].Attribute("CopyToPublishDirectory")?.Value == "PreserveNewest", "distributable notices are copied exactly once");
        var adapter = File.ReadAllText(Path.Combine(app, "NativePlaybackSource.cs"));
        foreach (var requirement in new[] { "AdaptiveMediaSource.CreateFromStreamAsync", "MediaSource.CreateFromAdaptiveMediaSource", "GetDeferral()", "resolver.ReadResourceAsync", "new DenyNetworkFilter()", "args.Result.ExtendedStatus", "deferral?.Complete()", "cancellation.Cancel()", "DownloadRequested -= DownloadRequested" })
            StateTests.Verify(adapter.Contains(requirement), "safe shared native AMS adapter: " + requirement);
        StateTests.Verify(!adapter.Contains("MainWindow") && !adapter.Contains("YourTube.Core"), "native adapter is source-linkable and independent of account/UI transport");
        Console.WriteLine("PASS: XML, typed controls, native action wiring, lifecycle and publish-default gates.");
    }
}
