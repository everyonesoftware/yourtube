namespace YourTube.Playback;

public enum MediaExportFormat { Mp4, WebM, M4a, Mp3 }

public sealed record MediaExportOption(MediaExportFormat Format, int? Height, long SourceBytes)
{
    public string Extension => Format switch
    {
        MediaExportFormat.Mp4 => ".mp4",
        MediaExportFormat.WebM => ".webm",
        MediaExportFormat.M4a => ".m4a",
        MediaExportFormat.Mp3 => ".mp3",
        _ => throw new ArgumentOutOfRangeException(nameof(Format))
    };
}

public sealed partial class PlaybackResolver
{
    // Each transfer retains at most one bounded chunk, even for multi-gigabyte media.
    internal const ulong ExportChunkSize = 4 * 1024 * 1024;

    private sealed record ExportSelection(MediaExportOption Option, MediaTrack? Video, MediaTrack Audio);

    /// <summary>Returns choices for this resolver's current source, without any network or filesystem work.</summary>
    public IReadOnlyList<MediaExportOption> GetExportOptions(PlaybackMedia media) =>
        disposed || media is null || !ownership.TryGetValue(media, out var owned) || media.ExpiresAt <= exportClock.GetUtcNow()
            ? [] : Array.AsReadOnly(ExportSelections(owned).Select(selection => selection.Option).ToArray());

    private static IEnumerable<ExportSelection> ExportSelections(OwnedSource source)
    {
        var audio = source.Tracks.Values.Where(track => track.Height == 0).MaxBy(track => track.Bitrate);
        if (audio is null) yield break;
        var videos = source.Tracks.Values.Where(track => track.Height > 0 && track.Size + audio.Size <= long.MaxValue)
            .GroupBy(track => track.Height).OrderBy(group => group.Key).Select(group => group.MaxBy(track => track.Bitrate)!);
        foreach (var format in new[] { MediaExportFormat.Mp4, MediaExportFormat.WebM })
            foreach (var video in videos)
                yield return new(new(format, video.Height, (long)(video.Size + audio.Size)), video, audio);
        yield return new(new(MediaExportFormat.M4a, null, (long)audio.Size), null, audio);
        yield return new(new(MediaExportFormat.Mp3, null, (long)audio.Size), null, audio);
    }

