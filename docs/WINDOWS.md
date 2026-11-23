# Windows proof-of-concept build and acceptance

## Build on Windows

Requires Windows 10 version 2004 or later (x64), .NET SDK pinned in `global.json`, and the Windows SDK/native build prerequisites provided on `windows-2022` GitHub runners.

```powershell
dotnet test apps/windows/YourTube.Core.Tests/YourTube.Core.Tests.csproj -c Release
dotnet test apps/windows/YourTube.Update.Tests/YourTube.Update.Tests.csproj -c Release
dotnet test apps/windows/YourTube.Playback.Tests/YourTube.Playback.Tests.csproj -c Release
node --test tests/*.test.mjs
dotnet run --project apps/windows/validation/YourTube.App.Validation.csproj -c Release
node --test apps/windows/YourTube.Update.Tests/panel-contracts.test.mjs
./scripts/test-windows-playback.ps1 -SourceSha (git rev-parse HEAD)
./scripts/build-windows.ps1 -Version 0.1.1 -SourceSha (git rev-parse HEAD)
./scripts/test-windows-native.ps1 -InstallerPath artifacts/windows/releases/YourTubeWindows-win-x64-Setup.exe -EvidenceDirectory artifacts/native-smoke
```

The packaging script requires clean committed inputs and an empty output directory. It restores pinned Velopack 1.2.161, builds a native self-contained WinUI application, checks native/.NET/PRI payloads, then generates the Setup installer, full nupkg, feed, checksums and exact-source manifest. It refuses Linux packaging. The copied/adapted Music installer smoke requires a disposable interactive account, verifies a responsive visible installed window, retains a screenshot, closes, uninstalls and independently checks cleanup. It never signs in to Google.

## Compile-only on Linux

```sh
export PATH=/opt/data/tools/dotnet:$PATH
export LD_LIBRARY_PATH=/opt/data/tools/icu/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}
dotnet build apps/windows/YourTube.App/YourTube.App.csproj -c Release -p:OutputType=Library -p:WindowsAppSDKSelfContained=false -p:PrepareForRunDependsOn=CopyFilesToOutputDirectory
```

This checks real C#/WinUI references only. **Do not distribute this output:** it does not establish native launch, runtime XAML, Credential Locker, install/update or Google consent.

## Account-owner acceptance (not automated)

1. Open installed YourTube. Confirm native sign-in UI, titlebar dragging/caption controls, keyboard and DPI behavior.
2. Choose Sign in. The app shows a device code. Explicitly open the activation page in your own browser; never paste the code/password/token into chat. Review the Google device identity and permissions; cancel if unacceptable.
3. Complete authorization. Confirm your account/channel appears and select the intended YouTube identity, including a brand account if applicable.
4. Confirm actual recent watched titles against YouTube itself. Exercise Refresh and Load more. Treat empty or unavailable data as a diagnostic result, not successful history retrieval. The feed consists of video cards/resume state, not timestamped watch events.
5. In a build containing the subscriptions extension, choose **Subscriptions**. Compare the subscribed channel names and latest video cards with the same YouTube identity; exercise Refresh (both lists) and Load more (videos). Empty/unavailable results are not authenticated acceptance. Switch accounts, return to History and enter Settings during requests: old channels, videos and cursors must not reappear. Verify native recycled channel/video rows, screen-reader labels, pointer and keyboard operation, narrow-window and DPI layout. Channels are a read-only list, not channel navigation. The TV tab snapshot has no independent channel-pagination cursor; completeness is not guaranteed.
6. In **Library**, check that History and Subscriptions wrap into fewer columns as the window narrows, and that History shows progress only when supplied. Expand **Subscribed channels**, then use both pointer and keyboard to open a card in Theater. Pick a different video from **Up next** and verify Back returns focus to the original card. Scroll a long feed and confirm late thumbnails never appear on another card, including after switching accounts. Thumbnails are requested from YouTube's public image host without account credentials; an unavailable image leaves a playable placeholder.
7. Switch to another YouTube identity, then close and reopen. Credential Locker session restoration should refresh authorization without exposing tokens, select the last identity and load its Home feed. If the identity is no longer available, the app should ask you to choose an account instead. Exercise cancellation, declined/expired codes and offline recovery.
8. Switch identity and sign out during a delayed request: old results must not reappear. Sign out and reopen: displayed history and saved credentials must be gone. Google device access can also be revoked in your Google account.
9. Open an installed build and confirm the app stays usable while it checks for updates in the background. With a newer Windows version available, look for a dot on **Settings**; without one, the dot should stay hidden. A failed startup check should also leave the dot hidden and allow a manual **Check for updates** in Settings. Verify explicit Download/progress → Restart/install with credential survival separately. No automatic download/apply or downgrade.
10. From an installed build with boxed commit links and a newer offer, confirm **What’s new** starts collapsed. Use pointer and keyboard to open it and its nested **Commit details**. Verify the count, grouped improvements/fixes, and original commit subjects beside seven-character boxes. For a skipped release, verify the single `base7…head7` box starts at the installed source, not the preceding release. Opening or reopening either disclosure should reuse the data loaded during the check and not open the browser. Use pointer and keyboard to activate a commit box and the range box; verify they open the full-SHA YourTube commit and comparison pages, respectively. Check visible focus and contrast in dark, light and high-contrast themes. Check that missing notes permit download and that the same offer retains its notes through download, verification and Ready to install.

