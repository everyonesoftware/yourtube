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
        StateTests.Verify(names.TryGetValue("TheaterPlayer", out var playerType) && playerType == "MediaPlayerElement", "Theater uses a genuine native MediaPlayerElement");
        XElement Named(string name) => xml.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
        var player = Named("TheaterPlayer");
        var overlay = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "PlayerOverlay");
        StateTests.Verify(overlay is not null && player.Parent == overlay.Parent && (string?)player.Attribute("AreTransportControlsEnabled") == "False",
            "one custom native transport overlay shares the video stage instead of a below-video strip");
        var videoHitArea = xml.Descendants().SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "PlayerVideoHitArea");
        StateTests.Verify(videoHitArea is not null && videoHitArea.Parent == player.Parent &&
            (string?)videoHitArea.Attribute("Background") == "Transparent" && videoHitArea.ElementsBeforeSelf().Contains(player) &&
            videoHitArea.ElementsAfterSelf().Contains(overlay!), "video clicks have a dedicated hit target behind the transport controls");
        StateTests.Verify(Regex.IsMatch(code, @"C<Border>\(""PlayerVideoHitArea""\)\.Tapped\s*\+=\s*\([^\r\n]*\)\s*=>\s*\{?\s*TogglePlayerPlayback\(\)") &&
            code.Contains("C<Button>(\"PlayerPlay\").Click += (_, _) => TogglePlayerPlayback();") &&
            Regex.IsMatch(code, @"void TogglePlayerPlayback\(\)\s*\{\s*if \(InteractivePlayer is"),
            "video clicks and the play button share the active-player guard and toggle");
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
        foreach (var action in new[] { "PlayerBackButton", "PlayerRetryButton", "PlayerOpenYouTubeButton", "PlayerNextButton" })
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
        StateTests.Verify(code.Contains("new HttpClientHandler { AllowAutoRedirect = false }"), "OAuth transport refuses redirects that could forward credentials");
        foreach (Match lookup in Regex.Matches(code, "C<(?<type>\\w+)>\\(\"(?<name>[^\"]+)\"\\)"))
            StateTests.Verify(names.TryGetValue(lookup.Groups["name"].Value, out var type) && type == lookup.Groups["type"].Value, "typed native control lookup matches XML: " + lookup.Groups["name"].Value);
        StateTests.Verify(names["HistoryList"] == "ListView" && names["SettingsHost"] == "ContentControl", "real native history and settings host");
        StateTests.Verify(xml.Descendants().All(e => e.Attribute(x + "Class") is null && !e.Attributes().Any(a => a.Value.Contains("{x:Bind"))), "runtime XAML needs no generated code");
        StateTests.Verify(!xml.ToString().Contains("WebView"), "no embedded login browser");
        foreach (var button in new[] { "SignInButton", "OpenActivationButton", "CancelButton", "RefreshButton", "LoadMoreButton", "SignOutButton", "SettingsButton" })
            StateTests.Verify(code.Contains($"C<Button>(\"{button}\").Click +="), "native action wired: " + button);
        StateTests.Verify(code.Contains("SetTitleBar(C<Border>(\"TitleBar\"))") && code.Contains("ExtendsContentIntoTitleBar = true"), "Music native titlebar APIs wired");
        StateTests.Verify(code.Contains("SettingsHost\").Content = updatesPanel.View") && code.Contains("updatesPanel.Dispose()"), "Settings updater integration and disposal");
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
