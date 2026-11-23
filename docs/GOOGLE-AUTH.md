# Experimental Google / YouTube sign-in

## Status and boundaries

This is a native-client proof of concept using SmartTube-style TV device authorization and private YouTube endpoints. It is **not** an official YouTube Data API integration, a Google-approved desktop OAuth client, or a supported watch-history export API. Google may reject the client or change the endpoints without notice. There is no extension, password collection, browser-cookie import, or simulated history.

Pinned source inspected locally: `yuliskov/MediaServiceCore` at `c1d202fc21bd97d800a5f92d058cd8f5fbbd65d8`. See `THIRD-PARTY-NOTICES.md` for attribution and the MIT license. The frozen application API is in `POC-CONTRACT.md`.

**User-authenticated acceptance is pending.** Live issuance and an unauthenticated pending token response were observed; no user authorized this agent, and no live accounts, refresh token, or signed-in history were obtained. Deterministic unit-test account/video data is explicitly synthetic and is not live evidence.

## Implemented protocol

1. Request `https://www.youtube.com/tv` with the pinned upstream Fire TV/Cobalt user agent.
2. Find the public base script (HTML `base-js`, Cobalt `m=base`, or Cobalt `base-js` assignment). Reject non-HTTPS/off-origin script locations. Extract the **first** paired client ID/secret pattern used by upstream. Cache discovery only in the client instance. Extract TV client version/visitor data when present; use the pinned upstream nonsecret TV version as fallback.
3. POST JSON to `https://www.youtube.com/o/oauth2/device/code` with runtime client ID, caller device UUID, `model_name=ytlr::`, and the upstream YouTube scopes. Validate required fields, positive lifetime/interval and an allowlisted HTTPS Google/YouTube activation URI.
4. Poll `https://www.youtube.com/o/oauth2/token` using the upstream `code` field and device grant URI. Wait the advertised interval before the first and subsequent polls; continue on pending; permanently add five seconds on `slow_down`; stop on expiry, denial, failure or cancellation. Expired authorizations do not trigger another token request.
5. Refresh explicitly using the refresh-token grant. Preserve the old refresh token when the response omits one; accept rotation when supplied. Reject malformed token responses and unsupported token types.
6. Use per-request bearer headers for `youtubei/v1/account/accounts_list` with owner/brand masks. Read identity names/bylines and the **first** `serviceEndpoint.selectActiveIdentityEndpoint.supportedTokens[*].pageIdToken.pageId`, matching pinned `AccountInt.java`. An `accountStateToken.obfuscatedGaiaId` is not a page ID; identities without a page token retain a null page ID. Skip disabled identities.
7. POST `FEhistory` to `youtubei/v1/browse` using the TV context and `Referer: https://www.youtube.com/tv` for initial and continuation requests (`BrowseApi.kt`). The active `BrowseApiHelper` → `PostDataHelper` → `AppClient.TV.browseTemplate` path supplies Cobalt browser name/version, `context.user.enableSafetyMode=false`, `lockedSafetyMode=false`, and top-level `racyCheckOk=true` / `contentCheckOk=true`; these fixed upstream values are preserved, not inferred from the separate Innertube model. This establishes request parity, not proof that the live service requires every field. Selected identities use `X-Goog-PageId` plus `context.user.onBehalfOfUser`. Continuation requests carry the opaque continuation instead of restarting `FEhistory`. Support grid/video, TV tile, modern lockup and Shorts cards, plus legacy `nextContinuationData` and command-token continuations. Skip unselected tabs, playlist tiles, missing and invalid video IDs. Preserve card ordering; clamp progress to 0–100.

8. `GetSubscriptionsAsync` uses the same bearer/session/selected page ID and TV browse envelope with `browseId=FEsubscriptions`. It reads the first TV secondary-navigation section's channel tabs and the aggregate subscription tab's content, never unselected channel videos or response headers. Video continuation requests omit `browseId`; only the opaque cursor is sent. The pinned three-item-row heuristic excludes long Most relevant/Shorts shelves when chronological rows exist. No upload timestamps, chronological completeness, guide fallback, or subscription mutation API is invented. Details and outstanding acceptance: `SUBSCRIPTIONS.md`.

For History continuation requests, a recognized `continuationContents.gridContinuation` takes precedence over top-level browse `contents` when both are present. Cards and the next cursor come from the same grid. The initial History request and other feeds retain their existing selection order.

Browse continuation parsing also recognizes body-slot reload commands in action/endpoint arrays. It skips header reloads and keeps the cards and next cursor in the selected body. Public upstream evidence for this command form comes from a channel-sorting fixture, not a signed-in History response; the History replay test uses synthetic cards.

These are video cards, **not timestamped watch events**. Duplicates/order reflect the provider response; the core does not invent timestamps or deduplicate watch events. Unknown top-level schemas fail rather than silently claiming empty history; known empty envelopes use a generic message, not raw provider text.

## Secret handling and caller requirements

- Core has no logging, persistent storage, global mutable state, background refresh, or browser launch. Caller owns and configures `HttpClient`; use a finite timeout and `AllowAutoRedirect = false` for the POC so POST credentials cannot follow redirects. Do not attach request/response-body logging handlers or default bearer headers to this client.
- Public runtime TV credentials are still treated as sensitive: do not copy them into source, diagnostics or tickets. Never log device/user codes, access/refresh tokens, authenticated response bodies or arbitrary exception details.
- Secret-bearing models override `ToString()` with redacted text. Provider errors, malformed JSON, network failures and timeouts become stable, safe `YouTubeException` categories without provider bodies or inner exceptions. Explicit cancellation remains `OperationCanceledException`.
- Native UI owns secure refresh-token storage, refresh before expiry, local device UUID persistence, account switching, cancellation/stale-result rejection and sign-out cleanup. Access tokens and history stay in memory. User-visible errors use the safe exception `Message`, never raw HTTP responses.
- Activation URIs are HTTPS host-allowlisted in core. The UI must revalidate before shell launch. Open video URLs only after explicit user action with canonical YouTube URLs and validated video IDs.

