using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class PeTests
{
    public static void Run(string assemblyPath, string expectedVersion = "0.1.1")
    {
        var expected = Version.Parse(expectedVersion);
        var expectedAssembly = new Version(expected.Major, expected.Minor, expected.Build, expected.Revision < 0 ? 0 : expected.Revision);
        using var input = File.OpenRead(assemblyPath);
        using var pe = new PEReader(input);
        StateTests.Verify(pe.PEHeaders.CoffHeader.Machine == Machine.Amd64, "compiled app targets Windows x64");
        var metadata = pe.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        StateTests.Verify(metadata.GetString(assembly.Name) == "YourTube" && assembly.Version == expectedAssembly, "emitted application identity/version");
        StateTests.Verify(metadata.ManifestResources.Select(h => metadata.GetString(metadata.GetManifestResource(h).Name)).Contains("YourTube.App.Shell.xaml"), "actual assembly contains runtime XAML resource");
        var version = FileVersionInfo.GetVersionInfo(assemblyPath);
        StateTests.Verify(version.FileVersion == expectedAssembly.ToString(), "actual PE file version");
        Console.WriteLine("PASS: real Windows x64 PE metadata, version and embedded shell resource.");
    }
}
