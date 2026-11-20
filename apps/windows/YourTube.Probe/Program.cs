using YourTube.Probe;
using System.Diagnostics;

var signIn = args.Length == 1 && args[0] == "--signin";
if (args.Length > 0 && !signIn)
{
    Console.Error.WriteLine("Usage: YourTube.Probe [--signin]");
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
    return await ProbeRunner.RunAsync(new HttpClientHandler { AllowAutoRedirect = false }, Console.Out, signIn, authorization =>
    {
        // Only this explicitly interactive, non-redirected local terminal sees the user code.
        // Device codes, credentials and tokens are never printed or placed on command lines.
        Console.Error.WriteLine($"Open {authorization.VerificationUri.AbsoluteUri} and enter the activation code shown below:");
        Console.Error.WriteLine(authorization.UserCode);
        try { Process.Start(new ProcessStartInfo(authorization.VerificationUri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { Console.Error.WriteLine("Open the activation page manually in your browser."); }
    }, cancellation.Token);
}
catch (Exception)
{
    Console.Error.WriteLine("probe_error=unexpected_failure");
    return 1;
}
