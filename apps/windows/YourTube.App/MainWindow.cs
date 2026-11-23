using System.Reflection;
using Microsoft.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Windows.System;
using Windows.Media.Playback;
using YourTube.Core;
using YourTube.Playback;

namespace YourTube.App;

internal sealed partial class MainWindow : Window
{
    private const string PlayGlyph = "\uE768";
    private const string PauseGlyph = "\uE769";
    private readonly Grid root;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private readonly WindowsUpdatesPanel updatesPanel;
    private readonly PlaybackResolver playbackResolver = new();
    private readonly PlaybackController<NativePlaybackSession> playback;
    private readonly bool compareWatchLookups = Environment.GetEnvironmentVariable("YOURTUBE_WATCH_LOOKUP_COMPARE") == "1";
    private int startupQuality;
    private NativePlaybackSession? boundSession;
    private HistoryVideo? watchSelection;
    private TimeSpan? lastWatchPosition;
    private bool watchAttempted;
    private YouTubeAccount? watchStatusIdentity;
    private long watchStatusGeneration;
    private CancellationTokenSource? watchRequest;
    private DispatcherQueueTimer? playerClock;
    private Storyboard? playerTapFade;
    private bool updatingTransport;
    private uint? seekPointer;
    private NativePlaybackSession? seekOwner;
    private bool fullscreen;
    private bool playerMenuOpen;
    private readonly PlayerAudioState playerAudio = new();
    private AppWindowPresenter? windowedPresenter;
    private PlaybackMedia? descriptionCatalog;
    private CancellationTokenSource? descriptionRequest;
    private SessionController? controller;
    private IReadOnlyList<HistoryVideo>? displayedVideos;
    private IReadOnlyList<SubscribedChannel>? displayedChannels;
    private BrowseView lastBrowseView = BrowseView.History;
    private string? browserNotice;
    private bool settingsVisible;
    private bool initialized;
    private bool closed;

