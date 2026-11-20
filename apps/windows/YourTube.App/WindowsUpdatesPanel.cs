using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using System.Reflection;
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
          <Expander x:Name="Notes" Header="What’s new" IsExpanded="False" Visibility="Collapsed" HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch">
            <StackPanel Spacing="8">
              <TextBlock x:Name="NotesStatus" TextWrapping="Wrap" Visibility="Collapsed"/>
              <Button x:Name="RetryNotes" Content="Retry changelog" Visibility="Collapsed"/>
              <StackPanel x:Name="NotesContent" Spacing="8" Visibility="Collapsed"/>
            </StackPanel>
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
    private readonly TextBlock currentVersion, title, status, offeredVersion, notesStatus;
    private readonly Button check, download, restart, retryNotes;
    private readonly StackPanel actions, notesContent;
    private readonly ProgressBar progress;
    private readonly Expander notes;
    private bool disposed;
    private Version? renderedOffer;
    private WindowsChangelog? renderedChanges;
    private bool renderedLoading;

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
        notesStatus = Control<TextBlock>("NotesStatus"); notesContent = Control<StackPanel>("NotesContent");
        retryNotes = Control<Button>("RetryNotes");
        // Reading the app assembly does not initialize the native Velopack locator.
        var assembly = typeof(WindowsUpdatesPanel).Assembly;
        string? version = assembly.GetName().Version?.ToString(3);
        currentVersion.Text = $"Current Version: {version ?? "unavailable"}";
        string? info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string? installedSourceSha = version is not null && info?.StartsWith(version + "+", StringComparison.Ordinal) == true
            ? info[(version.Length + 1)..] : null;
        flow = new WindowsUpdateFlow(new WindowsUpdateBackend(installedSourceSha: installedSourceSha), beforeRestart);
        flow.Changed += Changed;
        check.Click += CheckClicked; download.Click += DownloadClicked; restart.Click += RestartClicked;
        retryNotes.Click += RetryNotesClicked;
        Render();
    }
    private T Control<T>(string name) where T : FrameworkElement => (T)View.FindName(name);
    private async void CheckClicked(object sender, RoutedEventArgs e) => await flow.CheckAsync();
    private async void RetryNotesClicked(object sender, RoutedEventArgs e) => await flow.RetryNotesAsync();
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
        if (renderedOffer != state.Version || renderedLoading != state.NotesLoading || !ReferenceEquals(renderedChanges, state.Changelog))
        {
            if (renderedOffer != state.Version) notes.IsExpanded = false;
            renderedOffer = state.Version;
            renderedChanges = state.Changelog;
            renderedLoading = state.NotesLoading;
            RenderNotes(state.Changelog, state.NotesLoading);
        }
        notes.Visibility = Visible(state.Version is not null);
        retryNotes.IsEnabled = state.Phase == UpdatePhase.Available;
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
    private void RenderNotes(WindowsChangelog? changes, bool loading)
    {
        notesContent.Children.Clear();
        notesContent.Visibility = Visible(changes is not null);
        notesStatus.Visibility = Visible(changes is null);
        retryNotes.Visibility = Visible(changes is null && !loading);
        notesStatus.Text = loading ? "Loading changelog…" : changes is null ? "Changelog unavailable. You can still download or install the update." : "";
        notes.Header = "What’s new";
        if (changes is null) return;

        int count = changes.Improvements.Count + changes.Fixes.Count;
        notes.Header = $"What’s new · {count} {(count == 1 ? "change" : "changes")}";
        AddGroup("Improvements", changes.Improvements);
        AddGroup("Fixes", changes.Fixes);
        if (count == 0) notesContent.Children.Add(NoteText("No user-facing Windows changes in this source range."));

        var details = new StackPanel { Spacing = 8 };
        details.Children.Add(NoteText($"{changes.BaseRevision[..7]}…{changes.HeadRevision[..7]}"));
        foreach (var commit in changes.Commits)
            details.Children.Add(NoteText($"{commit.Sha[..7]}  {commit.Subject}"));
        notesContent.Children.Add(new Expander { Header = "Commit details", Content = details,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
    }
    private void AddGroup(string heading, IReadOnlyList<string> entries)
    {
        if (entries.Count == 0) return;
        notesContent.Children.Add(new TextBlock { Text = heading, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (string entry in entries) notesContent.Children.Add(NoteText($"• {entry}"));
    }
    private static TextBlock NoteText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        check.Click -= CheckClicked; download.Click -= DownloadClicked; restart.Click -= RestartClicked;
        retryNotes.Click -= RetryNotesClicked;
        flow.Changed -= Changed;
        flow.Dispose();
    }
}
