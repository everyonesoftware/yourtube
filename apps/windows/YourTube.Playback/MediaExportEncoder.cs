using System.Diagnostics;

namespace YourTube.Playback;

internal static class MediaExportEncoder
{
    internal static ProcessStartInfo CreateStartInfo(string executable, MediaExportFormat format,
        string? video, string audio, string staged, bool reservedOutput = false, int? audioBitrateKbps = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(audio)!
        };
        start.Environment.Remove("FFREPORT");
        // An already verified, pinned empty scratch reservation can be opened for writing.
        // Neither the destination nor destination-side staging is ever passed to the converter.
        Add("-hide_banner", "-loglevel", "quiet", "-nostdin", "-xerror", reservedOutput ? "-y" : "-n");
        if (video is not null) Input(video);
        Input(audio);
        if (video is not null) Add("-map", "0:v:0", "-map", "1:a:0");
        else Add("-map", "0:a:0");
        Add("-map_metadata", "-1", "-map_chapters", "-1");
        switch (format)
        {
            case MediaExportFormat.Mp4:
                Add("-c:v", "copy", "-c:a", "copy", "-movflags", "+faststart", "-f", "mp4");
                break;
            case MediaExportFormat.WebM:
                Add("-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "32", "-deadline", "good", "-cpu-used", "4",
                    "-c:a", "libopus", "-b:a", "128k", "-f", "webm");
                break;
            case MediaExportFormat.Mp3:
                Add("-c:a", "libmp3lame");
                if (audioBitrateKbps is int bitrate)
                    Add("-ar", "44100", "-b:a", bitrate.ToString(System.Globalization.CultureInfo.InvariantCulture) + "k");
                else Add("-q:a", "2");
                Add("-f", "mp3");
                break;
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
        Add(staged);
        return start;

        void Input(string path) => Add("-protocol_whitelist", "file", "-enable_drefs", "0", "-f", "mov", "-i", path);
        void Add(params string[] args)
        {
            foreach (string arg in args) start.ArgumentList.Add(arg);
        }
    }

    internal static async Task RunAsync(string executable, MediaExportOption option, EncoderScratch scratch,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            scratch.ProtectForEncoding();
            token.ThrowIfCancellationRequested();
            using var process = Process.Start(CreateStartInfo(executable, option.Format, scratch.Video, scratch.Audio,
                scratch.Output, scratch.ReservedOutput, option.AudioBitrateKbps)) ?? throw EncoderFailed();
            var discard = Task.WhenAll(process.StandardError.BaseStream.CopyToAsync(Stream.Null, token),
                process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token));
            try
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                await discard.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw EncoderFailed();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // Process already exited.
                await process.WaitForExitAsync().ConfigureAwait(false);
                try { await discard.ConfigureAwait(false); } catch (OperationCanceledException) { }
                throw;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { token.ThrowIfCancellationRequested(); throw EncoderFailed(); }
    }

    private static PlaybackException EncoderFailed() => new("ffmpeg_failed", "FFmpeg could not create the selected media format.");
}