## Publication and verification

### Home acceptance (first Windows implementation)

After sign-in, select a YouTube identity to load **Home**. Compare the Recommended and Shorts shelves with that identity’s YouTube TV Home feed when they’re available. Subsequent rows come from the same YouTube response and **Load more** requests the next set of shelves, using SmartTube’s TV browse flow as the reference. If YouTube does not supply Shorts, YourTube does not make up a shelf or fetch a separate Shorts feed. The page shows video cards, not channel or playlist tiles.

In an installed Windows build, scroll both axes with mouse and keyboard, load more at the bottom, open a card and check that Back returns focus to it. Verify that Refresh replaces the rows and that switching account, opening Settings, or signing out during a request does not restore rows from the old view. A fixture or compile check is not proof of live personalized results or native scrolling and focus.

### Windows publication

One `ci.yml` workflow: validation/selection, one Windows test/build/package/native-smoke job, and serialized publication/table refresh. Only native/transitive-input changes since the most recent verified Windows publication rebuild Windows. Native code is not rebuilt for mockup/docs/control-plane-only changes. Unknown inputs fail closed and select Windows.

Draft release assets are downloaded and byte-checked before publication. Source/version/package/channel identities are enforced. Setup, full nupkg, feed and checksums remain at their original version-specific URLs. README and release Current downloads tables are generated from one independently verified Windows inventory. The README writer uses the Contents blob-SHA precondition; release body updates are GET/PATCH and cannot be atomic with simultaneous human edits.

The publisher embeds cumulative Windows changelog data in the existing `yourtube-windows.json` manifest. Verified Windows publications establish the release boundaries; full Git history supplies each intervening commit. The schema-1 `changelog` includes the offered `head` (`version`, `sha`) and ordered `releases` (`version`, `sha`, `count`, `commits`). Here `count` is the Git commit count, not the CI run number in the version. Each commit retains its `sha` and original first-line `subject`; nullable `kind` (`improvement` or `fix`) and `text` supply the user-facing groups. Docs and CI-only commits remain in details. The first verified publication is the baseline and claims no earlier history.

The client loads notes for the selected offer during the update check and uses the installed executable’s embedded source SHA and Velopack version to select the exact range. It does not use GitHub release descriptions or reconstruct commit history. Historical manifests are unchanged; loading, retrying or failing to load notes does not block downloading an otherwise valid update. Installations running the older panel do not gain the display until they upgrade to a changelog-capable build. Like Music, the release producer rejects incomplete history rather than publishing a partial range as complete.

Outstanding acceptance must be reported honestly: fixture-backed protocol tests are not completed Google authorization or authenticated history; source/XML checks and Linux compilation are not native runtime tests; native unauthenticated smoke is not Credential Locker or history acceptance; a published first installer is not a tested in-app upgrade.

## Theater / Native sage playback validation

The native player uses a credential-isolated YoutubeExplode resolver and the production `NativePlaybackSource` DASH adapter. Its scope is public non-DRM VOD with usable native H.264/AAC streams; quality availability comes from the source. Account authorization is used for browsing, not passed to the stream resolver or CDN. Unavailable/private/paid/live videos must fail honestly instead of showing sample content or silently opening a browser.

In Theater or fullscreen, click the video itself to pause or resume. The corresponding icon briefly fades in and out at the center of the video. The controls along the bottom still handle play/pause, seeking, volume and fullscreen. To choose another video, use the **Up next** shelf below the player in Theater. Open the gear for **Quality** and **Playback speed**. **F11** toggles fullscreen and **Esc** exits it when the menu is closed. Auto starts each video and uses the compatible adaptive stream set. A manual choice opens a DASH source restricted to that resolution and preserves playback state near the current position; if the replacement cannot open or seek, the previous choice stays active. This selector doesn't extend the existing codec or resolution limits.

