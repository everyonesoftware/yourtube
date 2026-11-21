using YourTube.App;

var epoch = new RequestEpoch();
var stale = epoch.Begin();
var current = epoch.Begin();
var writes = 0;
Check(!epoch.Commit(stale.Id, () => writes++), "stale completion must not commit");
Check(stale.Token.IsCancellationRequested, "replacing an operation cancels it");
Check(epoch.Commit(current.Id, () => writes++), "current completion can commit");
Check(writes == 1, "only current vault/state write is accepted");
Console.WriteLine("PASS: request epochs reject stale writes and cancel superseded work.");
Check(BrowserPolicy.IsActivationUri(new Uri("https://www.google.com/device")), "Google device activation permitted");
foreach (var address in new[] { "http://www.google.com/device", "https://www.google.com.evil.test/device", "https://evil.test/", "https://user@google.com/device", "https://google.com:444/device", "https://google.com/url?q=https://evil.test" })
    Check(!BrowserPolicy.IsActivationUri(new Uri(address)), "unsafe activation URI rejected");
Check(BrowserPolicy.VideoUri("aB_cD-eF012")?.AbsoluteUri == "https://www.youtube.com/watch?v=aB_cD-eF012", "canonical explicit video activation");
Check(BrowserPolicy.VideoUri("../bad") is null, "invalid video ID rejected");
Console.WriteLine("PASS: browser allowlists and canonical video IDs.");

AutoStartupPreferenceTests.Run();
await PlaybackStateTests.Run();
await StateTests.Run();
await FailureTests.Run();
await SubscriptionStateTests.Run();
WiringTests.Run(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
if (args.Length > 1) PeTests.Run(args[1], args.Length > 2 ? args[2] : "0.1.1");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
}
