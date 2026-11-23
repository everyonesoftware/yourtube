using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class WiringTests
{
    public static void Run(string root)
    {
        var app = Path.Combine(root, "apps/windows/YourTube.App");
        StateTests.Verify(File.Exists(Path.Combine(app, "Shell.xaml")), "native shell resource exists");
        var xml = XDocument.Load(Path.Combine(app, "Shell.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var names = xml.Root!.DescendantsAndSelf().Where(e => e.Attribute(x + "Name") is not null).ToDictionary(e => e.Attribute(x + "Name")!.Value, e => e.Name.LocalName);
        var code = File.ReadAllText(Path.Combine(app, "MainWindow.cs"));
        var libraryCode = File.ReadAllText(Path.Combine(app, "LibraryView.cs"));
        StateTests.Verify(names.TryGetValue("TheaterPlayer", out var playerType) && playerType == "MediaPlayerElement", "Theater uses a genuine native MediaPlayerElement");
        XElement Named(string name) => xml.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
        var player = Named("TheaterPlayer");
        var overlay = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "PlayerOverlay");
        StateTests.Verify(overlay is not null && player.Parent == overlay.Parent && (string?)player.Attribute("AreTransportControlsEnabled") == "False",
            "one custom native transport overlay shares the video stage instead of a below-video strip");
        var close = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "PlayerCloseButton");
        StateTests.Verify(close?.Name.LocalName == "Button" && close.Parent == player.Parent && close.ElementsBeforeSelf().Contains(overlay!) &&
            (string?)close.Attribute("HorizontalAlignment") == "Right" && (string?)close.Attribute("VerticalAlignment") == "Top" &&
            (string?)close.Attribute("AutomationProperties.Name") == "Close video",
            "the top-right close action is on the stage and remains reachable independently of the transport");
        StateTests.Verify(code.Contains("C<Button>(\"PlayerCloseButton\").Click += (_, _) => CloseVideo();") &&
            code.Contains("C<Button>(\"PlayerBackButton\").Click += (_, _) => CloseVideo();") &&
            Regex.IsMatch(code, @"void CloseVideo\(\)\s*\{[^}]*playback\.Back\(\);\s*RestoreLibraryFocus\(origin\);"),
            "close and Back share the existing feed return and restore focus to the original card");
        StateTests.Verify(code.Contains("stage.PointerEntered +=") && code.Contains("UIElement.PointerMovedEvent") &&
            code.Contains("stage.PointerExited +=") && code.Contains("overlay.OpacityTransition") &&
            code.Contains("overlay.IsHitTestVisible = show;"),
            "routed activity drives the transport fade without leaving an invisible mouse hit target");
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
        StateTests.Verify(Regex.IsMatch(code, @"C<Border>\(""PlayerVideoHitArea""\)\.Tapped\s*\+=\s*\([^\r\n]*\)\s*=>\s*\{?\s*TogglePlayerPlayback\(showFeedback: true\)") &&
            code.Contains("C<Button>(\"PlayerPlay\").Click += (_, _) => TogglePlayerPlayback();") &&
            Regex.IsMatch(code, @"void TogglePlayerPlayback\(bool showFeedback = false\)\s*\{\s*if \(InteractivePlayer is") &&
            code.Contains("if (accepted && showFeedback && ReferenceEquals(active, InteractivePlayer)) ShowPlayerTapFeedback(pause);"),
            "only video taps that send a command to the current player show feedback; the button shares the guarded toggle");
        StateTests.Verify(Regex.IsMatch(code, @"if \(playback\.QualityBusy\)\s*\{[^}]*playerTapFade\?\.Stop\(\);"),
            "feedback resets when quality switching starts, before a candidate can attach");
        var theater = Named("TheaterPanel");
        var fullscreenChange = Regex.Match(code, @"private void SetFullscreen\(bool enabled, bool restoreFocus\)[\s\S]*?\r?\n    \}").Value;
        StateTests.Verify(player.Ancestors().Contains(theater) && fullscreenChange.Length > 0 &&
            !Regex.IsMatch(fullscreenChange, @"\.Children\.(Add|Remove|Clear|Insert|RemoveAt)\("),
            "entering and exiting fullscreen keeps the live media and overlay attached to the same Theater tree");
        foreach (var (name, type) in new[] { ("PlayerSeek", "Slider"), ("PlayerPlay", "Button"), ("PlayerMute", "Button"),
            ("PlayerVolume", "Slider"), ("PlayerSettingsGear", "Button"), ("PlayerFullscreen", "Button") })
            StateTests.Verify(Named(name).Name.LocalName == type && Named(name).Ancestors().Contains(overlay!), "native action inside the video overlay: " + name);
        StateTests.Verify(!names.ContainsKey("PlayerQualityButton") && code.Contains("new MenuFlyoutSubItem") && code.Contains("Text = \"Quality"), "quality is under the on-video gear, not a separate selector");
        var hitArea = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "PlayerSeekHitArea");
        var slider = Named("PlayerSeek");
        StateTests.Verify(hitArea is not null && hitArea.Parent == slider.Parent && hitArea.ElementsBeforeSelf().Contains(slider) &&
            (string?)slider.Attribute("IsHitTestVisible") == "False", "pointer scrubs do not reach the native slider template before capture");
        StateTests.Verify(code.Contains("hitArea.CapturePointer(args.Pointer)") && code.Contains("hitArea.PointerMoved +=") &&
            code.Contains("hitArea.PointerReleased +=") && code.Contains("if (commit && active is not null"), "captured drag previews on the hit surface and commits only on release");
        foreach (var edge in new[] { "PointerCaptureLost", "PointerCanceled" })
            StateTests.Verify(code.Split('\n').Single(line => line.Contains($"hitArea.{edge} +=")).Contains("commit: false"), "interrupted scrub cancels: " + edge);
        StateTests.Verify(Regex.IsMatch(code, @"active\.Prepare\(\);\s+ApplyPlayerAudio\(active\);\s+active\.Open\(\);") &&
            code.Contains("playerAudio.ToggleMute()"),
            "new videos inherit window volume and mute before they can play");
        StateTests.Verify(code.Contains("ChangeQualityAsync(active, height") && code.Contains("playbackResolver.ForQuality(active.Catalog, height)"), "native quality menu drives the validated manifest switch");
        StateTests.Verify(!names.ContainsKey("PlayerOverlayNext") && !names.ContainsKey("PlayerNextButton") &&
            !code.Contains("playback.NextAsync()"), "Theater has no dedicated Next button; the Up next shelf remains available");
        foreach (var action in new[] { "PlayerBackButton", "PlayerRetryButton", "PlayerOpenYouTubeButton" })
            StateTests.Verify(code.Contains($"C<Button>(\"{action}\").Click +="), "native player action wired: " + action);
        StateTests.Verify(code.Contains("playback.Selected is not null && playback.Queue.Any(v => ReferenceEquals(v, video))"), "hidden or stale Up next activation is rejected before shared membership gate");
        StateTests.Verify(code.Contains("playback.Selected is not null || controller is not"), "hidden browse rows cannot activate over Theater");
        StateTests.Verify(code.Contains("await OpenVideoAsync(video)") && code.Contains("playback.SelectAsync(video)"), "feed and queue activation share native membership boundary");
        StateTests.Verify(names.TryGetValue("ChannelsList", out var channelType) && channelType == "ListView", "native subscribed channel list exists alongside the video feed");
        foreach (var button in new[] { "HistoryButton", "SubscriptionsButton" })
            StateTests.Verify(code.Contains($"C<Button>(\"{button}\").Click +="), "native browse navigation wired: " + button);
        StateTests.Verify(code.Contains("controller.NavigateAsync(view)") && code.Contains("controller.Channels") && code.Contains("RenderChannelRow"), "subscriptions navigation and native channel rows use real controller state");
        var services = File.ReadAllText(Path.Combine(app, "WindowsServices.cs"));
        StateTests.Verify(services.Contains("=> client.GetSubscriptionsAsync(session, account, continuation, cancellationToken)"), "native subscription API forwards to the real core client");
        StateTests.Verify(code.Contains("controller.View == BrowseView.Subscriptions") && code.Contains("!settingsVisible") && code.Contains("controller.Videos.Any(v => ReferenceEquals(v, video))"), "explicit video activation is bound to the visible current feed");
        StateTests.Verify(code.Contains("new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }"), "OAuth transport refuses redirects and does not retain cookies");
        var historySwitch = Named("WatchHistorySync");
        StateTests.Verify(historySwitch.Ancestors().Contains(Named("SettingsPanel")) &&
            (string?)historySwitch.Attribute("Header") == "Update YouTube History" && (string?)historySwitch.Attribute("IsOn") == "True",
            "Update YouTube History starts on in Playback settings");
        StateTests.Verify(code.Contains("!C<ToggleSwitch>(\"WatchHistorySync\").IsOn") && services.Contains("=> client.RecordWatchStartAsync("),
            "the switch still controls authenticated watch reporting");
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
        foreach (Match lookup in Regex.Matches(code + libraryCode, "C<(?<type>\\w+)>\\(\"(?<name>[^\"]+)\"\\)"))
            StateTests.Verify(names.TryGetValue(lookup.Groups["name"].Value, out var type) && type == lookup.Groups["type"].Value, "typed native control lookup matches XML: " + lookup.Groups["name"].Value);
        var library = Named("HistoryList");
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
            code.Contains("C<ListView>(\"HomeShelvesList\").Visibility = ToVisibility(home)") &&
            code.Contains("C<GridView>(\"HistoryList\").Visibility = ToVisibility(!home)"),
            "Home and the flat feed are mutually exclusive in the same finite viewport below shared feed status");
        StateTests.Verify(Named("HomeLoadMoreButton").Ancestors().Any(e => e.Parent == home && e.Name.LocalName == "ListView.Footer") &&
            code.Contains("C<Button>(\"HomeLoadMoreButton\").Click +=") && code.Contains("LoadHistoryAsync(append: true)"),
            "Home pagination is explicit and follows the shelves in their scroller");
        var shelfTemplate = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Key") == "HomeShelfTemplate");
        var shelfCards = shelfTemplate?.Descendants().SingleOrDefault(e => e.Name.LocalName == "GridView");
        StateTests.Verify(shelfCards is not null && (string?)home.Attribute("ItemTemplate") == "{StaticResource HomeShelfTemplate}" &&
            (string?)shelfCards.Attribute("ItemTemplate") == (string?)library.Attribute("ItemTemplate") &&
            (string?)shelfCards.Attribute("IsItemClickEnabled") == "True" && (string?)shelfCards.Attribute("ScrollViewer.HorizontalScrollMode") == "Enabled" &&
            (string?)shelfCards.Attribute("ScrollViewer.VerticalScrollMode") == "Disabled" && shelfCards.Descendants().Any(e =>
                e.Name.LocalName == "ItemsWrapGrid" && (string?)e.Attribute("Orientation") == "Vertical" && (string?)e.Attribute("MaximumRowsOrColumns") == "1"),
            "each Home shelf renders shared native cards in a single horizontal scroll row");
        var bindShelf = Regex.Match(libraryCode, @"private void RenderHomeShelf\([^\r\n]*\)[\s\S]*?\r?\n    \}").Value;
        StateTests.Verify(Regex.IsMatch(bindShelf,
            @"feed\.RemoveHandler\(UIElement\.PointerWheelChangedEvent, homeShelfWheelHandler\);[\s\S]*?if \(shelf is null\) return;[\s\S]*?feed\.AddHandler\(UIElement\.PointerWheelChangedEvent, homeShelfWheelHandler, handledEventsToo: true\);"),
            "recycled Home shelf grids forward wheel input even when the inner grid has handled it, without accumulating handlers");
        var shelfWheel = Regex.Match(libraryCode, @"private void ForwardHomeShelfWheel\([^\r\n]*\)[\s\S]*?\r?\n    \}").Value;
        StateTests.Verify(bindShelf.Contains("homeShelfWheelHandler ??= ForwardHomeShelfWheel;") &&
            Regex.IsMatch(shelfWheel, @"if \([^\r\n]*IsHorizontalMouseWheel[^\r\n]*KeyModifiers != VirtualKeyModifiers\.None\) return;") &&
            shelfWheel.Contains("C<ListView>(\"HomeShelvesList\")") && shelfWheel.Contains("VisualTreeHelper.GetParent") &&
            Regex.IsMatch(shelfWheel, @"\.ChangeView\(null, [^,]+, null, disableAnimation: true\)") && shelfWheel.Contains("args.Handled = true;"),
            "an ordinary wheel turn over a Home card scrolls only the outer list vertically, leaving horizontal and modified gestures to WinUI");
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
            brand.Elements().Any(e => (string?)e.Attribute("Text") == "YourTube") &&
            code.Contains("AppWindow.TitleBar.LeftInset / xamlRoot.RasterizationScale") &&
            code.Contains("AppWindow.TitleBar.RightInset / xamlRoot.RasterizationScale"),
            "the app has no separate top bar; its brand is left aligned in the native title bar");
        var accountFooter = Named("ShellFooter");
        var accountButton = Named("AccountButton");
        StateTests.Verify(accountFooter.Parent == xml.Root && (string?)accountFooter.Attribute("Grid.Row") == "2" &&
            accountButton.Parent == accountFooter && (string?)accountButton.Attribute("HorizontalAlignment") == "Left" &&
            (string?)accountButton.Descendants().Single(e => e.Name.LocalName == "MenuFlyout").Attribute("Placement") == "TopEdgeAlignedLeft" &&
            fullscreenChange.Contains("C<Grid>(\"ShellFooter\").Visibility = chromeVisibility;") &&
            fullscreenChange.Contains("Grid.SetRow(theater, enabled ? 0 : 1);") &&
            (string?)theater.Attribute("Grid.Row") == "1" && !names.ContainsKey("AccountChooser"),
            "the account menu anchors to the bottom left in every windowed view and opens upward");
        StateTests.Verify(names.TryGetValue("AccountAvatar", out var avatarType) && avatarType == "Ellipse" &&
            Named("AccountAvatar").Ancestors().Contains(accountButton) &&
            (string?)Named("AccountAvatar").Attribute("Visibility") == "Collapsed" &&
            code.Contains("RenderAccountAvatar(signedIn ? controller.SelectedAccount : null)") &&
            code.Contains("ReferenceEquals(account, avatarAccount)") &&
            code.Contains("avatarRequest?.Cancel()"), "the account picture starts hidden and follows the selected identity without stale completions");
        StateTests.Verify(Named("LoadMoreButton").Ancestors().Any(e => e.Parent == library && e.Name.LocalName == "GridView.Footer"), "pagination follows the virtualized cards inside the feed scroller");
        StateTests.Verify(Named("FeedStatus").Ancestors().Contains(Named("LibraryContent")) && (string?)Named("ShellStatus").Attribute("Visibility") == "Collapsed", "feed feedback is inline and the shell has no permanent status footer");
        StateTests.Verify(library.Name.LocalName == "GridView" && library.Descendants().Any(e =>
            e.Name.LocalName == "ItemsWrapGrid" && (string?)e.Attribute("Orientation") == "Horizontal") &&
            (string?)library.Attribute("IsItemClickEnabled") == "True" && names["SettingsHost"] == "ContentControl",
            "Library uses a native virtualized wrapping grid with item activation and retains Settings");
        StateTests.Verify(Named("ChannelsList").Ancestors().Any(e => e.Name.LocalName == "Expander") &&
            Named("SubscriptionsButton").Ancestors().Contains(Named("LibraryNavigation")) &&
            Named("HistoryButton").Ancestors().Contains(Named("LibraryNavigation")),
            "Library uses a dedicated feed rail and a disclosure for read-only channels");
        StateTests.Verify(code.Contains("libraryOrigin = video;") && code.Contains("RestoreLibraryFocus(origin)") &&
            libraryCode.Contains("ReferenceEquals(video, origin)") && libraryCode.Contains("feed.ScrollIntoView(origin)"),
            "Back restores the originating Library card, only if it still belongs to the active feed");
        StateTests.Verify(xml.Descendants().All(e => e.Attribute(x + "Class") is null && !e.Attributes().Any(a => a.Value.Contains("{x:Bind"))), "runtime XAML needs no generated code");
        StateTests.Verify(!xml.ToString().Contains("WebView"), "no embedded login browser");
        foreach (var button in new[] { "SignInButton", "OpenActivationButton", "CancelButton", "RefreshButton", "LoadMoreButton", "SettingsButton" })
            StateTests.Verify(code.Contains($"C<Button>(\"{button}\").Click +="), "native action wired: " + button);
        StateTests.Verify(code.Contains("await current.SelectAccountAsync(account)") && code.Contains("current.SignOut()"), "account menu uses the existing identity and sign-out boundaries");
        StateTests.Verify(code.Contains("SetTitleBar(C<Border>(\"TitleBar\"))") && code.Contains("ExtendsContentIntoTitleBar = true"), "Music native titlebar APIs wired");
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
        StateTests.Verify(Named("PlayerSettingsPageButton").Descendants().Any(e => (string?)e.Attribute(x + "Name") == "PlayerSettingsUpdateDot" &&
            (string?)e.Attribute("Visibility") == "Collapsed") && code.Contains("C<Ellipse>(\"PlayerSettingsUpdateDot\").Visibility = ToVisibility(showDot)") &&
            code.Contains("AutomationProperties.SetName(C<Button>(\"PlayerSettingsPageButton\"), accessibleName)"),
            "the visible Settings button in windowed Theater shows and announces the same available update");
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