**Settings → Playback → Auto startup quality** defaults to 720p and is saved on this device. Choose a different resolution or **Let Windows decide** to send no bitrate hint. If the requested resolution is unavailable, Auto hints the closest lower compatible rendition when possible. This is a starting preference, not a cap or a manual quality lock.

**Settings → Playback → Update YouTube History** starts on each normal launch and can be turned off for that session. To test, select the intended account, ensure its YouTube History is on, and choose a public video in Subscriptions that isn't already in History. Let it play in YourTube for at least 15 seconds without opening the video on youtube.com. Refresh History on youtube.com under the same identity and look for the title. Repeat with YouTube History paused and a different previously unwatched video; it should stay out of History. Restore your original History setting afterward. The Settings status reports request outcome, not whether History changed. No resume updates or background retries are sent. Playback does not depend on the request succeeding.

After native playback starts advancing, this experiment looks up the public player script's signature timestamp without credentials. It uses the timestamp in the signed-in TV fallback lookup for the selected identity. If that context is unavailable or the lookup does not return playable, matching tracking data, YourTube sends no watch-start request. Public media resolution and delivery remain separate from the History request.

For the **read-only History lookup comparison**, close YourTube, set `YOURTUBE_WATCH_LOOKUP_COMPARE=1` in the PowerShell session used to launch the diagnostic-capable executable, then enable **Settings → Playback → Compare YouTube History lookups (diagnostic)**. Play a public video from the selected account’s feed; let it run for about 15 seconds before returning to Settings to read **Baseline** and **TV fallback**. The fallback uses the pinned SmartTube `TV_DOWNGRADED` client version and user agent in the player body and headers, a TV referrer, a player-specific client context, and the same authenticated account and video as the baseline. It does not add signature-timestamp playback context, contact tracking endpoints, or write History. Only sanitized statuses and tracking usability are displayed. Leave the variable unset for normal behavior. Version 0.1.29 compared headers only and does not contain the fallback.

For a timing capture, close YourTube, set the `YOURTUBE_PLAYBACK_TIMING` environment variable to `1` in the PowerShell session used to launch the installed app executable, then reproduce the issue. Share only `%LOCALAPPDATA%\YourTube\playback-timing.log`. The bounded local file contains UTC timestamps, per-attempt elapsed times, playback progress (not a first-frame measurement), bitrate changes and buffering events. It does not include video IDs, titles, URLs, account data or credentials. Nothing is uploaded automatically.

`scripts/test-windows-playback.ps1` builds a separate native validation executable (not an installer asset), using the same adapter source as the app. It plays three real public videos, checks actual audio/video tracks, natural video dimensions, advancing position, pause, seek and disposal, and retains foreground desktop screenshots in `artifacts/native-playback/evidence`. It never authorizes a Google account or obtains browser cookies.

Run **Live YouTube playback (advisory)** manually in Actions to check current public playback. Failures remain visible in that workflow, with available stage evidence retained, but it has no release dependency or publishing permissions. The release workflow still requires the deterministic suites, Windows installer smoke and package verification. Historical owner proof is not needed for publication and does not attest to later builds.

Run **Windows fixture smoke** manually in Actions, or run the following from a clean checkout in interactive Windows PowerShell 7. These checks are separate from releases while runner reliability is evaluated. Use new/empty output directories.

```powershell
./scripts/test-windows-playback.ps1 -Fixture -SourceSha (git rev-parse HEAD)
./scripts/test-windows-settings.ps1 -SourceSha (git rev-parse HEAD)
```

Playback uses embedded synthetic H.264/AAC media and runs the production native adapter without YouTube or credentials. Settings drives the real panel with a fixture offer, checks disclosure and keyboard focus, verifies exact commit/comparison URLs without opening a browser, and checks that reopening reuses loaded notes. These checks do not prove live resolution, audible output, actual browser launch, or an in-app upgrade.

Owner acceptance remains distinct: after signing in and loading a real feed, open a video, compare the Theater/sage layout, exercise native pointer/keyboard controls and quality changes, choose another actual feed item, and navigate Back/Settings/account/sign-out while resolving and playing. Check high-DPI/narrow-window layout and audio routing on the owner's machine. The public test corpus does not establish every YouTube video, authenticated private playback, live playback, 4K, or universal codec/network compatibility.