    public MainWindow()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("YourTube.App.Shell.xaml") ?? throw new InvalidOperationException("Missing native shell resource.");
        using var reader = new StreamReader(stream);
        root = (Grid)XamlReader.Load(reader.ReadToEnd());
        Title = "YourTube";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "YourTube.ico"));
        Content = root;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(C<Border>("TitleBar"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 800));
        AppWindow.TitleBar.ButtonBackgroundColor = ColorHelper.FromArgb(255, 36, 44, 40);
        AppWindow.TitleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 237, 240, 233);
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = ColorHelper.FromArgb(255, 36, 44, 40);
        AppWindow.TitleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 172, 184, 174);

        // Integration point: updater owns its native view and transport, never auth.
        updatesPanel = new WindowsUpdatesPanel();
        updatesPanel.UpdateAvailabilityChanged += RenderSettingsButton;
        C<ContentControl>("SettingsHost").Content = updatesPanel.View;
        InitializeStartupQuality();
        if (compareWatchLookups)
        {
            C<ToggleSwitch>("WatchHistorySync").Header = "Compare YouTube History lookups (diagnostic)";
            C<TextBlock>("WatchHistoryHelp").Text = "Off each time the app opens. When enabled, compares two signed-in player lookups after playback starts. No history write is sent.";
        }
        C<ToggleSwitch>("WatchHistorySync").Toggled += (_, _) => ResetWatchStatus();
        var theater = C<MediaPlayerElement>("TheaterPlayer");
        playback = new PlaybackController<NativePlaybackSession>((id, ct, timing) => playbackResolver.ResolveAsync(id, ct, timing),
            async (media, ct, timing) => new NativePlaybackSession(await NativePlaybackSource.CreateAsync(playbackResolver, media, ct, timing, startupQuality), theater),
            PlaybackTiming.StartIfEnabled);
        playback.Changed += RenderPlayback;
        WirePlayerOverlay();
        C<Button>("PlayerBackButton").Click += (_, _) =>
        {
            var origin = libraryOrigin;
            playback.Back();
            RestoreLibraryFocus(origin);
        };
        C<Button>("PlayerRetryButton").Click += async (_, _) => { SyncPlaybackContext(); await playback.RetryAsync(); };
        C<Button>("PlayerOpenYouTubeButton").Click += async (_, _) =>
        {
            SyncPlaybackContext();
            if (playback.Selected is { } video && BrowserPolicy.VideoUri(video.VideoId) is { } uri) await LaunchExplicitAsync(uri);
        };
        C<ListView>("PlayerQueue").ContainerContentChanging += (_, args) => RenderHistoryRow(args);
        C<ListView>("PlayerQueue").ItemClick += async (_, args) =>
        {
            SyncPlaybackContext();
            if (args.ClickedItem is HistoryVideo video && playback.Selected is not null && playback.Queue.Any(v => ReferenceEquals(v, video))) await OpenVideoAsync(video);
        };

        C<Button>("SettingsButton").Click += async (_, _) => await NavigateAsync(settingsVisible ? lastBrowseView : BrowseView.Settings);
        C<Button>("PlayerSettingsPageButton").Click += async (_, _) => await NavigateAsync(BrowseView.Settings);
        C<Button>("HistoryButton").Click += async (_, _) => await NavigateAsync(BrowseView.History);
        C<Button>("SubscriptionsButton").Click += async (_, _) => await NavigateAsync(BrowseView.Subscriptions);
        C<Button>("SignInButton").Click += async (_, _) => { if (controller is { Busy: false }) await controller.SignInAsync(); };
        C<Button>("RestoreButton").Click += async (_, _) => { if (controller is { Busy: false }) await controller.InitializeAsync(); else if (controller is null) await InitializeServicesAsync(); };
        C<Button>("OpenActivationButton").Click += async (_, _) => await OpenActivationAsync();
        C<Button>("CancelButton").Click += (_, _) => { playback.Back(); controller?.Cancel(); };
        C<Button>("FeedCancelButton").Click += (_, _) => { playback.Back(); controller?.Cancel(); };
        C<Button>("RefreshButton").Click += async (_, _) => { if (controller is not null) await controller.LoadHistoryAsync(); };
        C<Button>("LoadMoreButton").Click += async (_, _) => { if (controller is not null) await controller.LoadHistoryAsync(append: true); };
        var accountMenu = (MenuFlyout)C<Button>("AccountButton").Flyout;
        accountMenu.Opening += (_, _) => PopulateAccountMenu(accountMenu);
        accountMenu.Closed += (_, _) => accountMenu.Items.Clear();
        C<ListView>("ChannelsList").ContainerContentChanging += (_, args) => RenderChannelRow(args);
        WireLibrary();
        C<GridView>("HistoryList").ItemClick += async (_, args) =>
        {
            if (closed || settingsVisible || playback.Selected is not null || controller is not { Busy: false, Phase: SessionPhase.SignedIn } || controller.View == BrowseView.Settings || args.ClickedItem is not HistoryVideo video ||
                !controller.Videos.Any(v => ReferenceEquals(v, video))) return;
            libraryOrigin = video;
            await OpenVideoAsync(video);
        };
        root.Loaded += async (_, _) =>
        {
            if (initialized || closed) return;
            initialized = true;
            _ = updatesPanel.CheckOnStartupAsync();
            C<Border>("ShellStatus").Visibility = Visibility.Visible;
            await InitializeServicesAsync();
        };
        Closed += (_, _) =>
        {
            closed = true;
            CancelWatchRequest();
            playerClock?.Stop();
            playerTapFade?.Stop();
            CancelScrub();
            ResetDescription(null);
            playback.Dispose();
            boundSession = null;
            playbackResolver.Dispose();
            controller?.Close();
            DisposeLibrary();
            updatesPanel.UpdateAvailabilityChanged -= RenderSettingsButton;
            updatesPanel.Dispose();
            http.Dispose();
        };
    }

    private T C<T>(string name) where T : FrameworkElement => (T)root.FindName(name);

    private void InitializeStartupQuality()
    {
        var path = AutoStartupPreference.SettingsPath;
        startupQuality = AutoStartupPreference.Load(path);
        var chooser = C<ComboBox>("StartupQualityChooser");
        var status = C<TextBlock>("StartupQualityStatus");
        foreach (int height in AutoStartupPreference.Choices)
        {
            var item = new ComboBoxItem { Tag = height, Content = height == 0 ? "Let Windows decide" : $"{height}p" };
            chooser.Items.Add(item);
            if (height == startupQuality) chooser.SelectedItem = item;
        }
        chooser.SelectionChanged += (_, _) =>
        {
            if (closed || chooser.SelectedItem is not ComboBoxItem { Tag: int height } || height == startupQuality) return;
            if (AutoStartupPreference.TrySave(path, height))
            {
                startupQuality = height;
                status.Visibility = Visibility.Collapsed;
                return;
            }
            chooser.SelectedItem = chooser.Items.OfType<ComboBoxItem>().Single(item => item.Tag is int saved && saved == startupQuality);
            status.Text = "Couldn't save the startup quality. Your previous setting is still in use.";
            status.Visibility = Visibility.Visible;
        };
    }

    private async Task InitializeServicesAsync()
    {
        if (closed) return;
        try
        {
            controller ??= new SessionController(new CoreApi(new YouTubeClient(http)), new VaultTokenStore(), DeviceIdentity.LoadOrCreate());
            controller.Changed -= OnStateChanged;
            controller.Changed += OnStateChanged;
            await controller.InitializeAsync();
        }
        catch
        {
            // Credential and filesystem failures must not expose paths or exception text.
            C<TextBlock>("StatusText").Text = "Windows could not initialize secure account storage. Retry saved login to try again.";
            C<ProgressRing>("BusyRing").IsActive = false;
            C<ProgressRing>("BusyRing").Visibility = Visibility.Collapsed;
            C<Border>("ShellStatus").Visibility = Visibility.Visible;
            C<ScrollViewer>("SignInPanel").Visibility = Visibility.Visible;
            C<Button>("SignInButton").IsEnabled = false;
        }
    }

    private void OnStateChanged()
    {
        if (closed) return;
        void ShowState() { browserNotice = null; Render(); }
        if (DispatcherQueue.HasThreadAccess) ShowState();
        else DispatcherQueue.TryEnqueue(ShowState);
    }

    private async Task NavigateAsync(BrowseView view)
    {
        if (closed) return;
        if (view != BrowseView.Settings && !settingsVisible && controller?.Phase != SessionPhase.SignedIn) return;
        browserNotice = null;
        playback.Back();
        settingsVisible = view == BrowseView.Settings;
        if (!settingsVisible) lastBrowseView = view;
        if (controller is not null) await controller.NavigateAsync(view);
        Render();
    }

    private void RenderSettingsButton()
    {
        bool showDot = !settingsVisible && updatesPanel.HasUpdate;
        string label = settingsVisible ? "Back" : "Settings";
        C<TextBlock>("SettingsButtonLabel").Text = label;
        C<Ellipse>("SettingsUpdateDot").Visibility = ToVisibility(showDot);
        C<Ellipse>("PlayerSettingsUpdateDot").Visibility = ToVisibility(showDot);
        string accessibleName = showDot ? "Settings, update available" : "Settings";
        AutomationProperties.SetName(C<Button>("SettingsButton"), settingsVisible ? label : accessibleName);
        AutomationProperties.SetName(C<Button>("PlayerSettingsPageButton"), accessibleName);
    }

    private void PopulateAccountMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        if (closed || controller is not { } current) return;
        if (current.Phase == SessionPhase.SignedIn)
        {
            foreach (var account in current.Accounts)
            {
                var selected = Equals(account, current.SelectedAccount);
                var item = new MenuFlyoutItem { Text = string.IsNullOrWhiteSpace(account.Handle) ? account.Name : $"{account.Name} · {account.Handle}", IsEnabled = !settingsVisible };
                if (selected) item.Icon = new FontIcon { Glyph = "\uE73E" };
                AutomationProperties.SetName(item, selected ? $"{item.Text}, selected" : item.Text);
                item.Click += async (_, _) =>
                {
                    if (closed || settingsVisible || !ReferenceEquals(controller, current) || current.Phase != SessionPhase.SignedIn ||
                        !current.Accounts.Any(candidate => ReferenceEquals(candidate, account)) || Equals(current.SelectedAccount, account)) return;
                    playback.Back();
                    await current.SelectAccountAsync(account);
                };
                menu.Items.Add(item);
            }
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        }
        var signOut = new MenuFlyoutItem { Text = current.Phase == SessionPhase.SignedIn ? "Sign out" : "Clear saved login" };
        signOut.Click += (_, _) =>
        {
            if (closed || !ReferenceEquals(controller, current) || current.Phase is not (SessionPhase.SignedIn or SessionPhase.SignedOut)) return;
            playback.Back();
            current.SignOut();
        };
        menu.Items.Add(signOut);
    }

    private void RenderStatus()
    {
        if (controller is not { } current) return;
        var browsing = current.Phase == SessionPhase.SignedIn && !settingsVisible && playback.Selected is null;
        var busy = current.Busy;
        var show = busy || !string.IsNullOrWhiteSpace(current.Status);
        C<Grid>("FeedStatus").Visibility = ToVisibility(browsing && show);
        C<TextBlock>("FeedStatusText").Text = browsing ? current.Status : "";
        C<ProgressRing>("FeedBusyRing").IsActive = browsing && busy;
        C<ProgressRing>("FeedBusyRing").Visibility = ToVisibility(browsing && busy);
        C<Button>("FeedCancelButton").Visibility = ToVisibility(browsing && busy);
        var shared = !fullscreen && !browsing && (browserNotice is not null || (show && current.Phase != SessionPhase.SignedIn));
        C<Border>("ShellStatus").Visibility = ToVisibility(shared);
        C<TextBlock>("StatusText").Text = shared ? browserNotice ?? current.Status : "";
        C<ProgressRing>("BusyRing").IsActive = shared && busy;
        C<ProgressRing>("BusyRing").Visibility = ToVisibility(shared && busy);
        C<Button>("CancelButton").Visibility = ToVisibility(shared && busy);
    }

    private void Render()
    {
        if (closed) return;
        var watchIdentity = controller is { Phase: SessionPhase.SignedIn } ? controller.SelectedAccount : null;
        if (!ReferenceEquals(watchStatusIdentity, watchIdentity))
        {
            watchStatusIdentity = watchIdentity;
            ResetWatchStatus();
        }
        SyncPlaybackContext();
        C<ScrollViewer>("SettingsPanel").Visibility = ToVisibility(settingsVisible);
        RenderSettingsButton();
        if (controller is null)
        {
            C<ScrollViewer>("SignInPanel").Visibility = ToVisibility(!settingsVisible && initialized);
            return;
        }
        {
            var signedIn = controller.Phase == SessionPhase.SignedIn;
            var subscriptions = controller.View == BrowseView.Subscriptions;
            SetLibraryDestination(C<Button>("HistoryButton"), "History", signedIn, !settingsVisible && !subscriptions);
            SetLibraryDestination(C<Button>("SubscriptionsButton"), "Subscriptions", signedIn, !settingsVisible && subscriptions);
            C<TextBlock>("ViewHeading").Text = subscriptions ? "Subscriptions" : "History";
            C<Border>("ChannelsDisclosure").Visibility = ToVisibility(subscriptions);
            if (!subscriptions || controller.Channels.Count == 0) C<Expander>("ChannelsPanel").IsExpanded = false;
            C<TextBlock>("ChannelsStatusText").Text = controller.Channels.Count > 0 ? $"Subscribed channels ({controller.Channels.Count})" : controller.Busy ? "Loading subscribed channels…" : "No subscribed channels loaded.";
            C<TextBlock>("VideosStatusText").Text = subscriptions ? "Latest videos from your channels" : "Videos you’ve watched";
            AutomationProperties.SetName(C<GridView>("HistoryList"), subscriptions ? "Latest subscription videos" : "Watch history");
            C<Grid>("HistoryPanel").Visibility = ToVisibility(!settingsVisible && signedIn && playback.Selected is null);
            C<ScrollViewer>("SignInPanel").Visibility = ToVisibility(!settingsVisible && controller.Phase == SessionPhase.SignedOut);
            C<StackPanel>("DevicePanel").Visibility = ToVisibility(!settingsVisible && controller.Phase == SessionPhase.Authorizing);

            C<Button>("SignInButton").IsEnabled = !controller.Busy;
            C<Button>("RestoreButton").IsEnabled = !controller.Busy;

            C<Button>("RefreshButton").IsEnabled = signedIn && !controller.Busy && controller.SelectedAccount is not null;
            C<Button>("LoadMoreButton").Visibility = ToVisibility(controller.Continuation is not null);
            C<Button>("LoadMoreButton").IsEnabled = signedIn && !controller.Busy;
            C<GridView>("HistoryList").IsItemClickEnabled = signedIn && !controller.Busy;
            var accountButton = C<Button>("AccountButton");
            var accountName = controller.SelectedAccount?.Name;
            var label = string.IsNullOrWhiteSpace(accountName) ? (signedIn && controller.Accounts.Count > 0 ? "Choose account" : "Account") : accountName;
            C<TextBlock>("AccountLabel").Text = label;
            C<TextBlock>("AccountInitial").Text = string.IsNullOrWhiteSpace(accountName) ? "?" : System.Globalization.StringInfo.GetNextTextElement(accountName.Trim()).ToUpperInvariant();
            AutomationProperties.SetName(accountButton, $"YouTube account: {label}");
            accountButton.Visibility = ToVisibility(signedIn || controller.Phase == SessionPhase.SignedOut);
            if (accountButton.Visibility == Visibility.Collapsed) accountButton.Flyout.Hide();
            var authorization = controller.Authorization;
            C<TextBlock>("DeviceCode").Text = authorization?.UserCode ?? "";
            C<TextBlock>("ExpiryText").Text = authorization is null ? "" : $"Code expires at {authorization.ExpiresAt.ToLocalTime():t}.";
            C<Button>("OpenActivationButton").IsEnabled = authorization is not null && authorization.ExpiresAt > DateTimeOffset.UtcNow && BrowserPolicy.IsActivationUri(authorization.VerificationUri);

            if (!ReferenceEquals(displayedChannels, controller.Channels))
            {
                displayedChannels = controller.Channels;
                C<ListView>("ChannelsList").ItemsSource = controller.Channels;
            }
            if (!ReferenceEquals(displayedVideos, controller.Videos))
            {
                libraryOrigin = null;
                CancelLibraryImages();
                displayedVideos = controller.Videos;
                C<GridView>("HistoryList").ItemsSource = controller.Videos;
            }
        }
        RenderPlayback();
    }

    private static Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void RenderChannelRow(ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not Grid host) return;
        host.Children.Clear();
        AutomationProperties.SetName(args.ItemContainer, "");
        if (closed || args.InRecycleQueue || args.Item is not SubscribedChannel channel) return;
        var row = new StackPanel { Spacing = 5 };
        row.Children.Add(new TextBlock { Text = channel.Name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (channel.HasNewContent) row.Children.Add(new TextBlock { Text = "New content", FontSize = 12 });
        host.Children.Add(row);
        AutomationProperties.SetName(args.ItemContainer, channel.HasNewContent ? $"{channel.Name}, new content" : channel.Name);
        args.Handled = true;
    }

    private void RenderHistoryRow(ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not Grid host) return;
        host.Children.Clear();
        AutomationProperties.SetName(args.ItemContainer, "");
        if (closed || args.InRecycleQueue || args.Item is not HistoryVideo video) return;
        var row = new StackPanel { Spacing = 5 };
        row.Children.Add(new TextBlock { Text = video.Title, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(new TextBlock { Text = $"{video.Channel}  ·  {video.Duration}", Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 172, 184, 174)), TextTrimming = TextTrimming.CharacterEllipsis });
        if (video.PercentWatched is int percent)
            row.Children.Add(new TextBlock { Text = $"{Math.Clamp(percent, 0, 100)}% watched", FontSize = 12 });
        host.Children.Add(row);
        AutomationProperties.SetName(args.ItemContainer, $"{video.Title}, {video.Channel}, {video.Duration}. Play natively.");
        args.Handled = true;
    }

    private void SyncPlaybackContext() => playback.SetContext(!closed && !settingsVisible && controller is { Busy: false, Phase: SessionPhase.SignedIn }, controller?.SelectedAccount, controller?.View ?? BrowseView.History, controller?.Videos ?? []);

    private async Task OpenVideoAsync(HistoryVideo video)
    {
        SyncPlaybackContext();
        if (closed || settingsVisible || controller is not { Busy: false, Phase: SessionPhase.SignedIn } || controller.View == BrowseView.Settings ||
            !controller.Videos.Any(v => ReferenceEquals(v, video))) return;
        await playback.SelectAsync(video);
    }

    private void RenderPlayback()
    {
        if (closed) return;
        var selected = playback.Selected;
        if (!ReferenceEquals(selected, watchSelection))
        {
            CancelWatchRequest();
            watchSelection = selected;
            watchAttempted = false;
            lastWatchPosition = null;
        }
        C<Grid>("BrowseAndSettings").Visibility = ToVisibility(!fullscreen && (selected is null || settingsVisible));
        C<ScrollViewer>("TheaterPanel").Visibility = ToVisibility(selected is not null && !settingsVisible);
        C<Grid>("HistoryPanel").Visibility = ToVisibility(selected is null && !settingsVisible && controller?.Phase == SessionPhase.SignedIn);
        var source = playback.Active;
        if (!ReferenceEquals(source, boundSession))
        {
            boundSession = source;
            lastWatchPosition = null;
            playerTapFade?.Stop();
            PlayerMenu.Hide();
            CancelScrub();
            if (source is not null && !source.IsOpened)
            {
                try
                {
                    var active = source;
                    active.Failed += () => playback.Fail(active);
                    active.Ready += () => { if (ReferenceEquals(playback.Active, active)) RenderPlayback(); };
                    active.Prepare();
                    ApplyPlayerAudio(active);
                    active.Open();
                    if (!active.HasFailed) active.Play();
                }
                catch { playback.Fail(source); source = playback.Active; }
            }
        }
        C<TextBlock>("PlayerTitle").Text = source?.Media.Title ?? selected?.Title ?? "";
        C<TextBlock>("PlayerAuthor").Text = source is null ? selected?.Channel ?? "" : $"{source.Media.Author} · {source.Media.Duration:g}";
        if (!ReferenceEquals(source?.Catalog, descriptionCatalog)) ResetDescription(source?.Catalog);
        if (source is { IsOpened: true } && descriptionRequest is null)
            _ = LoadDescriptionAsync(source.Catalog, (descriptionRequest = new()).Token);
        C<TextBlock>("PlayerError").Text = playback.Error ?? "";
        C<TextBlock>("PlayerError").Visibility = ToVisibility(playback.Error is not null);
        if (fullscreen && (selected is null || playback.Error is not null || settingsVisible)) SetFullscreen(false, restoreFocus: false);
        RenderStatus();
        if (playback.QualityBusy)
        {
            playerTapFade?.Stop();
            CancelScrub();
        }
        C<TextBlock>("PlayerQualityStatus").Text = playback.QualityBusy ? "Changing video quality…" : playback.QualityError ?? "";
        C<TextBlock>("PlayerQualityStatus").Visibility = ToVisibility(playback.QualityBusy || playback.QualityError is not null);
        C<Button>("PlayerRetryButton").Visibility = ToVisibility(playback.Error is not null);
        C<Button>("PlayerRetryButton").IsEnabled = selected is not null && !playback.Busy;
        C<Button>("PlayerOpenYouTubeButton").IsEnabled = selected is not null;
        C<ProgressRing>("PlayerBusyRing").IsActive = playback.Busy;
        C<ProgressRing>("PlayerBusyRing").Visibility = ToVisibility(playback.Busy);
        var label = controller?.View == BrowseView.Subscriptions ? "subscriptions" : "history";
        C<TextBlock>("PlayerFeedLabel").Text = $"{label} / Now playing";
        C<TextBlock>("PlayerQueueLabel").Text = playback.Queue.Count == 0 ? $"No other videos loaded in {label}" : $"Up next · from your loaded {label}";
        C<ListView>("PlayerQueue").ItemsSource = playback.Queue;
        UpdatePlayerOverlay();
    }

    private void ResetDescription(PlaybackMedia? catalog)
    {
        var previous = descriptionRequest;
        descriptionCatalog = catalog;
        descriptionRequest = null;
        previous?.Cancel();
        previous?.Dispose();
        if (!closed) C<TextBlock>("PlayerDescription").Text = catalog?.Description ?? "";
    }

    private async Task LoadDescriptionAsync(PlaybackMedia catalog, CancellationToken token)
    {
        var description = await playbackResolver.GetDeferredDescriptionAsync(catalog, token);
        if (!closed && !token.IsCancellationRequested && description is not null && catalog.ExpiresAt > DateTimeOffset.UtcNow &&
            ReferenceEquals(playback.Active?.Catalog, catalog)) C<TextBlock>("PlayerDescription").Text = description;
    }

    private static string QualityLabel(int? height) => height is int value ? $"{value}p" : "Auto";
    private static string RateLabel(double rate) => rate == 1 ? "Normal" : $"{rate:0.##}×";

    private NativePlaybackSession? InteractivePlayer => !closed && !settingsVisible && !playback.QualityBusy &&
        playback.Active is { IsOpened: true, HasFailed: false } active ? active : null;

    private void WirePlayerOverlay()
    {
        var stage = C<Grid>("PlayerStage");
        var seek = C<Slider>("PlayerSeek");
        PlayerMenu.Opening += (_, _) => PopulatePlayerMenu();
        PlayerMenu.Opened += (_, _) => playerMenuOpen = true;
        PlayerMenu.Closed += (_, _) => playerMenuOpen = false;
        C<Grid>("PlayerStageSlot").SizeChanged += (_, args) =>
        {
            if (!fullscreen && args.NewSize.Width > 0) ResizePlayerStage(args.NewSize.Width);
        };
        stage.SizeChanged += (_, args) =>
            C<Slider>("PlayerVolume").Visibility = ToVisibility(args.NewSize.Width >= 450);
        C<Button>("PlayerPlay").Click += (_, _) => TogglePlayerPlayback();
        C<Border>("PlayerVideoHitArea").Tapped += (_, args) =>
        {
            TogglePlayerPlayback(showFeedback: true);
            args.Handled = true;
        };
        C<Button>("PlayerMute").Click += (_, _) =>
        {
            if (InteractivePlayer is { } active) NativePlaybackSource.Attempt(() =>
            {
                playerAudio.ToggleMute();
                ApplyPlayerAudio(active);
            });
            UpdatePlayerOverlay();
        };
        C<Slider>("PlayerVolume").ValueChanged += (_, args) =>
        {
            if (!updatingTransport && InteractivePlayer is { } active) NativePlaybackSource.Attempt(() =>
            {
                playerAudio.SetVolume(args.NewValue / 100);
                ApplyPlayerAudio(active);
            });
        };
        var hitArea = C<Border>("PlayerSeekHitArea");
        hitArea.PointerPressed += (_, args) =>
        {
            if (seekPointer is null && InteractivePlayer is { } active && seek.IsEnabled &&
                args.GetCurrentPoint(hitArea).Properties.IsLeftButtonPressed && hitArea.CapturePointer(args.Pointer))
            {
                seekPointer = args.Pointer.PointerId;
                seekOwner = active;
                seek.Focus(FocusState.Pointer);
                PreviewScrub(args);
                args.Handled = true;
            }
        };
        hitArea.PointerMoved += (_, args) =>
        {
            if (seekPointer != args.Pointer.PointerId) return;
            PreviewScrub(args);
            args.Handled = true;
        };
        hitArea.PointerReleased += (_, args) =>
        {
            if (seekPointer != args.Pointer.PointerId) return;
            PreviewScrub(args);
            FinishScrub(args.Pointer.PointerId, commit: true);
            args.Handled = true;
        };
        hitArea.PointerCaptureLost += (_, args) => FinishScrub(args.Pointer.PointerId, commit: false);
        hitArea.PointerCanceled += (_, args) => FinishScrub(args.Pointer.PointerId, commit: false);
        seek.ValueChanged += (_, args) =>
        {
            if (updatingTransport || InteractivePlayer is not { } active) return;
            if (seekPointer is null) Seek(active, args.NewValue);
        };
        C<Button>("PlayerFullscreen").Click += (_, _) => ToggleFullscreen();
        AddFullscreenKey(Windows.System.VirtualKey.F11, toggle: true);
        AddFullscreenKey(Windows.System.VirtualKey.Escape, toggle: false);

        playerClock = root.DispatcherQueue.CreateTimer();
        playerClock.Interval = TimeSpan.FromMilliseconds(150);
        playerClock.Tick += (_, _) => UpdatePlayerOverlay();
    }

    private void TogglePlayerPlayback(bool showFeedback = false)
    {
        if (InteractivePlayer is { } active)
        {
            bool pause = false;
            bool accepted = NativePlaybackSource.Attempt(() =>
            {
                var player = active.Player;
                var view = player.PlaybackSession;
                pause = IsPlaying(view.PlaybackState);
                if (pause) player.Pause();
                else
                {
                    if (view.CanSeek && view.NaturalDuration > TimeSpan.Zero && view.Position >= view.NaturalDuration)
                        view.Position = TimeSpan.Zero;
                    active.Play();
                }
            });
            if (accepted && showFeedback && ReferenceEquals(active, InteractivePlayer)) ShowPlayerTapFeedback(pause);
        }
        UpdatePlayerOverlay();
    }

    private void ShowPlayerTapFeedback(bool pause)
    {
        playerTapFade?.Stop();
        C<FontIcon>("PlayerTapIcon").Glyph = pause ? PauseGlyph : PlayGlyph;
        if (playerTapFade is null)
        {
            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100)), Value = 1 });
            fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), Value = 1 });
            fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), Value = 0 });
            Storyboard.SetTarget(fade, C<Border>("PlayerTapFeedback"));
            Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
            playerTapFade = new Storyboard { FillBehavior = FillBehavior.Stop };
            playerTapFade.Children.Add(fade);
        }
        playerTapFade.Begin();
    }

    private void ApplyPlayerAudio(NativePlaybackSession active)
    {
        // Muting goes first so restoring a saved gain never creates a loud transient.
        if (playerAudio.IsMuted) active.Player.IsMuted = true;
        active.Player.Volume = playerAudio.Volume;
        if (!playerAudio.IsMuted) active.Player.IsMuted = false;
    }

    private void AddFullscreenKey(Windows.System.VirtualKey key, bool toggle)
    {
        var shortcut = new KeyboardAccelerator { Key = key };
        shortcut.Invoked += (_, args) =>
        {
            // Escape belongs to the open menu first.
            if (playerMenuOpen || (!fullscreen && (!toggle || InteractivePlayer is null))) return;
            ToggleFullscreen();
            args.Handled = true;
        };
        root.KeyboardAccelerators.Add(shortcut);
    }

    private void ToggleFullscreen()
    {
        if (fullscreen || InteractivePlayer is not null) SetFullscreen(!fullscreen, restoreFocus: true);
    }

    private void SetFullscreen(bool enabled, bool restoreFocus)
    {
        if (enabled == fullscreen) return;
        var slot = C<Grid>("PlayerStageSlot");
        var stage = C<Grid>("PlayerStage");
        var theater = C<ScrollViewer>("TheaterPanel");
        PlayerMenu.Hide();
        CancelScrub();
        if (enabled)
        {
            windowedPresenter = AppWindow.Presenter;
            if (!NativePlaybackSource.Attempt(() => AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)))
            {
                windowedPresenter = null;
                return;
            }
        }
        else if (!NativePlaybackSource.Attempt(() =>
        {
            if (windowedPresenter is { } prior) AppWindow.SetPresenter(prior);
            else AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        }) && !NativePlaybackSource.Attempt(() => AppWindow.SetPresenter(AppWindowPresenterKind.Default))) return;
        fullscreen = enabled;
        if (!enabled) windowedPresenter = null;

        // Resize Theater in place: moving the stage disconnects the live player and its focused controls.
        var chromeVisibility = ToVisibility(!enabled);
        C<Border>("TitleBar").Visibility = chromeVisibility;
        C<Grid>("ShellHeader").Visibility = chromeVisibility;
        C<Grid>("BrowseAndSettings").Visibility = ToVisibility(!enabled && (playback.Selected is null || settingsVisible));
        if (enabled) C<Button>("AccountButton").Flyout.Hide();
        RenderStatus();
        C<Grid>("TheaterHeader").Visibility = chromeVisibility;
        C<StackPanel>("TheaterDetails").Visibility = chromeVisibility;
        Grid.SetRow(theater, enabled ? 0 : 2);
        Grid.SetRowSpan(theater, enabled ? root.RowDefinitions.Count : 1);
        theater.VerticalScrollBarVisibility = enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        theater.TabFocusNavigation = enabled ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Local;
        if (enabled) stage.Height = double.NaN;
        else ResizePlayerStage(slot.ActualWidth);
        var button = C<Button>("PlayerFullscreen");
        SetIconAction(button, enabled ? "\uE73F" : "\uE740", enabled ? "Exit fullscreen" : "Fullscreen");
        if (restoreFocus) button.Focus(FocusState.Programmatic);
    }

    private void ResizePlayerStage(double width) => C<Grid>("PlayerStage").Height = Math.Clamp(width * 9 / 16, 240, 650);

    private void CancelScrub()
    {
        if (seekPointer is null) return;
        seekPointer = null;
        seekOwner = null;
        C<Border>("PlayerSeekHitArea").ReleasePointerCaptures();
    }

    private void PreviewScrub(PointerRoutedEventArgs args)
    {
        var hitArea = C<Border>("PlayerSeekHitArea");
        var seek = C<Slider>("PlayerSeek");
        double width = hitArea.ActualWidth;
        if (width <= 0) return;
        updatingTransport = true;
        try
        {
            seek.Value = seek.Maximum * Math.Clamp(args.GetCurrentPoint(hitArea).Position.X / width, 0, 1);
            C<TextBlock>("PlayerTime").Text = $"{FormatTime(seek.Value)} / {FormatTime(seek.Maximum)}";
        }
        finally { updatingTransport = false; }
    }

    private void FinishScrub(uint id, bool commit)
    {
        if (seekPointer != id) return;
        var active = seekOwner;
        double position = commit ? C<Slider>("PlayerSeek").Value : 0;
        CancelScrub();
        if (commit && active is not null && ReferenceEquals(active, InteractivePlayer))
            Seek(active, position);
        UpdatePlayerOverlay();
    }

    private void Seek(NativePlaybackSession active, double seconds) => NativePlaybackSource.Attempt(() =>
    {
        lastWatchPosition = null;
        var view = active.Player.PlaybackSession;
        if (view.CanSeek && view.NaturalDuration > TimeSpan.Zero)
            view.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, view.NaturalDuration.TotalSeconds));
    });

    private static bool IsPlaying(MediaPlaybackState state) => state is MediaPlaybackState.Playing or MediaPlaybackState.Buffering;

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Truncate(seconds)));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";
    }

    private static void SetIconAction(Button button, string glyph, string label)
    {
        var icon = (FontIcon)button.Content;
        if (icon.Glyph == glyph && AutomationProperties.GetName(button) == label) return;
        icon.Glyph = glyph;
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
    }

    private void UpdatePlayerOverlay()
    {
        if (closed) return;
        var session = playback.Active;
        bool ready = !settingsVisible && session is { IsOpened: true, HasFailed: false };
        var overlay = C<StackPanel>("PlayerOverlay");
        overlay.Visibility = ToVisibility(ready);
        if (!ready) { lastWatchPosition = null; playerClock?.Stop(); return; }
        if (playerClock is { IsRunning: false }) playerClock.Start();
        bool enabled = InteractivePlayer is not null;
        C<Button>("PlayerPlay").IsEnabled = enabled;
        C<Button>("PlayerMute").IsEnabled = enabled;
        C<Button>("PlayerSettingsGear").IsEnabled = enabled;
        C<Slider>("PlayerVolume").IsEnabled = enabled;
        C<Button>("PlayerFullscreen").IsEnabled = enabled || fullscreen;
        updatingTransport = true;
        try
        {
            NativePlaybackSource.Attempt(() =>
            {
                var player = session!.Player;
                var view = player.PlaybackSession;
                ObserveWatchProgress(view);
                var seek = C<Slider>("PlayerSeek");
                double duration = Math.Max(0, view.NaturalDuration.TotalSeconds);
                seek.IsEnabled = enabled && view.CanSeek && duration > 0;
                if (seekPointer is null)
                {
                    double position = Math.Clamp(view.Position.TotalSeconds, 0, duration);
                    seek.Maximum = Math.Max(duration, 1);
                    // WinUI's Slider snaps user input to StepFrequency.
                    seek.Value = position;
                    var time = C<TextBlock>("PlayerTime");
                    string label = $"{FormatTime(position)} / {FormatTime(duration)}";
                    if (time.Text != label) time.Text = label;
                }
                var playing = IsPlaying(view.PlaybackState);
                SetIconAction(C<Button>("PlayerPlay"), playing ? PauseGlyph : PlayGlyph, playing ? "Pause" : "Play");
                var muted = player.IsMuted || player.Volume == 0;
                SetIconAction(C<Button>("PlayerMute"), muted ? "\uE74F" : "\uE767", muted ? "Unmute" : "Mute");
                C<Slider>("PlayerVolume").Value = player.Volume * 100;
            });
        }
        finally { updatingTransport = false; }
    }

    private void ObserveWatchProgress(MediaPlaybackSession view)
    {
        if (watchAttempted || !C<ToggleSwitch>("WatchHistorySync").IsOn || playback.Busy || playback.QualityBusy || seekPointer is not null ||
            view.PlaybackState != MediaPlaybackState.Playing || watchSelection is not { } video ||
            controller is not { Busy: false, SelectedAccount: { } account } current)
        {
            lastWatchPosition = null;
            return;
        }
        var prior = lastWatchPosition;
        var position = view.Position;
        var duration = view.NaturalDuration;
        lastWatchPosition = position;
        if (prior is null || prior.Value < TimeSpan.Zero || position <= prior || position > duration) return;
        watchAttempted = true;
        var request = watchRequest = new CancellationTokenSource();
        _ = SendWatchStartAsync(current, account, video, duration, prior.Value, ++watchStatusGeneration, request);
    }

    private void CancelWatchRequest()
    {
        var request = watchRequest;
        watchRequest = null;
        request?.Cancel();
    }

    private void ResetWatchStatus()
    {
        CancelWatchRequest();
        watchStatusGeneration++;
        C<TextBlock>("WatchHistoryStatus").Text = "";
    }

    private async Task SendWatchStartAsync(SessionController current, YouTubeAccount account, HistoryVideo video, TimeSpan duration, TimeSpan position, long generation, CancellationTokenSource request)
    {
        string? result;
        try
        {
            result = compareWatchLookups
                ? await current.TryCompareWatchLookupsAsync(account, video.VideoId, request.Token)
                : await current.TryRecordWatchStartAsync(account, video.VideoId, duration, position, request.Token);
        }
        finally
        {
            if (ReferenceEquals(watchRequest, request)) watchRequest = null;
            request.Dispose();
        }
        if (closed || generation != watchStatusGeneration || !ReferenceEquals(account, current.SelectedAccount)) return;
        if (compareWatchLookups)
        {
            C<TextBlock>("WatchHistoryStatus").Text = (result switch
            {
                "watch_account_failed" or "auth_required" => "Account preparation failed.",
                "watch_discovery_failed" => "YouTube TV client setup failed.",
                "watch_lookup_failed" => "The player lookup comparison did not complete.",
                "watch_timed_out" => "The player lookup comparison timed out.",
                null => "The player lookup comparison was interrupted.",
                "watch_failed" => "The player lookup comparison failed.",
                _ => result
            }) + " No history write was sent.";
            return;
        }
        C<TextBlock>("WatchHistoryStatus").Text = result switch
        {
            "watch_sent" => "Playback start sent. Check YouTube History to confirm it was saved.",
            "watch_account_failed" or "auth_required" => "Account preparation failed. No history write was sent.",
            "watch_discovery_failed" => "YouTube TV client setup failed. No history write was sent.",
            "watch_context_unavailable" => "YouTube player context could not be found. No history write was sent.",
            "watch_lookup_failed" => "YouTube player lookup failed. No history write was sent.",
            "watch_lookup_not_playable" => "The YouTube player lookup did not return a playable video. No history write was sent.",
            "watch_tracking_unusable" => "The YouTube player response did not contain usable tracking data. No history write was sent.",
            "watch_record_rejected" => "YouTube returned an unsuccessful response to the history write.",
            "watch_record_unconfirmed" => "The history write could not be confirmed. Check YouTube History.",
            "watch_timed_out" => "Reporting the playback start timed out. Check YouTube History.",
            null => "Reporting was interrupted. Check YouTube History.",
            _ => "Reporting the playback start failed. Check YouTube History."
        };
    }

    private MenuFlyout PlayerMenu => (MenuFlyout)C<Button>("PlayerSettingsGear").Flyout;

    private void PopulatePlayerMenu()
    {
        var flyout = PlayerMenu;
        flyout.Items.Clear();
        if (InteractivePlayer is not { } active) return;
        var quality = new MenuFlyoutSubItem { Text = "Quality · " + QualityLabel(playback.QualityHeight), IsEnabled = active.Catalog.Qualities.Count > 0 };
        var speed = new MenuFlyoutSubItem { Text = "Playback speed · " + RateLabel(active.Player.PlaybackSession.PlaybackRate) };
        flyout.Items.Add(quality);
        flyout.Items.Add(speed);
        AddQuality(null);
        foreach (var height in active.Catalog.Qualities.Select(q => q.Height).Distinct().OrderDescending()) AddQuality(height);
        foreach (var rate in new[] { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2 })
        {
            var item = new ToggleMenuFlyoutItem { Text = RateLabel(rate), IsChecked = Math.Abs(active.Player.PlaybackSession.PlaybackRate - rate) < 0.01 };
            item.Click += (_, _) =>
            {
                if (ReferenceEquals(InteractivePlayer, active))
                    NativePlaybackSource.Attempt(() => active.Player.PlaybackSession.PlaybackRate = rate);
            };
            speed.Items.Add(item);
        }

        void AddQuality(int? height)
        {
            var label = QualityLabel(height);
            var item = new ToggleMenuFlyoutItem { Text = height is 720 or 1080 ? label + "  HD" : label, IsChecked = playback.QualityHeight == height };
            item.Click += async (_, _) =>
            {
                SyncPlaybackContext();
                await playback.ChangeQualityAsync(active, height, PrepareQualityAsync,
                    (old, candidate, token, accept) => candidate.ConfirmHandoffAsync(old, token, accept));
            };
            quality.Items.Add(item);
        }
    }

    private async Task<NativePlaybackSession> PrepareQualityAsync(NativePlaybackSession active, int? height,
        CancellationToken lifetime, CancellationToken operation)
    {
        var media = playbackResolver.ForQuality(active.Catalog, height);
        var timing = PlaybackTiming.StartIfEnabled();
        timing?.Mark(height is null ? PlaybackTimingStage.ReturnToAutoStarted : PlaybackTimingStage.ManualQualityStarted, height: height);
        var candidate = new NativePlaybackSession(await NativePlaybackSource.CreateAsync(playbackResolver, media, lifetime, operation, auto: height is null, timing: timing, startupHeight: startupQuality),
            C<MediaPlayerElement>("TheaterPlayer"), active.Catalog);
        try
        {
            await candidate.PrepareHandoffAsync(operation);
            candidate.Failed += () => playback.Fail(candidate);
            return candidate;
        }
        catch { candidate.Dispose(); throw; }
    }

    private async Task OpenActivationAsync()
    {
        var authorization = controller?.Authorization;
        if (closed || controller?.Phase != SessionPhase.Authorizing || authorization is null ||
            authorization.ExpiresAt <= DateTimeOffset.UtcNow || !BrowserPolicy.IsActivationUri(authorization.VerificationUri)) return;
        await LaunchExplicitAsync(authorization.VerificationUri);
    }

    private async Task LaunchExplicitAsync(Uri uri)
    {
        var video = playback.Selected;
        var authorization = controller?.Authorization;
        try
        {
            if (await Launcher.LaunchUriAsync(uri)) return;
        }
        catch { }
        if (closed || settingsVisible || !ReferenceEquals(video, playback.Selected) || !ReferenceEquals(authorization, controller?.Authorization)) return;
        browserNotice = "Windows could not open your default browser.";
        RenderStatus();
    }
}
