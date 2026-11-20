using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Velopack;

namespace YourTube.App;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        // Lifecycle hooks must run before UI or credentials open. Installing is explicit.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new YourTubeApplication();
        });
    }
}

public sealed partial class YourTubeApplication : Application, Microsoft.UI.Xaml.Markup.IXamlMetadataProvider
{
    private readonly Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider metadata = new();
    private MainWindow? window;
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(Type type) => metadata.GetXamlType(type);
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(string name) => metadata.GetXamlType(name);
    public Microsoft.UI.Xaml.Markup.XmlnsDefinition[] GetXmlnsDefinitions() => metadata.GetXmlnsDefinitions();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Application resources cannot be touched before WinUI initializes its core.
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        Resources["SystemAccentColor"] = Windows.UI.Color.FromArgb(255, 192, 215, 174);
        window = new MainWindow();
        window.Activate();
    }
}
