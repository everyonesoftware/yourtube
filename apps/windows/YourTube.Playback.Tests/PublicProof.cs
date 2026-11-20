using System.Diagnostics;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;
namespace YourTube.Playback.Tests;

// Explicit public-network opt-in; no credentials, media files or signed URL diagnostics.
public sealed class PublicProofFactAttribute : FactAttribute
{
    public PublicProofFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("YOURTUBE_PUBLIC_PLAYBACK_PROOF") != "1") Skip = "Public playback proof requires explicit network opt-in.";
    }
}
public sealed class PublicProof(ITestOutputHelper output)
{
    // Observe status only, using the same network handler as the default resolver.
    private sealed class RedirectCounter() : DelegatingHandler(Playback.PlaybackResolver.CreatePublicHandler())
    {
        private int cdnRedirects;
        internal int CdnRedirects => Volatile.Read(ref cdnRedirects);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 &&
                request.RequestUri is { } uri && Playback.PublicTransport.IsCdn(uri))
                Interlocked.Increment(ref cdnRedirects);
            return response;
        }
    }

    [PublicProofFact] public async Task ResolvePublicVideosAndReadDash()
    {
        var network = new RedirectCounter();
        using var resolver = new Playback.PlaybackResolver(network);
        foreach (var id in new[] { "jNQXAC9IVRw", "aqz-KE-bpKQ", "YE7VzlLtp-4" })
        {
            int redirectsBefore = network.CdnRedirects;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            Playback.PlaybackMedia? media = null; string? failure = null;
            try { media = await resolver.ResolveAsync(id, deadline.Token); }
            catch (Playback.PlaybackException e) { failure = e.Code; }
            catch { failure = "cancelled_or_unavailable"; }
            Assert.True(media is not null, "Public resolution failed: " + (failure ?? "unknown"));
            XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
            var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(media!.Manifest));
            Assert.True(doc.Descendants(ns + "Representation").Count() >= 2);
            foreach (var representation in doc.Descendants(ns + "Representation"))
            {
                var uri = new Uri(representation.Element(ns + "BaseURL")!.Value);
                var parts = representation.Element(ns + "SegmentBase")!.Attribute("indexRange")!.Value.Split('-');
                var offset = ulong.Parse(parts[0]); var length = ulong.Parse(parts[1]) - offset + 1;
                Playback.PlaybackResource? read = null; string? readFailure = null;
                try { read = await resolver.ReadResourceAsync(media, uri, offset, length, deadline.Token); }
                catch (Playback.PlaybackException e) { readFailure = e.Code; }
                catch { readFailure = "cancelled_or_unavailable"; }
                Assert.True(read is not null && (ulong)read.Bytes.Length == length, "Public nonzero media read failed: " + (readFailure ?? "unknown"));
            }
            // Manifest is passed only through stdin. Never on disk or in process args.
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("YOURTUBE_FFMPEG") ?? "ffmpeg") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-nostdin", "-v", "quiet", "-xerror", "-protocol_whitelist", "pipe,https,tls,tcp", "-f", "dash", "-i", "pipe:0", "-map", "0:v:0", "-map", "0:a:0", "-t", "2", "-progress", "pipe:1", "-f", "null", "-" }) start.ArgumentList.Add(arg);
            start.Environment.Remove("FFREPORT");
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token); var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.StandardInput.BaseStream.WriteAsync(media.Manifest, deadline.Token); process.StandardInput.Close();
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { process.Kill(true); Assert.Fail("Public DASH reader timed out."); }
            var progress = await stdout; await stderr; // Never expose reader diagnostics containing URLs.
            Assert.True(process.ExitCode == 0, "Public DASH reader rejected manifest.");
            var frames = progress.Split('\n').Where(line => line.StartsWith("frame=", StringComparison.Ordinal)).Select(line => long.TryParse(line[6..].Trim(), out var count) ? count : 0).DefaultIfEmpty().Max();
            var elapsed = progress.Split('\n').Where(line => line.StartsWith("out_time_us=", StringComparison.Ordinal)).Select(line => long.TryParse(line[12..].Trim(), out var count) ? count : 0).DefaultIfEmpty().Max();
            Assert.True(frames > 0 && elapsed >= 1900000, "DASH reader did not decode advancing audio/video.");
            output.WriteLine($"public_video={id} qualities={media.Qualities.Count} dash_reader=accepted decoded_frames={frames} decoded_microseconds={elapsed} resolver_cdn_redirects={network.CdnRedirects - redirectsBefore}");
        }
    }
}