    /// <summary>Exports to the explicitly selected full path. An existing destination is never replaced.</summary>
    /// <remarks>Progress is a fraction of source bytes up to 0.95, then 0.99 once publication can no longer be canceled,
    /// and 1 after the file is published. FFmpeg is not used for M4A.</remarks>
    public async Task ExportAsync(PlaybackMedia media, MediaExportOption option, string destination,
        string? ffmpegPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default,
        DownloadJournal? journal = null)
    {
        EncoderScratch? scratch = null;
        long copied = 0;
        const int preparing = 0, canceled = 1, committing = 2;
        int publication = preparing;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var cancellation = cancellationToken.UnsafeRegister(
                _ => Interlocked.CompareExchange(ref publication, canceled, preparing), null);
            CheckDisposed();
            if (media is null || !ownership.TryGetValue(media, out var owned)) throw PublicTransport.Unsafe();
            CheckExportLifetime(media, cancellationToken);
            var selection = ExportSelections(owned).SingleOrDefault(candidate => candidate.Option == option)
                ?? throw new PlaybackException("unsupported_export", "The requested media export option is unavailable.");

            string finalPath = ExportDestination(destination, option.Extension);
            if (journal is not null && !DownloadJournal.IsSafeDestination(finalPath, option.Extension))
                throw new PlaybackException("invalid_destination", "Choose an ordinary local file path for this download.");
            bool needsEncoder = option.Format != MediaExportFormat.M4a;
            if (needsEncoder && (string.IsNullOrEmpty(ffmpegPath) || !Path.IsPathFullyQualified(ffmpegPath) || !File.Exists(ffmpegPath)))
                throw new PlaybackException("ffmpeg_required", "Select a working FFmpeg executable to export this format.");

            Report(progress, 0);
            CheckExportLifetime(media, cancellationToken);
            if (needsEncoder)
            {
                Persist(() => scratch = new EncoderScratch(option, finalPath, media.Title, journal));
                string audio = scratch!.Audio;
                string? video = scratch.Video;
                if (video is not null)
                {
                    await using var input = scratch.OpenInput(video);
                    await CopyTrackAsync(selection.Video!, input).ConfigureAwait(false);
                }
                await using (var input = scratch.OpenInput(audio))
                    await CopyTrackAsync(selection.Audio, input).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                await MediaExportEncoder.RunAsync(ffmpegPath!, option.Format, scratch, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Guid operation = Guid.NewGuid();
            var staged = new MediaExportStaging(finalPath, operation);
            bool recorded = false;
            try
            {
                if (journal is not null)
                {
                    Persist(() => journal.Begin(operation, media.Title, option, finalPath, journal.Capture(staged)));
                    recorded = true;
                }
                if (scratch is null)
                    await CopyTrackAsync(selection.Audio, staged.Stream).ConfigureAwait(false);
                else
                {
                    await using var encoded = scratch.OpenEncoded();
                    await encoded.CopyToAsync(staged.Stream, 64 * 1024, cancellationToken).ConfigureAwait(false);
                }

                await staged.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (staged.Stream.Length == 0)
                    throw new PlaybackException("export_failed", "The media export produced no output.");
                if (journal is not null) staged.Stream.Flush(flushToDisk: true);
                CheckDisposed();
                // Reserve publication atomically against the cancellation callback. Check the token
                // after reserving in case notification was requested but its callback is still pending.
                if (Interlocked.CompareExchange(ref publication, committing, preparing) != preparing ||
                    cancellationToken.IsCancellationRequested)
                {
                    Volatile.Write(ref publication, canceled);
                    throw new OperationCanceledException(cancellationToken);
                }
                Report(progress, 0.99);
                Action? acknowledgePublication = null;
                if (journal is not null)
                    Persist(() => acknowledgePublication = journal.PreparePublication(operation, staged.Stream.Length, exportClock.GetUtcNow().ToUniversalTime()));
                try { staged.Publish(finalPath); }
                catch (IOException) when (File.Exists(finalPath) || Directory.Exists(finalPath))
                {
                    throw MediaExportStaging.DestinationExists();
                }
                acknowledgePublication?.Invoke();
            }
            finally
            {
                staged.Dispose();
                if (recorded && staged.Cleaned) journal!.NoteCleaned(operation);
            }
            Report(progress, 1);

            async Task CopyTrackAsync(MediaTrack track, FileStream output)
            {
                for (ulong offset = 0; offset < track.Size;)
                {
                    CheckExportLifetime(media, cancellationToken);
                    ulong size = Math.Min(ExportChunkSize, track.Size - offset);
                    byte[] bytes = await FetchAsync(track, offset, size, cancellationToken).ConfigureAwait(false);
                    CheckExportLifetime(media, cancellationToken);
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    offset += size;
                    copied += bytes.Length;
                    Report(progress, 0.95 * ((double)copied / option.SourceBytes));
                }
            }
        }
        catch (OperationCanceledException) when (publication != committing && cancellationToken.IsCancellationRequested) { throw; }
        catch (PlaybackException) { throw; }
        catch (Exception)
        {
            if (publication != committing) cancellationToken.ThrowIfCancellationRequested();
            throw new PlaybackException("export_failed", "The media could not be exported.");
        }
        finally
        {
            scratch?.Dispose();
        }
    }

    private void CheckExportLifetime(PlaybackMedia media, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CheckDisposed();
        if (media.ExpiresAt <= exportClock.GetUtcNow())
            throw new PlaybackException("expired", "The public media has expired. Reopen the video before exporting it.");
    }

    private static string ExportDestination(string path, string extension)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) ||
            Path.GetFileName(path).AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new PlaybackException("invalid_destination", "Choose a full file path with the selected format's extension.");
        string fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) || Directory.Exists(fullPath)) throw MediaExportStaging.DestinationExists();
        if (!Directory.Exists(Path.GetDirectoryName(fullPath)))
            throw new PlaybackException("invalid_destination", "The export folder does not exist.");
        return fullPath;
    }



    private static void Persist(Action write)
    {
        try { write(); }
        catch (Exception e) when (DownloadJournal.IsStorageError(e))
        {
            throw new PlaybackException("export_journal_failed", "The local download record could not be saved safely.");
        }
    }

    private static void Report(IProgress<double>? progress, double value)
    {
        // An observer failure cannot change the outcome of an already published file.
        try { progress?.Report(value); } catch (Exception) { }
    }


}