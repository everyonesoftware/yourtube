using System.Reflection;
using EveryoneSoftware.WindowsCommon;
using Microsoft.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
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
    private readonly HorizontalShelf playerQueueArrows;
    private readonly TextBlock playerQueueLabel;
    private readonly NativeTitleBar titleBar;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private readonly WindowsUpdatesPanel updatesPanel;
    private readonly PlaybackResolver playbackResolver = new();
    private readonly PlaybackController<NativePlaybackSession> playback;
    private readonly bool compareWatchLookups = Environment.GetEnvironmentVariable("YOURTUBE_WATCH_LOOKUP_COMPARE") == "1";
    private int startupQuality;
    private int reachabilityChecks;
    private PlaybackStartupSettings startupSettings;
    private NativePlaybackSession? boundSession;
    private Action? unbindWatchSeek;
    private HistoryVideo? watchSelection;
    private readonly WatchProgress watchProgress = new();
    private YouTubeClient.WatchRecord? watchRecord;
    private Task? watchProgressWrite;
    private bool drainingWindowWatch;
    private CancellationToken windowWatchDeadline;
    private bool allowWindowClose;
    private bool watchAttempted;
    private YouTubeAccount? watchStatusIdentity;
    private long watchStatusGeneration;
    private CancellationTokenSource? watchRequest;
    private DispatcherQueueTimer? playerClock;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? playerTapFade;
    private bool playerRunning;
    private Windows.Foundation.Point? playerMousePosition;
    private long? lastPlayerActivity;
    private readonly ScalarTransition playerFadeIn = new() { Duration = TimeSpan.FromMilliseconds(110) };
    private readonly ScalarTransition playerFadeOut = new() { Duration = TimeSpan.FromMilliseconds(220) };
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
    private YouTubeAccount? avatarAccount;
    private CancellationTokenSource? avatarRequest;
    private IReadOnlyList<HistoryVideo>? displayedVideos;
    private IReadOnlyList<SubscribedChannel>? displayedChannels;
    private readonly Dictionary<SelectorItem, CancellationTokenSource> channelImages = new();
    private sealed class ChannelChoice(SubscribedChannel? channel)
    {
        public SubscribedChannel? Channel { get; } = channel;
    }
    private static readonly ChannelChoice AllSubscriptions = new(null);
    private bool syncingChannelSelection;
    private BrowseView lastBrowseView = BrowseView.Home;
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
        var captionBackground = ColorHelper.FromArgb(255, 28, 33, 31);
        titleBar = new NativeTitleBar(this, C<Border>("TitleBar"), new Thickness(16, 0, 12, 0),
            new TitleBarColors(captionBackground, ColorHelper.FromArgb(255, 237, 240, 233),
                captionBackground, ColorHelper.FromArgb(255, 172, 184, 174)));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 800));

        // Integration point: updater owns its native view and transport, never auth.
        updatesPanel = new WindowsUpdatesPanel();
        updatesPanel.UpdateAvailabilityChanged += RenderSettingsButton;
        C<ContentControl>("SettingsHost").Content = updatesPanel.View;
        InitializeStartupQuality();
        InitializeReachabilityChecks();
        if (compareWatchLookups)
        {
            C<ToggleSwitch>("WatchHistorySync").IsOn = false;
            C<ToggleSwitch>("WatchHistorySync").Header = "Compare YouTube History lookups (diagnostic)";
            C<TextBlock>("WatchHistoryHelp").Text = "Off each time the app opens. When enabled, compares two signed-in player lookups after playback starts. No history write is sent.";
        }
        C<ToggleSwitch>("WatchHistorySync").Toggled += (_, _) => ResetWatchStatus();
        var theater = C<MediaPlayerElement>("TheaterPlayer");
        playback = new PlaybackController<NativePlaybackSession>((id, ct, timing) => playbackResolver.ResolveAsync(id, ct, timing),
            async (media, ct, timing) => new NativePlaybackSession(await NativePlaybackSource.CreateAsync(playbackResolver, media, ct, timing, startupSettings.AutoHeight), theater),
            StartStartupTiming);
        playback.Changed += RenderPlayback;
        WirePlayerOverlay();
        WirePlaybackProgress();
        C<Button>("PlayerCloseButton").Click += (_, _) => CloseVideo();
        C<Button>("PlayerRetryButton").Click += async (_, _) => { SyncPlaybackContext(); await playback.RetryAsync(); };
        C<Button>("PlayerOpenYouTubeButton").Click += async (_, _) =>
        {
            SyncPlaybackContext();
            if (playback.Selected is { } video && BrowserPolicy.VideoUri(video.VideoId) is { } uri) await LaunchExplicitAsync(uri);
        };
        playerQueueLabel = new TextBlock
        {
            FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        };
        playerQueueArrows = new HorizontalShelf(playerQueueLabel);
        C<ContentControl>("PlayerQueueHeader").Content = playerQueueArrows.Header;
        playerQueueArrows.Bind(C<ListView>("PlayerQueue"), () => C<ScrollViewer>("TheaterPanel"));
        C<ListView>("PlayerQueue").ContainerContentChanging += (_, args) => RenderHistoryRow(args);
        C<ListView>("PlayerQueue").ItemClick += async (_, args) =>
        {
            SyncPlaybackContext();
            if (args.ClickedItem is HistoryVideo video && playback.Selected is not null && playback.Queue.Any(v => ReferenceEquals(v, video))) await OpenVideoAsync(video);
        };

        C<Button>("SettingsButton").Click += async (_, _) => await NavigateAsync(settingsVisible ? lastBrowseView : BrowseView.Settings);
        C<Button>("HomeButton").Click += async (_, _) => await NavigateAsync(BrowseView.Home);
        C<Button>("HistoryButton").Click += async (_, _) => await NavigateAsync(BrowseView.History);
        C<Button>("SubscriptionsButton").Click += async (_, _) => await NavigateAsync(BrowseView.Subscriptions);
        C<Button>("SignInButton").Click += async (_, _) => { if (controller is { Busy: false }) await controller.SignInAsync(); };
        C<Button>("RestoreButton").Click += async (_, _) => { if (controller is { Busy: false }) await controller.InitializeAsync(); else if (controller is null) await InitializeServicesAsync(); };
        C<Button>("OpenActivationButton").Click += async (_, _) => await OpenActivationAsync();
        C<Button>("CancelButton").Click += (_, _) => { playback.Back(); controller?.Cancel(); };
        C<Button>("FeedCancelButton").Click += (_, _) =>
        {
            if (controller is { Busy: true, Appending: false } current) { playback.Back(); current.Cancel(); }
        };
        C<Button>("RefreshButton").Click += async (_, _) =>
        {
            if (controller is { View: BrowseView.Home } && search.Active) await SubmitSearchAsync(search.Query);
            else if (controller is not null) await controller.LoadHistoryAsync();
        };
        C<Button>("FeedRetryButton").Click += async (_, _) =>
        {
            if (!closed && !settingsVisible && playback.Selected is null && controller is { View: BrowseView.History or BrowseView.Subscriptions, AppendFailed: true } current)
                await current.LoadHistoryAsync(append: true);
        };
        C<Button>("HomeRetryButton").Click += async (_, _) =>
        {
            if (!closed && !settingsVisible && playback.Selected is null && controller is { View: BrowseView.Home, AppendFailed: true } current)
                await current.LoadHistoryAsync(append: true);
        };
        var accountMenu = (MenuFlyout)C<Button>("AccountButton").Flyout;
        accountMenu.Opening += (_, _) => PopulateAccountMenu(accountMenu);
        accountMenu.Closed += (_, _) => accountMenu.Items.Clear();
        C<ListView>("ChannelsList").ContainerContentChanging += (_, args) => RenderChannelRow(args);
        C<ListView>("ChannelsList").SelectionChanged += async (sender, _) =>
        {
            if (syncingChannelSelection || closed || settingsVisible || playback.Selected is not null ||
                controller is not { View: BrowseView.Subscriptions } current ||
                sender is not ListView { SelectedItem: ChannelChoice choice }) return;
            await current.SelectSubscriptionChannelAsync(choice.Channel);
        };
        C<Grid>("LibraryFeeds").SizeChanged += (_, args) =>
            C<Border>("SubscriptionRail").Width = Math.Clamp(args.NewSize.Width * .27, 134, 214);
        WireLibrary();
        WireSearch();
        C<GridView>("HistoryList").ItemClick += async (_, args) =>
        {
            if (closed || settingsVisible || search.Active || playback.Selected is not null || controller is not { CanPlayFeed: true, View: BrowseView.History or BrowseView.Subscriptions } || args.ClickedItem is not HistoryVideo video ||
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
        AppWindow.Closing += OnWindowClosing;
        Closed += (_, _) =>
        {
            closed = true;
            titleBar.Dispose();
            search.Dispose();

            RenderAccountAvatar(null);
            CancelChannelImages();
            CancelWatchRequest();
            BindWatchSeekSource(null);
            playerClock?.Stop();
            progressClock?.Stop();
            PlayerProgressFlyout.Hide();
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

    private void InitializeReachabilityChecks()
    {
        var path = ReachabilityPreference.SettingsPath;
        reachabilityChecks = ReachabilityPreference.Load(path);
        var chooser = C<ComboBox>("ReachabilityChecksChooser");
        var status = C<TextBlock>("ReachabilityChecksStatus");
        foreach (int count in ReachabilityPreference.Choices)
        {
            var item = new ComboBoxItem { Tag = count, Content = count == 1 ? "1 (sequential, default)" : $"{count}" };
            chooser.Items.Add(item);
            if (count == reachabilityChecks) chooser.SelectedItem = item;
        }
        chooser.SelectionChanged += (_, _) =>
        {
            if (closed || chooser.SelectedItem is not ComboBoxItem { Tag: int count } || count == reachabilityChecks) return;
            if (ReachabilityPreference.TrySave(path, count))
            {
                reachabilityChecks = count;
                status.Visibility = Visibility.Collapsed;
                return;
            }
            chooser.SelectedItem = chooser.Items.OfType<ComboBoxItem>().Single(item => item.Tag is int saved && saved == reachabilityChecks);
            status.Text = "Couldn't save the reachability check limit. Your previous setting is still in use.";
            status.Visibility = Visibility.Visible;
        };
    }

    private PlaybackTiming StartStartupTiming()
    {
        startupSettings = new(startupQuality, reachabilityChecks);
        playbackResolver.MaxConcurrentStreamChecks = startupSettings.ReachabilityLimit;
        return PlaybackTiming.StartForStartup();
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
        string accessibleName = showDot ? "Settings, update available" : "Settings";
        AutomationProperties.SetName(C<Button>("SettingsButton"), settingsVisible ? label : accessibleName);
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
        C<Button>("FeedCancelButton").Visibility = ToVisibility(browsing && busy && !current.Appending);
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
        BindSearch();
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
            var home = controller.View == BrowseView.Home;
            var subscriptions = controller.View == BrowseView.Subscriptions;
            SetLibraryDestination(C<Button>("HomeButton"), "Home", signedIn, !settingsVisible && home);
            SetLibraryDestination(C<Button>("HistoryButton"), "History", signedIn, !settingsVisible && controller.View == BrowseView.History);
            SetLibraryDestination(C<Button>("SubscriptionsButton"), "Subscriptions", signedIn, !settingsVisible && subscriptions);
            C<TextBlock>("ViewHeading").Text = home ? "Home" : subscriptions ? "Subscriptions" : "History";
            C<Border>("SubscriptionRail").Visibility = ToVisibility(subscriptions && controller.SelectedAccount is not null);
            var videosStatus = C<TextBlock>("VideosStatusText");
            videosStatus.Visibility = ToVisibility(!home && !subscriptions);
            AutomationProperties.SetName(C<GridView>("HistoryList"), subscriptions ? $"Videos: {controller.SelectedSubscriptionChannel?.Name ?? "All Subscriptions"}" : "Watch history");
            C<ListView>("HomeShelvesList").Visibility = ToVisibility(home && !search.Active);
            C<GridView>("HistoryList").Visibility = ToVisibility(!home && !search.Active);
            RenderSearch();
            C<Grid>("HistoryPanel").Visibility = ToVisibility(!settingsVisible && signedIn && playback.Selected is null);
            C<ScrollViewer>("SignInPanel").Visibility = ToVisibility(!settingsVisible && controller.Phase == SessionPhase.SignedOut);
            C<StackPanel>("DevicePanel").Visibility = ToVisibility(!settingsVisible && controller.Phase == SessionPhase.Authorizing);

            C<Button>("SignInButton").IsEnabled = !controller.Busy;
            C<Button>("RestoreButton").IsEnabled = !controller.Busy;

            C<Button>("RefreshButton").IsEnabled = signedIn && !controller.Busy && controller.SelectedAccount is not null;
            C<Button>("FeedRetryButton").Visibility = ToVisibility(!home && controller.AppendFailed);
            C<Button>("FeedRetryButton").IsEnabled = signedIn && !home && !controller.Busy;
            C<Button>("HomeRetryButton").Visibility = ToVisibility(home && controller.AppendFailed);
            C<Button>("HomeRetryButton").IsEnabled = signedIn && home && !controller.Busy;
            C<GridView>("HistoryList").IsItemClickEnabled = !home && controller.CanPlayFeed;
            var accountButton = C<Button>("AccountButton");
            var accountName = controller.SelectedAccount?.Name;
            var label = string.IsNullOrWhiteSpace(accountName) ? (signedIn && controller.Accounts.Count > 0 ? "Choose account" : "Account") : accountName;
            C<TextBlock>("AccountLabel").Text = label;
            C<TextBlock>("AccountInitial").Text = string.IsNullOrWhiteSpace(accountName) ? "?" : System.Globalization.StringInfo.GetNextTextElement(accountName.Trim()).ToUpperInvariant();
            RenderAccountAvatar(signedIn ? controller.SelectedAccount : null);
            AutomationProperties.SetName(accountButton, $"YouTube account: {label}");
            accountButton.Visibility = ToVisibility(signedIn || controller.Phase == SessionPhase.SignedOut);
            if (accountButton.Visibility == Visibility.Collapsed) accountButton.Flyout.Hide();
            var authorization = controller.Authorization;
            C<TextBlock>("DeviceCode").Text = authorization?.UserCode ?? "";
            C<TextBlock>("ExpiryText").Text = authorization is null ? "" : $"Code expires at {authorization.ExpiresAt.ToLocalTime():t}.";
            C<Button>("OpenActivationButton").IsEnabled = authorization is not null && authorization.ExpiresAt > DateTimeOffset.UtcNow && BrowserPolicy.IsActivationUri(authorization.VerificationUri);

            RenderSubscriptionChoices(controller.Channels, controller.SelectedSubscriptionChannel);
            RenderHomeShelves(signedIn && home ? controller.HomeShelves : []);
            RenderLibraryVideos(signedIn && !home ? controller.Videos : null);
        }
        RenderPlayback();
    }

    private void RenderAccountAvatar(YouTubeAccount? account)
    {
        if (ReferenceEquals(account, avatarAccount)) return;
        avatarAccount = account;
        avatarRequest?.Cancel();
        avatarRequest = null;
        var avatar = C<Ellipse>("AccountAvatar");
        avatar.Visibility = Visibility.Collapsed;
        avatar.Fill = null;
        C<TextBlock>("AccountInitial").Visibility = Visibility.Visible;
        if (account?.AvatarUri is not { } uri) return;

        var request = new CancellationTokenSource();
        avatarRequest = request;
        _ = LoadAccountAvatarAsync(uri, request);
    }

    private async Task LoadAccountAvatarAsync(Uri uri, CancellationTokenSource request)
    {
        try
        {
            var bytes = await libraryThumbnails.LoadAvatarAsync(uri, request.Token);
            if (closed || !ReferenceEquals(avatarRequest, request) || bytes is null) return;
            var bitmap = await DecodeImageAsync(bytes, 104);
            if (closed || !ReferenceEquals(avatarRequest, request)) return;
            var avatar = C<Ellipse>("AccountAvatar");
            avatar.Fill = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
            avatar.Visibility = Visibility.Visible;
            C<TextBlock>("AccountInitial").Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (System.Runtime.InteropServices.COMException) { } // Leave the initial if the image cannot be decoded.
        finally
        {
            if (ReferenceEquals(avatarRequest, request)) avatarRequest = null;
            request.Dispose();
        }
    }

    private static Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void RenderSubscriptionChoices(IReadOnlyList<SubscribedChannel> channels, SubscribedChannel? selected)
    {
        var list = C<ListView>("ChannelsList");
        syncingChannelSelection = true;
        try
        {
            if (!ReferenceEquals(displayedChannels, channels))
            {
                CancelChannelImages();
                displayedChannels = channels;
                list.ItemsSource = new[] { AllSubscriptions }.Concat(channels.Select(c => new ChannelChoice(c))).ToArray();
            }
            if (list.SelectedItem is not ChannelChoice choice || !ReferenceEquals(choice.Channel, selected))
            {
                var index = 0;
                while (selected is not null && index < channels.Count && !ReferenceEquals(channels[index], selected)) index++;
                list.SelectedIndex = selected is null || index == channels.Count ? 0 : index + 1;
            }
        }
        finally { syncingChannelSelection = false; }
    }

    private void RenderChannelRow(ContainerContentChangingEventArgs args)
    {
        var container = args.ItemContainer;
        if (channelImages.Remove(container, out var previous)) previous.Cancel();
        AutomationProperties.SetName(container, "");
        if (container.ContentTemplateRoot is not Grid host) return;
        host.Children.Clear();
        if (closed || args.InRecycleQueue || args.Item is not ChannelChoice choice) return;
        var channel = choice.Channel;
        var name = channel?.Name ?? "All Subscriptions";
        var hasNewVideos = channel?.HasNewContent == true;
        var row = new Grid { ColumnSpacing = 9 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock
        {
            Text = name, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = hasNewVideos ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
        };
        Ellipse? photo = null;
        if (channel is not null)
        {
            var art = new Grid { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(art, AccessibilityView.Raw);
            art.Children.Add(new Ellipse { Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 74, 93, 78)) });
            art.Children.Add(new FontIcon { Glyph = "\uE77B", FontSize = 14 });
            photo = new Ellipse();
            art.Children.Add(photo);
            row.Children.Add(art);
            Grid.SetColumn(label, 1);
        }
        row.Children.Add(label);
        host.Children.Add(new Border
        {
            Child = row, Padding = new Thickness(9, 5, 9, 5), MinHeight = 38, CornerRadius = new CornerRadius(6),
            Background = hasNewVideos ? new SolidColorBrush(ColorHelper.FromArgb(255, 58, 80, 63)) : null
        });
        AutomationProperties.SetName(container, hasNewVideos ? $"{name}, new videos" : name);
        args.Handled = true;

        if (photo is not null && channel?.AvatarUri is { } uri)
        {
            var request = new CancellationTokenSource();
            channelImages.Add(container, request);
            _ = LoadChannelImageAsync(container, photo, uri, request);
        }
    }

    private bool OwnsChannelImage(SelectorItem container, CancellationTokenSource request) =>
        !closed && channelImages.TryGetValue(container, out var current) && ReferenceEquals(request, current);

    private async Task LoadChannelImageAsync(SelectorItem container, Ellipse photo, Uri uri, CancellationTokenSource request)
    {
        try
        {
            var bytes = await libraryThumbnails.LoadAvatarAsync(uri, request.Token);
            if (!OwnsChannelImage(container, request) || bytes is null) return;
            var bitmap = await DecodeImageAsync(bytes, 64);
            if (!OwnsChannelImage(container, request)) return;
            photo.Fill = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
        }
        catch (OperationCanceledException) { }
        catch (System.Runtime.InteropServices.COMException) { } // Keep the placeholder if decoding fails.
        finally
        {
            if (OwnsChannelImage(container, request)) channelImages.Remove(container);
            request.Dispose();
        }
    }

    private void CancelChannelImages()
    {
        var requests = channelImages.Values.ToArray();
        channelImages.Clear();
        foreach (var request in requests) request.Cancel();
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
        if (controller?.View == BrowseView.History && video.PercentWatched is int percent)
            row.Children.Add(new TextBlock { Text = $"{Math.Clamp(percent, 0, 100)}% watched", FontSize = 12 });
        host.Children.Add(row);
        AutomationProperties.SetName(args.ItemContainer, $"{video.Title}, {video.Channel}, {video.Duration}. Play natively.");
        args.Handled = true;
    }

    private IReadOnlyList<HistoryVideo> PlaybackFeed => search.Active ? search.Videos : controller?.Videos ?? [];

    private void SyncPlaybackContext() => playback.SetContext(!closed && !settingsVisible && controller is { CanPlayFeed: true }, controller?.SelectedAccount, controller?.View ?? BrowseView.History, PlaybackFeed);

    private async Task OpenVideoAsync(HistoryVideo video)
    {
        SyncPlaybackContext();
        if (closed || settingsVisible || controller is not { CanPlayFeed: true } ||
            !PlaybackFeed.Any(v => ReferenceEquals(v, video))) return;
        await playback.SelectAsync(video);
    }

    private void CloseVideo()
    {
        if (closed || playback.Selected is null) return;
        var origin = libraryOrigin;
        playback.Back();
        RestoreLibraryFocus(origin);
    }

    private void RenderPlayback()
    {
        if (closed) return;
        var selected = playback.Selected;
        var theater = C<ScrollViewer>("TheaterPanel");
        if (!ReferenceEquals(selected, watchSelection))
        {
            ShowPlayerBrowserNotice(null);
            PlayerProgressFlyout.Hide();
            if (theater.VerticalOffset > 0) theater.ChangeView(null, 0, null, disableAnimation: true);
            CancelWatchRequest();
            watchSelection = selected;
            watchAttempted = false;
        }
        bool showTheater = selected is not null && !settingsVisible;
        if (!showTheater) PlayerProgressFlyout.Hide();
        if (showTheater && theater.Visibility != Visibility.Visible) C<Button>("AccountButton").Flyout.Hide();
        C<Grid>("BrowseAndSettings").Visibility = ToVisibility(!fullscreen && !showTheater);
        theater.Visibility = ToVisibility(showTheater);
        C<Grid>("ShellFooter").Visibility = ToVisibility(!showTheater);
        C<Grid>("HistoryPanel").Visibility = ToVisibility(selected is null && !settingsVisible && controller?.Phase == SessionPhase.SignedIn);
        var source = playback.Active;
        if (!ReferenceEquals(source, boundSession))
        {
            boundSession = source;
            playback.StartupTiming?.ObserveStartupClock(null);
            BindWatchSeekSource(source);
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
                    WireStartupEnd(active);
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
        var label = controller?.View switch
        {
            BrowseView.Home => "Home feed",
            BrowseView.Subscriptions => "subscriptions",
            _ => "history"
        };
        playerQueueLabel.Text = playback.Queue.Count == 0 ? $"No other videos loaded in {label}" : $"Up next · from your loaded {label}";
        playerQueueArrows.SetTitle(playback.Queue.Count == 0 ? null : "Up next");
        C<ListView>("PlayerQueue").ItemsSource = playback.Queue;
        UpdatePlayerOverlay();
        if (playerProgressOpen) RenderPlaybackProgress();
    }

    private void ResetDescription(PlaybackMedia? catalog)
    {
        var previous = descriptionRequest;
        descriptionCatalog = catalog;
        descriptionRequest = null;
        previous?.Cancel();
        previous?.Dispose();
        if (!closed) C<TextBlock>("PlayerDescription").Text = catalog?.Description ?? "";
        SetSeekChapters(catalog?.Description, catalog?.Duration ?? TimeSpan.Zero);
        ResetSeekStoryboard(catalog);
    }

    private async Task LoadDescriptionAsync(PlaybackMedia catalog, CancellationToken token)
    {
        var description = await playbackResolver.GetDeferredDescriptionAsync(catalog, token);
        if (!closed && !token.IsCancellationRequested && description is not null && catalog.ExpiresAt > DateTimeOffset.UtcNow &&
            ReferenceEquals(playback.Active?.Catalog, catalog))
        {
            C<TextBlock>("PlayerDescription").Text = description;
            SetSeekChapters(description, catalog.Duration);
        }
    }

    private static string QualityLabel(int? height) => height is int value ? $"{value}p" : "Auto";
    private static string RateLabel(double rate) => rate == 1 ? "Normal" : $"{rate:0.##}×";

    private NativePlaybackSession? InteractivePlayer => !closed && !settingsVisible && !playback.QualityBusy &&
        playback.Active is { IsOpened: true, HasFailed: false } active ? active : null;

    private void WirePlayerOverlay()
    {
        var stage = C<Grid>("PlayerStage");
        var seek = C<Slider>("PlayerSeek");
        stage.PointerEntered += (_, args) => TrackPlayerMouse(stage, args);
        stage.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, args) => TrackPlayerMouse(stage, args)), handledEventsToo: true);
        stage.PointerExited += (_, args) =>
        {
            if (args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
            playerMousePosition = null;
            lastPlayerActivity = null;
            UpdatePlayerControlsVisibility();
        };
        C<Grid>("PlayerOverlay").GotFocus += (_, _) => UpdatePlayerControlsVisibility();
        PlayerMenu.Opening += (_, _) => PopulatePlayerMenu();
        PlayerMenu.Opened += (_, _) => { playerMenuOpen = true; UpdatePlayerControlsVisibility(); };
        PlayerMenu.Closed += (_, _) => { playerMenuOpen = false; UpdatePlayerControlsVisibility(); };
        var theater = C<ScrollViewer>("TheaterPanel");
        theater.SizeChanged += (_, args) => ResizePlayerStage(args.NewSize.Width, args.NewSize.Height);
        stage.SizeChanged += (_, args) =>
        {
            C<Slider>("PlayerVolume").Visibility = ToVisibility(args.NewSize.Width >= 450);
            HideSeekPreview();
        };
        C<Button>("PlayerPlay").Click += (_, _) => TogglePlayerPlayback();
        C<Border>("PlayerVideoHitArea").Tapped += (_, args) =>
        {
            if (args.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse && !C<Grid>("PlayerOverlay").IsHitTestVisible)
            {
                lastPlayerActivity = Environment.TickCount64;
                UpdatePlayerControlsVisibility();
            }
            else TogglePlayerPlayback(showFeedback: true);
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
            if (seekPointer == args.Pointer.PointerId)
            {
                PreviewScrub(args);
                args.Handled = true;
            }
            else PreviewSeekHover(args);
        };
        hitArea.SizeChanged += (_, _) => { UpdateChapterTrack(); HideSeekPreview(); };
        C<Border>("PlayerSeekPreview").SizeChanged += (_, _) => PositionSeekPreview();
        hitArea.PointerEntered += (_, args) => PreviewSeekHover(args);
        hitArea.PointerExited += (_, _) => { if (seekPointer is null) HideSeekPreview(); };
        hitArea.PointerReleased += (_, args) =>
        {
            if (seekPointer != args.Pointer.PointerId) return;
            PreviewScrub(args);
            FinishScrub(args.Pointer.PointerId, commit: true);
            args.Handled = true;
        };
        hitArea.PointerCaptureLost += (_, args) => FinishScrub(args.Pointer.PointerId, commit: false);
        hitArea.PointerCanceled += (_, args) => FinishScrub(args.Pointer.PointerId, commit: false);
        seek.RegisterPropertyChangedCallback(UIElement.FocusStateProperty, (_, _) => UpdateChapterFocus());
        seek.ValueChanged += (_, args) =>
        {
            UpdateChapterProgress(args.NewValue);
            if (updatingTransport || InteractivePlayer is not { } active) return;
            if (seekPointer is null) Seek(active, args.NewValue);
        };
        C<Button>("PlayerFullscreen").Click += (_, _) => ToggleFullscreen();
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        AddFullscreenKey(Windows.System.VirtualKey.F11, toggle: true);
        AddFullscreenKey(Windows.System.VirtualKey.Escape, toggle: false);

        playerClock = root.DispatcherQueue.CreateTimer();
        playerClock.Interval = TimeSpan.FromMilliseconds(150);
        playerClock.Tick += (_, _) => UpdatePlayerOverlay();
    }

    private void TrackPlayerMouse(Grid stage, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        var point = args.GetCurrentPoint(stage).Position;
        // Captured moves can still reach the stage after the mouse leaves its bounds.
        Windows.Foundation.Point? position = point.X >= 0 && point.X < stage.ActualWidth &&
            point.Y >= 0 && point.Y < stage.ActualHeight ? point : null;
        if (Nullable.Equals(position, playerMousePosition)) return;
        playerMousePosition = position;
        lastPlayerActivity = position is not null ? Environment.TickCount64 : null;
        UpdatePlayerControlsVisibility();
    }

    private void UpdatePlayerControlsVisibility()
    {
        if (closed) return;
        var overlay = C<Grid>("PlayerOverlay");
        if (overlay.Visibility != Visibility.Visible) return;
        bool interacting = playerMenuOpen || playerProgressOpen || seekPointer is not null;
        if (!interacting && root.XamlRoot is { } xamlRoot &&
            FocusManager.GetFocusedElement(xamlRoot) is Control { FocusState: FocusState.Keyboard } focused)
        {
            for (DependencyObject? element = focused; element is not null; element = VisualTreeHelper.GetParent(element))
            {
                if (element != overlay) continue;
                interacting = true;
                break;
            }
        }
        bool show = PlayerControlsVisibility.ShouldShow(playerRunning, lastPlayerActivity, Environment.TickCount64, interacting);
        if (overlay.IsHitTestVisible == show) return;
        overlay.OpacityTransition = show ? playerFadeIn : playerFadeOut;
        overlay.Opacity = show ? 1 : 0;
        // A faded control must not intercept a click meant for the video surface.
        overlay.IsHitTestVisible = show;
        if (!show) HideSeekPreview();
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
                        SetUserPlaybackPosition(active, view, TimeSpan.Zero);
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
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, C<Border>("PlayerTapFeedback"));
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
            playerTapFade = new() { FillBehavior = FillBehavior.Stop };
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
            if (playerMenuOpen || playerProgressOpen || (!fullscreen && (!toggle || InteractivePlayer is null))) return;
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
        var stage = C<Grid>("PlayerStage");
        var theater = C<ScrollViewer>("TheaterPanel");
        PlayerMenu.Hide();
        PlayerProgressFlyout.Hide();
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
        if (!enabled) titleBar.RefreshInsets();
        C<Grid>("BrowseAndSettings").Visibility = ToVisibility(!enabled && (playback.Selected is null || settingsVisible));
        RenderStatus();
        C<StackPanel>("TheaterDetails").Visibility = chromeVisibility;
        Grid.SetRow(theater, enabled ? 0 : 1);
        Grid.SetRowSpan(theater, enabled ? root.RowDefinitions.Count : 1);
        theater.VerticalScrollBarVisibility = enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        theater.TabFocusNavigation = enabled ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Local;
        if (enabled) stage.Height = double.NaN;
        else ResizePlayerStage(theater.ActualWidth, theater.ActualHeight);
        var button = C<Button>("PlayerFullscreen");
        SetIconAction(button, enabled ? "\uE73F" : "\uE740", enabled ? "Exit fullscreen" : "Fullscreen");
        if (restoreFocus) button.Focus(FocusState.Programmatic);
        UpdatePlayerControlsVisibility();
    }

    private void ResizePlayerStage(double viewportWidth, double viewportHeight)
    {
        if (!fullscreen && viewportWidth > 0 && viewportHeight > 0)
            C<Grid>("PlayerStage").Height = PlayerStageLayout.FitHeight(viewportWidth, viewportHeight);
    }

    private void CancelScrub()
    {
        HideSeekPreview();
        if (seekPointer is null) return;
        seekPointer = null;
        seekOwner = null;
        C<Border>("PlayerSeekHitArea").ReleasePointerCaptures();
    }

    private void PreviewScrub(PointerRoutedEventArgs args)
    {
        var hitArea = C<Border>("PlayerSeekHitArea");
        var seek = C<Slider>("PlayerSeek");
        if (!seek.IsEnabled || seekOwner is null || !ReferenceEquals(seekOwner, InteractivePlayer))
        {
            CancelScrub();
            return;
        }
        double width = hitArea.ActualWidth;
        if (width <= 0) return;
        updatingTransport = true;
        try
        {
            double fraction = Math.Clamp(args.GetCurrentPoint(hitArea).Position.X / width, 0, 1);
            seek.Value = seek.Maximum * fraction;
            C<TextBlock>("PlayerTime").Text = $"{FormatTime(seek.Value)} / {FormatTime(seek.Maximum)}";
            ShowSeekPreview(seek.Value, fraction);
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
        var view = active.Player.PlaybackSession;
        if (view.CanSeek && view.NaturalDuration > TimeSpan.Zero)
            SetUserPlaybackPosition(active, view, TimeSpan.FromSeconds(Math.Clamp(seconds, 0, view.NaturalDuration.TotalSeconds)));
    });

    private void BindWatchSeekSource(NativePlaybackSession? source)
    {
        unbindWatchSeek?.Invoke();
        unbindWatchSeek = null;
        watchProgress.SourceChanged(source);
    }

    private void SetUserPlaybackPosition(NativePlaybackSession active, MediaPlaybackSession view, TimeSpan target)
    {
        if (!watchProgress.IsSeeking && view.Position == target) return;
        if (unbindWatchSeek is null)
        {
            var dispatcher = DispatcherQueue;
            void SeekCompleted(MediaPlaybackSession sender, object args) => dispatcher.TryEnqueue(() =>
            {
                if (!closed && ReferenceEquals(playback.Active, active)) watchProgress.CompleteSeek(active);
            });
            view.SeekCompleted += SeekCompleted;
            unbindWatchSeek = () => NativePlaybackSource.Attempt(() => view.SeekCompleted -= SeekCompleted);
        }
        // Never infer completion from clock advancement. If events are lost or coalesced,
        // only reporting is suppressed; subsequent native transport commands still go through.
        playback.StartupTiming?.ObserveStartupClock(null);
        watchProgress.BeginSeek();
        view.Position = target;
    }

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
        bool theaterActive = !settingsVisible && playback.Selected is not null;
        bool ready = theaterActive && session is { IsOpened: true, HasFailed: false };
        var overlay = C<Grid>("PlayerOverlay");
        overlay.Visibility = ToVisibility(theaterActive);
        C<StackPanel>("PlayerTransport").Visibility = ToVisibility(ready);
        if (!ready)
        {
            CancelScrub();
            UpdateChapterTrack();
            watchProgress.BreakSegment();
            playerRunning = false;
            if (playback.Selected is null)
            {
                lastPlayerActivity = null;
                playerMousePosition = null;
            }
            if (!theaterActive)
            {
                overlay.OpacityTransition = null;
                overlay.Opacity = 0;
                overlay.IsHitTestVisible = false;
            }
            playerClock?.Stop();
            UpdatePlayerControlsVisibility();
            return;
        }
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
                playback.StartupTiming?.ObserveStartupClock(!playback.QualityBusy &&
                    view.PlaybackState == MediaPlaybackState.Playing ? view.Position : null, view.PlaybackRate);
                ObserveWatchProgress(view);
                var seek = C<Slider>("PlayerSeek");
                double duration = Math.Max(0, view.NaturalDuration.TotalSeconds);
                bool canSeek = enabled && view.CanSeek && duration > 0;
                bool seekabilityChanged = seek.IsEnabled != canSeek;
                seek.IsEnabled = canSeek;
                if (!canSeek) CancelScrub();
                if (seekPointer is null)
                {
                    double position = Math.Clamp(view.Position.TotalSeconds, 0, duration);
                    bool durationChanged = seek.Maximum != Math.Max(duration, 1);
                    seek.Maximum = Math.Max(duration, 1);
                    if (seekabilityChanged || durationChanged) UpdateChapterTrack();
                    // WinUI's Slider snaps user input to StepFrequency.
                    seek.Value = position;
                    var time = C<TextBlock>("PlayerTime");
                    string label = $"{FormatTime(position)} / {FormatTime(duration)}";
                    if (time.Text != label) time.Text = label;
                }
                var playing = IsPlaying(view.PlaybackState);
                if (playing && !playerRunning && playerMousePosition is not null)
                    lastPlayerActivity = Environment.TickCount64;
                playerRunning = playing;
                SetIconAction(C<Button>("PlayerPlay"), playing ? PauseGlyph : PlayGlyph, playing ? "Pause" : "Play");
                var muted = player.IsMuted || player.Volume == 0;
                SetIconAction(C<Button>("PlayerMute"), muted ? "\uE74F" : "\uE767", muted ? "Unmute" : "Mute");
                C<Slider>("PlayerVolume").Value = player.Volume * 100;
            });
        }
        finally { updatingTransport = false; }
        UpdatePlayerControlsVisibility();
    }

    private void ObserveWatchProgress(MediaPlaybackSession view)
    {
        if (drainingWindowWatch || !C<ToggleSwitch>("WatchHistorySync").IsOn || playback.Busy || playback.QualityBusy || seekPointer is not null ||
            watchSelection is not { } video ||
            controller is not { Busy: false, SelectedAccount: { } account } current)
        {
            watchProgress.BreakSegment();
            return;
        }
        if (view.PlaybackState != MediaPlaybackState.Playing)
        {
            watchProgress.BreakSegment();
            if (view.PlaybackState == MediaPlaybackState.Paused)
                SendPendingWatchProgress(final: true);
            return;
        }
        var duration = view.NaturalDuration;
        if (watchProgress.Observe(view.Position, duration, Environment.TickCount64, view.PlaybackRate) is not { } prior) return;
        if (watchAttempted)
        {
            SendPendingWatchProgress(final: false);
            return;
        }
        watchAttempted = true;
        var request = watchRequest = new CancellationTokenSource();
        _ = SendWatchStartAsync(current, account, video, duration, prior, ++watchStatusGeneration, request.Token);
    }

    private void CancelWatchRequest()
    {
        var request = watchRequest;
        watchRequest = null;
        watchRecord = null;
        watchProgressWrite = null;
        watchProgress.Reset();
        request?.Cancel();
        request?.Dispose();
    }

    private void ResetWatchStatus()
    {
        CancelWatchRequest();
        watchStatusGeneration++;
        C<TextBlock>("WatchHistoryStatus").Text = "";
    }

    private void SendPendingWatchProgress(bool final)
    {
        if (compareWatchLookups || !C<ToggleSwitch>("WatchHistorySync").IsOn ||
            watchRecord is not { } record || watchRequest is not { } request || controller is not { Busy: false, SelectedAccount: { } account } current ||
            windowWatchDeadline.IsCancellationRequested || !watchProgress.CanTake(final)) return;
        if (watchProgressWrite is { IsCompleted: false })
        {
            if (final) watchProgress.FlushAfterCurrentWrite();
            return;
        }
        var generation = watchStatusGeneration;
        var token = request.Token;
        watchProgressWrite = watchProgress.DrainAsync(final,
            range => SendWatchProgressAsync(current, account, record, range, generation, token), token);
    }

    private async Task<bool> SendWatchProgressAsync(SessionController current, YouTubeAccount account, YouTubeClient.WatchRecord record,
        (TimeSpan Start, TimeSpan End) range, long generation, CancellationToken token)
    {
        if (windowWatchDeadline.IsCancellationRequested) return false;
        using var deadline = drainingWindowWatch ? CancellationTokenSource.CreateLinkedTokenSource(token, windowWatchDeadline) : null;
        var result = await current.TryRecordWatchProgressAsync(account, record, range.Start, range.End, deadline?.Token ?? token);
        if (closed || token.IsCancellationRequested || windowWatchDeadline.IsCancellationRequested || generation != watchStatusGeneration ||
            !ReferenceEquals(account, current.SelectedAccount) || result is null) return false;
        C<TextBlock>("WatchHistoryStatus").Text = result == "watch_progress_sent"
            ? "Playback progress sent. Check YouTube History to confirm it was saved."
            : "Playback progress could not be confirmed. Playback is unaffected.";
        return true;
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowWindowClose) return;
        if (drainingWindowWatch) { args.Cancel = true; return; }
        if (watchRecord is null || (watchProgressWrite is not { IsCompleted: false } && !watchProgress.CanTake(final: true))) return;
        // Cancel synchronously and finish outside the native Closing callback.
        var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        if (!DispatcherQueue.TryEnqueue(async () => await FinishWindowCloseAsync(budget)))
        {
            budget.Dispose();
            return;
        }
        windowWatchDeadline = budget.Token;
        args.Cancel = drainingWindowWatch = true;
    }

    private async Task FinishWindowCloseAsync(CancellationTokenSource closeBudget)
    {
        using var budget = closeBudget;
        try
        {
            if (watchProgressWrite is { IsCompleted: false } pending) await pending.WaitAsync(budget.Token);
            if (!closed)
            {
                SendPendingWatchProgress(final: true);
                if (watchProgressWrite is { IsCompleted: false } remaining) await remaining.WaitAsync(budget.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            allowWindowClose = true;
            if (!closed) Close();
        }
    }

    private async Task SendWatchStartAsync(SessionController current, YouTubeAccount account, HistoryVideo video, TimeSpan duration, TimeSpan position, long generation, CancellationToken token)
    {
        YouTubeClient.WatchRecord? record = null;
        var result = compareWatchLookups
            ? await current.TryCompareWatchLookupsAsync(account, video.VideoId, token)
            : await current.TryRecordWatchStartAsync(account, video.VideoId, duration, position, token, started => record = started);
        if (closed || token.IsCancellationRequested || generation != watchStatusGeneration || !ReferenceEquals(account, current.SelectedAccount)) return;
        watchRecord = record;
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
            WireStartupEnd(candidate);
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
        if (video is not null) ShowPlayerBrowserNotice(null);
        try
        {
            if (await Launcher.LaunchUriAsync(uri)) return;
        }
        catch { }
        if (closed || settingsVisible || !ReferenceEquals(video, playback.Selected) || !ReferenceEquals(authorization, controller?.Authorization)) return;
        const string message = "Windows could not open your default browser.";
        if (video is not null) ShowPlayerBrowserNotice(message);
        else
        {
            browserNotice = message;
            RenderStatus();
        }
    }

    private void ShowPlayerBrowserNotice(string? message)
    {
        var notice = C<TextBlock>("PlayerBrowserNotice");
        notice.Visibility = ToVisibility(message is not null);
        notice.Text = message ?? "";
    }
}
