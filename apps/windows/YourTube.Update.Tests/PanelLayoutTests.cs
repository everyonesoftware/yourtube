using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;
namespace YourTube.Update.Tests;

/// <summary>XML/source contracts only: not proof that native WinUI instantiated the controls.</summary>
public sealed class PanelLayoutTests
{
    [Fact]
    public void Runtime_markup_is_xml_and_named_controls_match_real_lookups()
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WindowsUpdatesPanel.source"));
        string markup = source.Split("\"\"\"", StringSplitOptions.None)[1];
        var root = XDocument.Parse(markup).Root!;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var named = root.DescendantsAndSelf().Where(e => e.Attribute(x + "Name") is not null).ToDictionary(e => e.Attribute(x + "Name")!.Value);
        foreach (Match match in Regex.Matches(source, "Control<(?<type>\\w+)>\\(\"(?<name>\\w+)\"\\)"))
            Assert.Equal(match.Groups["type"].Value, named[match.Groups["name"].Value].Name.LocalName);
        Assert.Equal(named["CurrentVersion"].Parent, named["Check"].Parent);
        Assert.Equal("Collapsed", named["Notes"].Attribute("Visibility")?.Value);
        Assert.Equal("What’s new", named["Notes"].Attribute("Header")?.Value);
        Assert.Contains("Changelog unavailable", named["Notes"].Descendants().Single().Attribute("Text")?.Value);
        Assert.DoesNotContain(root.DescendantsAndSelf().Attributes(), a => a.Name == x + "Class" || a.Name.LocalName == "Click");
    }
}
