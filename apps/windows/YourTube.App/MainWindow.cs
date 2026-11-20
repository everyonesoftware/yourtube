using System.Reflection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using YourTube.Core;

namespace YourTube.App;

internal sealed class MainWindow : Window
{
    private readonly Grid root;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private readonly WindowsUpdatesPanel updatesPanel;
    private SessionController? controller;
    private IReadOnlyList<HistoryVideo>? displayedVideos;
    private IReadOnlyList<YouTubeAccount>? displayedAccounts;
    private IReadOnlyList<SubscribedChannel>? displayedChannels;
    private BrowseView lastBrowseView = BrowseView.History;
    private bool rendering;
    private bool settingsVisible;
    private bool initialized;
    private bool closed;

    public MainWindow()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("YourTube.App.Shell.xaml") ?? throw new InvalidOperationException("Missing native shell resource.");
        using var reader = new StreamReader(stream);
        root = (Grid)XamlReader.Load(reader.ReadToEnd());
        Title = "YourTube";
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
        C<ContentControl>("SettingsHost").Content = updatesPanel.View;
        C<Button>("SettingsButton").Click += async (_, _) => await NavigateAsync(settingsVisible ? lastBrowseView : BrowseView.Settings);
        C<Button>("HistoryButton").Click += async (_, _) => await NavigateAsync(BrowseView.History);
        C<Button>("SubscriptionsButton").Click += async (_, _) => await NavigateAsync(BrowseView.Subscriptions);
        C<Button>("SignInButton").Click += async (_, _) => { if (controller is { Busy: false }) await controller.SignInAsync(); };
        C<Button>("RestoreButton").Click += async (_, _) => { if (controller is { Busy: false }) await controller.InitializeAsync(); else if (controller is null) await InitializeServicesAsync(); };
        C<Button>("OpenActivationButton").Click += async (_, _) => await OpenActivationAsync();
        C<Button>("CancelButton").Click += (_, _) => controller?.Cancel();
        C<Button>("SignOutButton").Click += (_, _) => controller?.SignOut();
        C<Button>("RefreshButton").Click += async (_, _) => { if (controller is not null) await controller.LoadHistoryAsync(); };
        C<Button>("LoadMoreButton").Click += async (_, _) => { if (controller is not null) await controller.LoadHistoryAsync(append: true); };
        C<ComboBox>("AccountChooser").SelectionChanged += async (_, _) =>
        {
            if (!rendering && !closed && controller is not null && C<ComboBox>("AccountChooser").SelectedItem is ComboBoxItem { Tag: YouTubeAccount account })
                await controller.SelectAccountAsync(account);
        };
        C<ListView>("ChannelsList").ContainerContentChanging += (_, args) => RenderChannelRow(args);
        C<ListView>("HistoryList").ContainerContentChanging += (_, args) => RenderHistoryRow(args);
        C<ListView>("HistoryList").ItemClick += async (_, args) =>
        {
            if (closed || settingsVisible || controller is not { Busy: false, Phase: SessionPhase.SignedIn } || controller.View == BrowseView.Settings || args.ClickedItem is not HistoryVideo video ||
                !controller.Videos.Any(v => ReferenceEquals(v, video))) return;
            var uri = BrowserPolicy.VideoUri(video.VideoId);
            if (uri is not null) await LaunchExplicitAsync(uri);
        };
        root.Loaded += async (_, _) => { if (!initialized) { initialized = true; await InitializeServicesAsync(); } };
        Closed += (_, _) =>
        {
            closed = true;
            controller?.Close();
            updatesPanel.Dispose();
            http.Dispose();
        };
    }

    private T C<T>(string name) where T : FrameworkElement => (T)root.FindName(name);

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
            C<ScrollViewer>("SignInPanel").Visibility = Visibility.Visible;
            C<Button>("SignInButton").IsEnabled = false;
        }
    }

    private void OnStateChanged()
    {
        if (closed) return;
        if (DispatcherQueue.HasThreadAccess) Render();
        else DispatcherQueue.TryEnqueue(Render);
    }

    private async Task NavigateAsync(BrowseView view)
    {
        if (closed) return;
        if (view != BrowseView.Settings && !settingsVisible && controller?.Phase != SessionPhase.SignedIn) return;
        settingsVisible = view == BrowseView.Settings;
        if (!settingsVisible) lastBrowseView = view;
        if (controller is not null) await controller.NavigateAsync(view);
        Render();
    }

    private void Render()
    {
        if (closed) return;
        C<ScrollViewer>("SettingsPanel").Visibility = ToVisibility(settingsVisible);
        C<Button>("SettingsButton").Content = settingsVisible ? "Back" : "Settings";
        if (controller is null)
        {
            C<ScrollViewer>("SignInPanel").Visibility = ToVisibility(!settingsVisible && initialized);
            return;
        }
        rendering = true;
        try
        {
            var signedIn = controller.Phase == SessionPhase.SignedIn;
            var subscriptions = controller.View == BrowseView.Subscriptions;
            C<Button>("HistoryButton").IsEnabled = signedIn;
            C<Button>("SubscriptionsButton").IsEnabled = signedIn;
            AutomationProperties.SetName(C<Button>("HistoryButton"), !settingsVisible && !subscriptions ? "History, selected" : "History");
            AutomationProperties.SetName(C<Button>("SubscriptionsButton"), !settingsVisible && subscriptions ? "Subscriptions, selected" : "Subscriptions");
            C<TextBlock>("ViewHeading").Text = settingsVisible ? "Settings" : subscriptions ? "Your subscriptions" : "Your history";
            C<TextBlock>("FeedDescription").Text = subscriptions ? "Subscribed channels and latest videos from the experimental YouTube TV feed, in provider order." : "History cards from YouTube TV. These are not timestamped watch events.";
            C<Grid>("ChannelsPanel").Visibility = ToVisibility(subscriptions);
            Grid.SetColumn(C<Grid>("VideosPanel"), subscriptions ? 1 : 0);
            Grid.SetColumnSpan(C<Grid>("VideosPanel"), subscriptions ? 1 : 2);
            C<TextBlock>("ChannelsStatusText").Text = controller.Channels.Count > 0 ? $"Subscribed channels ({controller.Channels.Count})" : controller.Busy ? "Loading subscribed channels…" : "No subscribed channels loaded.";
            C<TextBlock>("VideosStatusText").Text = controller.Videos.Count > 0 ? $"{(subscriptions ? "Latest subscription videos" : "History videos")} ({controller.Videos.Count})" : controller.Busy ? "Loading videos…" : "No videos loaded.";
            AutomationProperties.SetName(C<ListView>("HistoryList"), subscriptions ? "Latest subscription videos" : "Watch history");
            C<Grid>("HistoryPanel").Visibility = ToVisibility(!settingsVisible && signedIn);
            C<ScrollViewer>("SignInPanel").Visibility = ToVisibility(!settingsVisible && controller.Phase == SessionPhase.SignedOut);
            C<StackPanel>("DevicePanel").Visibility = ToVisibility(!settingsVisible && controller.Phase == SessionPhase.Authorizing);
            C<ProgressRing>("BusyRing").IsActive = controller.Busy;
            C<ProgressRing>("BusyRing").Visibility = ToVisibility(controller.Busy);
            C<TextBlock>("StatusText").Text = controller.Status;
            C<Button>("SignInButton").IsEnabled = !controller.Busy;
            C<Button>("RestoreButton").IsEnabled = !controller.Busy;
            C<Button>("SignOutButton").IsEnabled = true; // Also permits retrying a failed vault removal.
            C<Button>("CancelButton").Visibility = ToVisibility(controller.Busy);
            C<Button>("RefreshButton").IsEnabled = signedIn && !controller.Busy && controller.SelectedAccount is not null;
            C<Button>("LoadMoreButton").Visibility = ToVisibility(controller.Continuation is not null);
            C<Button>("LoadMoreButton").IsEnabled = signedIn && !controller.Busy;
            C<ListView>("HistoryList").IsEnabled = signedIn && !controller.Busy;
            C<ComboBox>("AccountChooser").IsEnabled = signedIn; // Switching invalidates a pending history call.
            var authorization = controller.Authorization;
            C<TextBlock>("DeviceCode").Text = authorization?.UserCode ?? "";
            C<TextBlock>("ExpiryText").Text = authorization is null ? "" : $"Code expires at {authorization.ExpiresAt.ToLocalTime():t}.";
            C<Button>("OpenActivationButton").IsEnabled = authorization is not null && authorization.ExpiresAt > DateTimeOffset.UtcNow && BrowserPolicy.IsActivationUri(authorization.VerificationUri);
            if (!ReferenceEquals(displayedAccounts, controller.Accounts))
            {
                displayedAccounts = controller.Accounts;
                var chooser = C<ComboBox>("AccountChooser");
                chooser.Items.Clear();
                foreach (var account in controller.Accounts)
                    chooser.Items.Add(new ComboBoxItem { Content = string.IsNullOrWhiteSpace(account.Handle) ? account.Name : $"{account.Name} · {account.Handle}", Tag = account });
                chooser.SelectedItem = chooser.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, controller.SelectedAccount));
            }
            if (!ReferenceEquals(displayedChannels, controller.Channels))
            {
                displayedChannels = controller.Channels;
                C<ListView>("ChannelsList").ItemsSource = controller.Channels;
            }
            if (!ReferenceEquals(displayedVideos, controller.Videos))
            {
                displayedVideos = controller.Videos;
                C<ListView>("HistoryList").ItemsSource = controller.Videos;
            }
        }
        finally { rendering = false; }
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
        AutomationProperties.SetName(args.ItemContainer, $"{video.Title}, {video.Channel}, {video.Duration}. Open in browser.");
        args.Handled = true;
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
        try
        {
            if (!await Launcher.LaunchUriAsync(uri) && !closed) C<TextBlock>("StatusText").Text = "Windows could not open your default browser.";
        }
        catch { if (!closed) C<TextBlock>("StatusText").Text = "Windows could not open your default browser."; }
    }
}
