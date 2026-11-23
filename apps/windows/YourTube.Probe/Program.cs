using YourTube.Probe;
using System.Diagnostics;

var includePlaybackContext = args.FirstOrDefault() == "--watch-context";
var lookupVideos = includePlaybackContext || args.FirstOrDefault() == "--watch-lookups" ? args[1..] : null;
var signIn = args is ["--signin"] || (lookupVideos is not null && ProbeRunner.IsLookupBatch(lookupVideos));
if (args.Length > 0 && !signIn)
{
    Console.Error.WriteLine("Usage: YourTube.Probe [--signin | --watch-lookups VIDEO_ID [VIDEO_ID ...] | --watch-context VIDEO_ID [VIDEO_ID ...]]");
    return 2;
}
if (signIn && (Console.IsErrorRedirected || Console.IsInputRedirected))
{
    Console.Error.WriteLine("Interactive sign-in requires a local terminal. No activation code was issued.");
    return 2;
}
using var cancellation = new CancellationTokenSource(signIn ? TimeSpan.FromMinutes(35) : TimeSpan.FromMinutes(2));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    return await ProbeRunner.RunAsync(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, Console.Out, signIn, authorization =>
    {
        // Only this explicitly interactive, non-redirected local terminal sees the user code.
        // Device codes, credentials and tokens are never printed or placed on command lines.
        Console.Error.WriteLine($"Open {authorization.VerificationUri.AbsoluteUri} and enter the activation code shown below:");
        Console.Error.WriteLine(authorization.UserCode);
        try { Process.Start(new ProcessStartInfo(authorization.VerificationUri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { Console.Error.WriteLine("Open the activation page manually in your browser."); }
    }, cancellation.Token, lookupVideos, includePlaybackContext);
}
catch (Exception)
{
    Console.Error.WriteLine("probe_error=unexpected_failure");
    return 1;
}
