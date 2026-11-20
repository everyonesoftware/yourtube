using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using YourTube.App;
using YourTube.Update;

namespace YourTube.Settings.NativeValidation;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        string? evidence = Environment.GetEnvironmentVariable("YOURTUBE_SETTINGS_EVIDENCE");
        string? sourceSha = Environment.GetEnvironmentVariable("YOURTUBE_SETTINGS_SOURCE");
        Environment.ExitCode = 1;
        if (evidence is null || !Path.IsPathFullyQualified(evidence) || !Directory.Exists(evidence) ||
            sourceSha is null || !Regex.IsMatch(sourceSha, @"\A[a-f0-9]{40}\z")) return;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new SettingsSmokeApplication(evidence, sourceSha);
        });
    }
}

internal sealed partial class SettingsSmokeApplication(string evidence, string sourceSha) : Application, IXamlMetadataProvider
{
    private readonly Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider metadata = new();
    private Window? window;
    public IXamlType GetXamlType(Type type) => metadata.GetXamlType(type);
    public IXamlType GetXamlType(string name) => metadata.GetXamlType(name);
    public XmlnsDefinition[] GetXmlnsDefinitions() => metadata.GetXmlnsDefinitions();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var fixture = new FixtureEngine();
        var verifiedLinks = new List<LinkEvidence>();
        int linkClicks = 0;
        string stage = "startup";
        bool passed = false;
        try
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            using var panel = new WindowsUpdatesPanel(null, new WindowsUpdateBackend(() => fixture));
            panel.View.Margin = new Thickness(20);
            var host = new ScrollViewer { Content = panel.View, RequestedTheme = ElementTheme.Dark };
            window = new Window { Title = "YourTube Settings smoke (fixture)", Content = host };
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 760));
            window.Activate();

            var check = (Button)panel.View.FindName("Check");
            var notes = (Expander)panel.View.FindName("Notes");
            var content = (StackPanel)panel.View.FindName("NotesContent");
            stage = "initial-state";
            await Until(() => IsRealized(check));
            Require(fixture.Checks == 0 && fixture.NotesLoads == 0 && notes.Visibility == Visibility.Collapsed);
            Require(((TextBlock)panel.View.FindName("CurrentVersion")).Text == "Current Version: 0.1.3");

            stage = "check-fixture-offer";
            (Peer(check) as IInvokeProvider
                ?? throw new InvalidOperationException()).Invoke();
            await Until(() => IsRealized(notes) && notes.Header as string == "What’s new · 2 changes");
            Require(((TextBlock)panel.View.FindName("Title")).Text == "Update available");
            Require(((TextBlock)panel.View.FindName("OfferedVersion")).Text == "YourTube 0.1.9");
            Require(fixture.Checks == 1 && fixture.NotesLoads == 1 && !notes.IsExpanded);
            var details = content.Children.OfType<Expander>().Single();
            Require(details.Header as string == "Commit details" && !details.IsExpanded);
            var detailBody = (StackPanel)details.Content;
            var links = new[] { (HyperlinkButton)detailBody.Children[0] }
                .Concat(detailBody.Children.Skip(1).Cast<Grid>().Select(row => (HyperlinkButton)row.Children[0])).ToArray();
            foreach (var link in links) link.Click += (_, _) => linkClicks++;
            Require(!IsRealized(details));

            stage = "expand-whats-new";
            Disclosure(notes).Expand();
            await Until(() => notes.IsExpanded && IsRealized(details));
            Require(!details.IsExpanded && !links.Any(IsRealized));
            Require(content.Children.OfType<TextBlock>().Select(text => text.Text).SequenceEqual(
                new[] { "Improvements", "• Improve fixture navigation", "Fixes", "• Fix fixture focus" }));

            stage = "expand-commit-details";
            Disclosure(details).Expand();
            await Until(() => details.IsExpanded && links.All(IsRealized));
            Require(detailBody.Children.Skip(1).Cast<Grid>().Select(row => ((TextBlock)row.Children[1]).Text)
                .SequenceEqual(FixtureEngine.Commits.Select(commit => commit.Subject)));
            Require(links.Length == 1 + FixtureEngine.Commits.Length);

            stage = "commit-boxes-and-keyboard-focus";
            const string repo = "https://github.com/everyonesoftware/yourtube";
            await VerifyLink(links[0], "1234567…fedcba9", $"{repo}/compare/{FixtureEngine.BaseSha}...{FixtureEngine.HeadSha}");
            for (int i = 0; i < FixtureEngine.Commits.Length; i++)
            {
                string sha = FixtureEngine.Commits[i].Sha;
                await VerifyLink(links[i + 1], sha[..7], $"{repo}/commit/{sha}");
            }

            stage = "collapse-and-reopen";
            Disclosure(details).Collapse();
            await Until(() => !details.IsExpanded && !links.Any(IsRealized));
            Disclosure(notes).Collapse();
            await Until(() => !notes.IsExpanded && !IsRealized(details));
            Disclosure(notes).Expand();
            await Until(() => notes.IsExpanded && IsRealized(details));
            Require(!details.IsExpanded);
            Disclosure(details).Expand();
            await Until(() => details.IsExpanded && links.All(IsRealized));
            Require(fixture.Checks == 1 && fixture.NotesLoads == 1 && fixture.Downloads == 0 && fixture.Applies == 0 && linkClicks == 0);

            stage = "complete";
            passed = true;
        }
        catch (Exception)
        {
            // Persist the fixed stage only, never transport diagnostics or exception details.
        }
        finally
        {
            try
            {
                string proof = Path.Combine(evidence, "proof.json");
                File.WriteAllText(proof + ".tmp", JsonSerializer.Serialize(new
                {
                    sourceSha, result = passed ? "passed" : "failed", stage, fixture.Checks, fixture.NotesLoads,
                    fixture.Downloads, fixture.Applies, linkClicks, links = verifiedLinks
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                File.Move(proof + ".tmp", proof);
                if (passed) Environment.ExitCode = 0;
            }
            finally { window?.Close(); }
        }

        async Task VerifyLink(HyperlinkButton link, string label, string target)
        {
            var peer = Peer(link);
            Require(link.Content as string == label && link.NavigateUri?.AbsoluteUri == target);
            Require(link.IsTabStop && link.UseSystemFocusVisuals && peer.IsKeyboardFocusable());
            Require(peer.GetName().Contains(label.Contains('…') ? "Compare source range" : label, StringComparison.Ordinal));
            Require(link.CornerRadius.TopLeft == 4 && link.BorderThickness.Left == 1 &&
                link.Background is SolidColorBrush { Color.A: > 0 } && link.BorderBrush is SolidColorBrush { Color.A: > 0 });
            // Exercise native keyboard focus without activating the URI or opening a browser.
            Require(link.Focus(FocusState.Keyboard));
            await Until(() => Equals(FocusManager.GetFocusedElement(link.XamlRoot), link) && link.FocusState == FocusState.Keyboard);
            verifiedLinks.Add(new(label, target, true));
        }
    }

    private static AutomationPeer Peer(UIElement element) => FrameworkElementAutomationPeer.CreatePeerForElement(element)
        ?? throw new InvalidOperationException();
    private static IExpandCollapseProvider Disclosure(Expander expander) => Peer(expander) as IExpandCollapseProvider
        ?? throw new InvalidOperationException();
    private static bool IsRealized(FrameworkElement element)
    {
        if (!element.IsLoaded || element.XamlRoot is null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;
        // A collapsed template ancestor can hide loaded controls that still have an arranged size.
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException(); }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(25, timeout.Token);
    }
    private sealed record LinkEvidence(string Label, string Target, bool KeyboardFocused);
}
