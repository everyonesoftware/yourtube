# YourTube.Playback

Credential-isolated public VOD resolver for the native Windows adaptive-media adapter. The project targets .NET 10 and pins YoutubeExplode 6.6.3. It does not consume the account client's tokens, cookies, credentials or handler.

## Media boundary

- Genuine metadata comes from `Videos.GetAsync`; separate video/audio streams come from `Videos.Streams.GetManifestAsync`.
- Before YoutubeExplode probes candidate sizes, the public player JSON is filtered in memory to remove known incompatible formats. Unknown metadata is left for the library to handle. The resolver still applies its complete compatibility checks and validates the retained media bytes and indexes.
- MP4 H.264 (`avc1`) representations through 1080p / 1920 pixels wide and one default-language AAC-LC (`mp4a.40.2`) audio representation are eligible. No 4K, WebM, AV1, DRM, live playback, private/paid-video support, captions or inferred statistics are advertised.
- Each candidate requires a complete unencrypted initialization and usable `sidx` in the first 1 MiB. The parser handles 32/64-bit box sizes and version 0/1 indexes, validates direct references against the source size, and carries index timing into DASH. Missing, malformed, encrypted, hierarchical or oversized indexes are unsupported, never guessed. At most 32 eligible video representations and one audio representation are accepted; exceeding this explicit bound fails.
- The UTF-8 static on-demand DASH manifest has real initialization/index ranges, codec/size/bitrate attributes, and separate audio/video adaptation sets. It contains signed URLs **in memory only**. Do not persist or log `Manifest`, URLs, provider responses, or exception details from external libraries.

## Reachability concurrency

Set `PlaybackResolver.MaxConcurrentStreamChecks` to an integer from 1 to 16. The default, 1, keeps serial discovery. Each resolution snapshots its setting at startup. For unambiguous direct CDN URLs with known lengths, a fixed window overlaps the pinned library's exact tail probes, then returns their responses to the library in request order. Missing lengths (HEAD fallback), ciphered URLs, external DASH manifests, and other ambiguous responses stay on the library's original path. The option does not parallelize DASH index reads or media downloads, and an overlap test does not establish a live playback speedup.

## Resource boundary

`ReadResourceAsync` accepts only exact original resource identities registered to that same media object by that same resolver. Publicly constructing a `PlaybackMedia`, copying a manifest, changing query parameters, or using another resolver cannot grant ownership. Ownership uses weak keys and is cleared on disposal.

The default transport disables automatic redirects, cookies, default credentials and decompression. YouTube requests are restricted to `youtube.com` / `www.youtube.com`; CDN requests and every manually followed redirect require HTTPS, default port, no userinfo/fragment, and a true `.googlevideo.com` suffix. CDN headers are rebuilt, never copied from account traffic. GET/HEAD validity probes are supported; redirect chains permit at most four follows. The injected-handler constructor is for tests: supplied handlers must not auto-redirect or add credentials.

Every redirect hop must retain the initiating request's path and all non-routing query parameters, including any query `range` (or its absence). The method and HTTP Range header are preserved. CDN host changes, query reordering and changes to explicitly allowed routing fields are permitted; ambiguous query names and unknown parameter changes fail closed. A future CDN route that rewrites the resource path, changes the range encoding, or uses unrecognized routing fields may be rejected even if the provider intended to serve the same bytes.

Header requests are capped at 1 MiB; every read and every buffered response is capped at 16 MiB. Empty, overflowing, out-of-resource and oversized ranges fail. Googlevideo reads use an internally generated **query range only**, not an additional HTTP Range header: combining both changes the CDN's resource-length semantics. HTTP 200 sliced responses require the exact byte count and no Content-Range; HTTP 206 requires exact original offsets and total source length. No unbounded whole-file fallback is attempted.

Expiry is the earliest included stream's provider `expire` timestamp minus 60 seconds. Expired reads fail with `expired`; callers can explicitly re-resolve. There is no disk cache, browser fallback, background retry loop or fabricated source. Exceptions are fixed safe messages without provider inner exceptions. The application's generation/cancellation logic and Windows adaptive-source runtime acceptance remain separate responsibilities.

## Optional storyboard previews

`GetStoryboardAsync` returns metadata already captured from the bounded, credential-free player JSON for a resolver-owned playback catalog. This is best-effort and adds no requests to startup or discovery. Missing/unsupported specifications return null; the resolver does not request a separate watch page to find previews. `Storyboard.FrameAt(seconds)` returns a sheet index and grid coordinates for the highest-resolution usable timed level. No signature or image URL is exposed.

`GetStoryboardSheetAsync` is an explicit in-memory read of one resolver-owned sheet. It uses exact HTTPS `i.ytimg.com` storyboard paths bound to the video, no cookies or credentials, no redirects, a five-second budget, at most two in-flight reads per resolver and a 2 MiB JPEG limit. Response type, JPEG markers and pixel dimensions are checked; native image decoding/painting is the caller's responsibility. Unavailable results return null; caller cancellation propagates. Keep signed data and images out of logs and persistent storage.

## Verification

```sh
dotnet test apps/windows/YourTube.Playback.Tests -c Release
```

Default tests are offline synthetic fixtures, including actual pinned-library metadata/stream/HEAD/tail-probe/redirect traversal. The public proof is explicitly skipped unless opted in:

```sh
YOURTUBE_PUBLIC_PLAYBACK_PROOF=1 dotnet test apps/windows/YourTube.Playback.Tests -c Release --filter FullyQualifiedName~PublicProof --logger 'console;verbosity=detailed'
```

Requires `ffmpeg` on PATH (or `YOURTUBE_FFMPEG` pointing to it) and public network access. It resolves the three explicit test IDs, reads every representation's nonzero index through production resource transport, passes the **unchanged generated MPD via stdin** to FFmpeg, requires audio and video maps, and checks actual advancing decoded frames/time. FFmpeg fetches media using its own public HTTPS transport; that proves DASH-reader compatibility, not native downloader integration. The manifest, signed URLs and media are never saved; provider/reader diagnostics are never emitted, `FFREPORT` is removed, and only fixed status, public test IDs and numeric results are printed. Linux decoding is not Windows MediaPlayer/AMS runtime acceptance.