## Run the safe live probe

From repository root, with .NET 10 SDK:

```sh
dotnet test apps/windows/YourTube.Core.Tests --nologo -c Release
dotnet run --project apps/windows/YourTube.Probe -c Release
```

Default mode is noninteractive: one issuance, then polling until the first pending response. It does not print activation codes, client credentials, tokens, names, titles, video IDs or response bodies. It does not open a browser or persist anything. A two-minute overall deadline bounds default execution. HTTP failure has nonzero exit; cancellation returns 130.

On the Linux verification environment prepend:

```sh
export PATH=/opt/data/tools/dotnet:$PATH
export LD_LIBRARY_PATH=/opt/data/tools/icu/usr/lib/x86_64-linux-gnu
```

For the **user to run locally**, not in an agent tool or shared transcript:

```sh
dotnet run --project apps/windows/YourTube.Probe -c Release -- --signin
```

This requires a local, non-redirected input/error terminal. Only this opt-in mode prints the user activation code to local stderr and opens the allowlisted activation page. It never prints the device code, credentials or tokens. After approval, stdout contains only authorization status, account count, page/card counts and request count. It reads up to ten pages per account, stopping on absent/repeated continuation tokens. It does not persist tokens/history; Ctrl+C cancels. Do not paste the activation code or console capture into chat. For full visual history use the native app.

### Read-only test-account lookup batch

Use a separate Google account with a single YouTube identity. In a local, interactive PowerShell terminal run `dotnet run --project apps/windows/YourTube.Probe -c Release -- --watch-lookups jNQXAC9IVRw` (or `YourTube.Probe.exe --watch-lookups jNQXAC9IVRw` from a standalone publish). Supply one to eight public video IDs in one batch. Approve the device authorization yourself in Google’s browser page. Do not share the code or a capture of the terminal.

This mode authorizes once per batch and uses the signed-in baseline and TV fallback player lookups without opening videos, fetching History, or sending watch events. It accepts exactly one returned YouTube identity; credentials stay in process memory and are discarded when it exits. Share only the `case=` or `probe_error=` lines, not the activation text. This does not grant a remote agent access to the session. To end authorization at Google, revoke the device authorization from the test account.

The standalone `--watch-context` mode adds a third lookup, `tv_with_context`. It reads a signature timestamp from the public watch page’s same-origin player script without authentication and adds a SmartTube-style playback context to the TV control request. If the timestamp cannot be found, the third lookup is skipped rather than guessed. This tests the context block, not byte-for-byte parity with SmartTube: it omits ad, reload and feed click-tracking fields. Like `--watch-lookups`, it does not play the video or write History.

## Verification evidence

Verified on November 20, 2026, in WSL with SDK `10.0.404`:

- Behavioral RED→GREEN cycles were run for bootstrap/issuance, safe response failures, polling/pending, `slow_down`, expiry, refresh preservation/rotation, accounts, history cards, pagination, TV/modern cards, empty/error schemas, input validation, embedded authentication failures and probe behavior. Initial missing behavior failed at runtime against compiling API seams, not missing-symbol compilation errors.
- Final Release test totals after upstream parity corrections: **74 passed, 0 failed, 0 skipped**. Account regression first failed by returning fixture Gaia IDs instead of page tokens; the Referer regressions failed **2/2** and active TV browse envelope regressions failed **6/6** before implementation, then all passed. Synthetic fixtures cover owner/brand page-token selection, first-token ordering, no-page-token identities, unrelated-token rejection, and propagation into history headers/context. The suite includes deterministic fake HTTP responses, a controllable clock/delay seam, security/error regressions, cancellation, malformed responses and probe privacy assertions. No live authenticated access was attempted for these corrections.
- Live default probe succeeded: TV HTML + public script + device authorization + pending token response = **4 HTTP requests**, exit **0**. No secrets were printed or stored.
- Release core/probe build: **0 warnings, 0 errors**. The test runner emitted a non-build diagnostic that `VSTEST_ARTIFACTS_DIRECTORY` was unset; no test failures resulted.
- Redirected `--signin` was exercised and rejected before issuance with exit **2**.

Exact sanitized final live output:

```text
device_authorization=issued
token_poll=pending
http_requests=4
live_probe_exit=0
```

`pending` means the HTTP observer parsed the provider's `authorization_pending` error; it is not an assumed success. No authenticated live output was substituted.

## Remaining limitations

Actual user approval, real owner/brand account selection, token refresh/revocation and signed-in history/pagination still require live user acceptance. Tests establish protocol behavior, not Google approval or universal schema compatibility. Private response variants can change; new renderer/account shapes may require updated parsers. Bootstrap credentials are cached for the lifetime of one client; create a new client after discovery/client rejection. Concurrent bootstrap calls can duplicate public discovery requests. The core has no retry policy for network/5xx/rate-limit errors and no automatic refresh; callers control retries and session lifecycle. History may be empty, paused, disabled or incomplete; no guarantee of complete account history or watch-event timestamps is made.
