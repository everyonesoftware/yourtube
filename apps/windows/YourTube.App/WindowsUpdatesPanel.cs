using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using YourTube.Update;

namespace YourTube.App;

/// <summary>Native Settings content. No startup check, download, or apply. Dispose when the window closes.</summary>
public sealed class WindowsUpdatesPanel : IDisposable
{
    private const string Markup = """
    <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Spacing="16" MaxWidth="760" HorizontalAlignment="Stretch">
      <Grid ColumnSpacing="16">
        <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
        <TextBlock x:Name="CurrentVersion" FontSize="20" VerticalAlignment="Center" TextWrapping="Wrap"/>
        <Button x:Name="Check" Grid.Column="1" Content="Check for updates" VerticalAlignment="Center"/>
      </Grid>
      <Border Padding="20" BorderThickness="1" CornerRadius="8" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}">
        <StackPanel Spacing="12">
          <Grid ColumnSpacing="16">
            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
            <TextBlock x:Name="Title" FontSize="20" FontWeight="SemiBold" TextWrapping="Wrap"/>
            <TextBlock x:Name="OfferedVersion" Grid.Column="1" VerticalAlignment="Center" TextWrapping="Wrap"/>
          </Grid>
          <TextBlock x:Name="Status" TextWrapping="Wrap"/>
          <Expander x:Name="Notes" Header="What’s new" Visibility="Collapsed" HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch">
            <TextBlock Text="Changelog unavailable for this proof of concept. You can still download and install the update." TextWrapping="Wrap"/>
          </Expander>
          <ProgressBar x:Name="Progress" Visibility="Collapsed"/>
          <StackPanel x:Name="Actions" Orientation="Horizontal" Spacing="12" Visibility="Collapsed">
            <Button x:Name="Download" Content="Download update" Visibility="Collapsed"/>
            <Button x:Name="Restart" Content="Restart and install" Visibility="Collapsed"/>
          </StackPanel>
        </StackPanel>
      </Border>
    </StackPanel>
    """;
    public FrameworkElement View { get; }
    private readonly WindowsUpdateFlow flow;
    private readonly DispatcherQueue dispatcher;
    private readonly TextBlock currentVersion, title, status, offeredVersion;
    private readonly Button check, download, restart;
    private readonly StackPanel actions;
    private readonly ProgressBar progress;
    private readonly Expander notes;
    private bool disposed;
    private Version? renderedOffer;

    // Optional hook must throw to block installation if app-specific persistence cannot complete.
    // No Google sign-out is needed: updater transport never receives account credentials.
    public WindowsUpdatesPanel(Action? beforeRestart = null)
    {
        View = (FrameworkElement)XamlReader.Load(Markup);
        dispatcher = View.DispatcherQueue;
        currentVersion = Control<TextBlock>("CurrentVersion");
        title = Control<TextBlock>("Title"); status = Control<TextBlock>("Status");
        offeredVersion = Control<TextBlock>("OfferedVersion");
        check = Control<Button>("Check"); download = Control<Button>("Download"); restart = Control<Button>("Restart");
        actions = Control<StackPanel>("Actions"); progress = Control<ProgressBar>("Progress"); notes = Control<Expander>("Notes");
        // Reading the app assembly does not initialize the native Velopack locator.
        var version = typeof(WindowsUpdatesPanel).Assembly.GetName().Version;
        currentVersion.Text = $"Current Version: {version?.ToString(3) ?? "unavailable"}";
        flow = new WindowsUpdateFlow(new WindowsUpdateBackend(), beforeRestart);
        flow.Changed += Changed;
        check.Click += CheckClicked; download.Click += DownloadClicked; restart.Click += RestartClicked;
        Render();
    }
    private T Control<T>(string name) where T : FrameworkElement => (T)View.FindName(name);
    private async void CheckClicked(object sender, RoutedEventArgs e) => await flow.CheckAsync();
    private async void DownloadClicked(object sender, RoutedEventArgs e) => await flow.DownloadAsync();
    private async void RestartClicked(object sender, RoutedEventArgs e) => await flow.RestartAsync();
    private void Changed() => dispatcher.TryEnqueue(() => { if (!disposed) Render(); });
    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private void Render()
    {
        var state = flow.State;
        title.Text = state.Phase switch
        {
            UpdatePhase.Idle => "Not checked yet",
            UpdatePhase.Checking => "Checking for updates",
            UpdatePhase.Current => "Up to date",
            UpdatePhase.Available => "Update available",
            UpdatePhase.Downloading => "Downloading update",
            UpdatePhase.Ready => "Ready to install",
            UpdatePhase.Restarting => "Restarting",
            UpdatePhase.NotInstalled => "Updates unavailable",
            _ => "Update failed"
        };
        status.Text = state.Phase == UpdatePhase.Downloading && state.Progress < 100 ? $"{state.Message} {state.Progress}%" : state.Message;
        status.Visibility = Visible(state.Phase != UpdatePhase.Available);
        offeredVersion.Text = state.Version is { } version ? $"YourTube {version}" : "";
        offeredVersion.Visibility = Visible(state.Version is not null);
        if (renderedOffer != state.Version) notes.IsExpanded = false;
        renderedOffer = state.Version;
        notes.Visibility = Visible(state.Version is not null);
        bool busy = state.Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Restarting;
        check.Content = state.Phase == UpdatePhase.Checking ? "Checking…" : "Check for updates";
        check.IsEnabled = !busy && state.Phase is not (UpdatePhase.Ready or UpdatePhase.NotInstalled);
        actions.Visibility = Visible(state.Phase is UpdatePhase.Available or UpdatePhase.Ready);
        download.Visibility = Visible(state.Phase == UpdatePhase.Available);
        restart.Visibility = Visible(state.Phase == UpdatePhase.Ready);
        download.IsEnabled = !busy; restart.IsEnabled = !busy;
        progress.Visibility = Visible(busy);
        progress.IsIndeterminate = state.Phase is UpdatePhase.Checking or UpdatePhase.Restarting || state.Phase == UpdatePhase.Downloading && state.Progress >= 100;
        progress.Value = state.Progress;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        check.Click -= CheckClicked; download.Click -= DownloadClicked; restart.Click -= RestartClicked;
        flow.Changed -= Changed;
        flow.Dispose();
    }
}
