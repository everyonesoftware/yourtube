# Windows history proof-of-concept contract

Scope: native Windows only for this vertical slice. Preserve all existing mockups and the longer-term platform targets. No simulated history or embedded Google password input. SmartTube-style device activation is an unsupported TV-client experiment, not an official YouTube API integration. End-to-end account access requires Dan to authorize the app himself.

Pinned reference: yuliskov/MediaServiceCore c1d202fc21bd97d800a5f92d058cd8f5fbbd65d8 (MIT). Port HTTP behavior only; retain upstream license attribution.

Namespace YourTube.Core, target net10.0, no third-party runtime dependencies. The UI project references ../YourTube.Core/YourTube.Core.csproj.

Public API (exact):
- sealed class YouTubeClient(HttpClient http)
- Task<DeviceAuthorization> BeginSignInAsync(string deviceId, CancellationToken cancellationToken = default)
- Task<YouTubeSession> CompleteSignInAsync(DeviceAuthorization authorization, CancellationToken cancellationToken = default)
- Task<YouTubeSession> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default)
- Task<IReadOnlyList<YouTubeAccount>> GetAccountsAsync(YouTubeSession session, CancellationToken cancellationToken = default)
- Task<HistoryPage> GetHistoryAsync(YouTubeSession session, YouTubeAccount account, string? continuation = null, CancellationToken cancellationToken = default)
- Task<SubscriptionsPage> GetSubscriptionsAsync(YouTubeSession session, YouTubeAccount account, string? continuation = null, CancellationToken cancellationToken = default)

Subscriptions models: SubscribedChannel record (Name:string, HasNewContent:bool); SubscriptionsPage record (Channels:IReadOnlyList<SubscribedChannel>, Videos:IReadOnlyList<HistoryVideo>, Continuation:string?, Message:string?). An initial request loads both lists from `FEsubscriptions`; subsequent opaque cursors page the videos only, retaining the initial channel snapshot in UI memory. TV channel tabs can all have browseId `FEsubscriptions`, not stable UC channel IDs; do not derive membership from video bylines or deduplicate channel names. The upstream TV channel-tab list exposes no independent continuation; do not invent a channel-list cursor or completeness guarantee. See `SUBSCRIPTIONS.md`.

Models: DeviceAuthorization (UserCode:string, VerificationUri:Uri, ExpiresAt:DateTimeOffset, IntervalSeconds:int, DeviceCode:string); YouTubeSession (AccessToken:string, RefreshToken:string, ExpiresAt:DateTimeOffset); YouTubeAccount record (Name:string, Handle:string?, PageId:string?); HistoryVideo record (VideoId:string, Title:string, Channel:string, Duration:string, PercentWatched:int?); HistoryPage record (Videos:IReadOnlyList<HistoryVideo>, Continuation:string?, Message:string?). Use positional record constructors for non-secret models; for secret models provide positional-argument constructors in the stated order and properties; override ToString with redacted output, never generated record ToString. No raw response/exception logging.

YouTubeException : Exception with Code:string and safe Message, constructor (string code, string message). The UI renders Message, never exception.ToString(). Recognized authentication failures use code auth_required. Other safe codes allowed. Caller owns HttpClient, sets timeout; library owns no global state. CompleteSignIn honors provider polling interval and expiry, cancellation and slow_down. Client credentials are extracted at runtime from public TV JavaScript, not committed. No token, code, client-secret or authenticated response logging.

Token refresh is explicit through RefreshSessionAsync. UI refreshes before account/history calls if ExpiresAt is within 60 seconds, writes refresh token into PasswordVault, keeps access token in memory, clears displayed history/account/session on sign-out and cancellation, and rejects stale request completions after sign-out/account switch. Persist device UUID as nonsecret data, do not write history to disk. Verification URI must be HTTPS Google/YouTube allowlisted before shell launch; never send a device code via chat.

Native shell: WinUI runtime embedded XAML, Music-style extended native titlebar, device-code panel, browser activation action, cancel, native account selector, refresh/load more, virtualized ListView, sign-out and status/errors. Do not auto-open video URLs except explicit user activation, only canonical https://www.youtube.com/watch?v= validated video IDs. No timestamp claims: the TV endpoint provides video cards, not an export of watch events.

## Native Theater playback extension

The selected player design is **Theater / Native sage**: a full-width native video stage, metadata below, and a horizontal next-video shelf drawn from the currently loaded History or Subscriptions feed. Feed items are not personalized recommendations; do not invent view counts, upload ages, subscription badges, or video descriptions. Selecting a video opens the player rather than automatically launching a browser. An unavailable video can offer explicit Retry and canonical Open in YouTube actions.

Playback is a separate `YourTube.Playback` component using pinned YoutubeExplode 6.6.3 for public, non-DRM video resolution. It must not receive device-authorization sessions, refresh tokens, or imported browser cookies. Adaptive H.264/AAC audio and video tracks are joined through native Windows DASH playback, not a web wrapper or two independent unsynchronized players. Only qualities present in the source may be offered; this slice does not promise 4K, live, private, rental, or age-restricted playback.

Keep signed URLs/manifests and fetched media in memory. Media resources must belong to the resolved source, use HTTPS/default port/no userinfo, and remain on boundary-validated Googlevideo hosts through a bounded manual redirect chain. Use credential-free media requests and bounded, validated byte ranges. Never log signed URLs, raw resolver responses, or provider exceptions. Account changes, navigation, Settings, sign-out and close invalidate pending resolution and dispose the native source/player; a delayed completion cannot restart playback or restore private metadata.

Publication requires deterministic tests, native Windows installer smoke and package integrity checks. Live public-video playback through the shared production adapter is advisory and never blocks publication. Stream retrieval/FFmpeg decoding or Linux compilation alone does not prove native playback. Owner-authenticated feed acceptance remains separate.

Builds: Windows native self-contained net10.0-windows10.0.19041.0, win-x64. Pin Microsoft.WindowsAppSDK.WinUI 1.8.260803003 and Velopack 1.2.161 to match Music. Linux compile override is verification only, never a distributable. One main CI workflow with validation and Windows build/package/smoke. Version 0.1.1 for initial POC. Do not claim complete until actual signed-in history is observed; report user-auth acceptance pending separately from compiled/tested implementation.
