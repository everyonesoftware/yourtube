# YourTube native UI validation

The application consumes `docs/POC-CONTRACT.md` through a thin adapter over the real `YourTube.Core.YouTubeClient`. All fixture cards, tokens and delayed services are confined to this validation project. Production never uses them.

## Executable checks

From the repository root, with .NET 10 available:

```sh
dotnet run --project apps/windows/validation/YourTube.App.Validation.csproj
```

The runner exercises session restoration, refresh-token persistence, device authorization cancellation, stale history/account/refresh completions, pagination, safe structured errors, transport-error sanitization and close-versus-sign-out behavior. It also parses the actual embedded XAML, matches typed control lookups, requires native action wiring and checks self-contained publish defaults. No credentials or network are needed.

The runner also exercises subscriptions as both channel and video data: account-bound navigation, delayed old-account/sign-out/Settings results, same-session expiry refresh, cursor cycles, append-versus-refresh replacement, retryable errors and authorization revocation. Subscription channels stay in memory during video pagination; changing account/view clears them. The XML contracts require the real native channel ListView, browse actions and core adapter forwarding. These are state/source tests, not native pointer/keyboard acceptance.

A Linux **compile-only** gate against the real pinned WinUI/Velopack packages:

```sh
dotnet build apps/windows/YourTube.App/YourTube.App.csproj -c Release \
  -p:OutputType=Library -p:WindowsAppSDKSelfContained=false \
  -p:SelfContained=false -p:PrepareForRunDependsOn=CopyFilesToOutputDirectory \
  -warnaserror

dotnet run --project apps/windows/validation/YourTube.App.Validation.csproj -- \
  "$PWD" "$PWD/apps/windows/YourTube.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/YourTube.dll"
```

The optional second runner argument checks the emitted Windows x64 PE, version `0.1.1.0` and actual `YourTube.App.Shell.xaml` manifest resource. For a CI `-p:Version=0.1.N` build, pass `0.1.N` as the third runner argument; assembly/file versions derive from that property, and Settings reads the running assembly version. The Linux build is a library for verification, **not** a runnable release or distributable. Native defaults remain a self-contained `YourTube.exe`; use the parent's Windows packaging pipeline. The standard native build starts with:

```powershell
dotnet publish apps/windows/YourTube.App/YourTube.App.csproj -c Release -r win-x64
```

## Theater playback state and native adapter

The production `PlaybackController<T>` is source-linked into this runner. Behavioral RED→GREEN checks cover real-feed selection, exclusion of the current video from Up next, Back disposal, eight invalidation paths (account, Settings, view, feed, sign-out, Back, close and reselection), ignored cancellation, late native-resource disposal, hidden Next/Retry, current reference membership, single-item recovery, stale native errors, sanitized failures, and no automatic retry loop. A minimal test-only `IPlaybackResolver` implements the exact real project contract; cancellation after resolution is checked before native source creation, and expired results never attach. No fixture data enters the app.

`NativePlaybackSource.cs` is self-contained and source-linkable in the parent's Windows proof harness:

```csharp
using var source = await NativePlaybackSource.CreateAsync(resolver, media, cancellationToken);
source.Failed += OnSafeNativeFailure;
mediaPlayer.Source = source.Source;
```

It exposes `Source` (`Windows.Media.Core.MediaSource`), `Media`, `HasFailed` and a parameterless `Failed` event. The adapter does **not** own the resolver. The caller detaches/disposes its `MediaPlayer` and disposes the adapter on replacement/close. It builds AMS from the in-memory DASH manifest, handles byte-range requests with a WinRT deferral and `IPlaybackResolver.ReadResourceAsync`, and supplies failure results instead of silently delegating failures to Windows networking. A deny-all `IHttpFilter` blocks native fallback requests as defense in depth. Callback errors carry no provider text. Expiration stops playback and offers explicit Retry; each Retry resolves once and never automatically loops.

Activation inventory: history and subscription cards share `HistoryList` → `OpenVideoAsync`; horizontal Up next verifies the visible queue and then uses the same current-account/feed boundary; Next uses the first remaining current-feed item only; Retry retains a single selected card; Open YouTube is explicit and reuses unchanged `BrowserPolicy`. Back restores the existing browse snapshot and attempts native focus restoration. Account selection, refresh/loading, navigation, Settings, cancellation, sign-out and close invalidate playback. Native system play/pause/volume/scrub/fullscreen/playback-rate controls operate the actual `MediaPlayer`; no quality picker, captions, 4K, views, dates, subscribed-state claims, recommendations or autoplay queue are fabricated.

The Theater stage and metadata/shelf are production `Shell.xaml` + `MainWindow.cs`; they are not recreated in the standalone adapter harness. The shell retains its extended titlebar, account/vault flow and Settings updater.

## Windows acceptance still required

The parent's Windows CI/native proof is mandatory **before publication**. Linux checks below do not establish any native playback acceptance. Public-video backend proof must include actual AMS creation, media opened, both audio/video tracks, nonzero decoded video dimensions, advancing position, seek, source switch and teardown through this same adapter. Also inspect the installed production Theater view (not just the harness) for full-width sage composition, live native controls/playback rate, pointer/Enter/Space queue/card activation, single-item Back/Retry, focus return, resizing/DPI, error recovery and source-expiry recovery. Verify no fallback native HTTP fetch occurs on denied/failed resource requests. Public-video proof does not establish private/paid/restricted playback or authenticated owner history/subscriptions acceptance.

- Launch the native published/installed build; confirm runtime XAML, theme resources, merged PRI, taskbar identity and caption buttons resolve.
- Verify titlebar dragging, active/inactive colors, maximize/restore, keyboard operation, DPI scaling, focus and screen-reader announcements.
- Confirm clean-machine startup stays in restoration until Windows Credential Manager resolves. Retry and sign out after vault failures; verify only this app's resource/user pair is removed.
- Dan must explicitly authorize in the native app. Confirm no browser opens until **Open activation page in browser**, only allowlisted HTTPS activation pages launch, cancellation/expiry works, and no sensitive values appear in logs or chat.
- Observe actual account enumeration and actual signed-in history. Switch accounts while loading; refresh, load more and interrupt requests. Confirm old responses never repopulate history or stored credentials.
- Close/reopen to verify PasswordVault restoration and refresh rotation. Sign out/reopen to verify private state is gone. Closing is not signing out.
- Exercise recycled native ListView rows, pointer/keyboard video activation, invalid-ID rejection and canonical video URLs. History cards are not timestamped watch events.
- Exercise the parent's Settings updater and installed-app lifecycle smoke separately. The shell hosts and disposes `WindowsUpdatesPanel`, with startup auto-apply disabled before WinUI initialization.

Compile/source/state-test success does not establish native launch, credential-vault behavior, installed update acceptance or signed-in account access.
